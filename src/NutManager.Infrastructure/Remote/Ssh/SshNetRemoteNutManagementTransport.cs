using Renci.SshNet;
using Renci.SshNet.Common;
using NutManager.Core.Services;

namespace NutManager.Infrastructure.Remote.Ssh;

public sealed class SshNetRemoteNutManagementTransport : IRemoteNutManagementTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public async Task<RemoteNutConnectionResult> ConnectAsync(RemoteNutConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        RemoteNutHostKeyInfo? receivedHostKey = null;
        SshClient? sshClient = null;
        SftpClient? sftpClient = null;
        try
        {
            sshClient = CreateSshClient(request, hostKey => receivedHostKey = hostKey);
            await ConnectBoundedAsync(sshClient, cancellationToken);
            sftpClient = CreateSftpClient(request, hostKey => receivedHostKey = hostKey);
            await ConnectBoundedAsync(sftpClient, cancellationToken);
            return new RemoteNutConnectionResult(
                RemoteNutConnectionState.Connected,
                new SshNetRemoteNutManagementSession(sshClient, sftpClient),
                receivedHostKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sshClient?.Dispose();
            sftpClient?.Dispose();
            return new RemoteNutConnectionResult(RemoteNutConnectionState.Disconnected, message: "Remote connection was cancelled.");
        }
        catch (TimeoutException)
        {
            sshClient?.Dispose();
            sftpClient?.Dispose();
            return new RemoteNutConnectionResult(RemoteNutConnectionState.Timeout, hostKey: receivedHostKey, message: "Remote connection timed out.");
        }
        catch (SshAuthenticationException)
        {
            sshClient?.Dispose();
            sftpClient?.Dispose();
            return new RemoteNutConnectionResult(RemoteNutConnectionState.AuthenticationFailed, hostKey: receivedHostKey, message: "SSH authentication failed.");
        }
        catch (SshConnectionException)
        {
            sshClient?.Dispose();
            sftpClient?.Dispose();
            var state = receivedHostKey is null
                ? RemoteNutConnectionState.ConnectionFailed
                : string.IsNullOrWhiteSpace(request.TrustedHostKeyFingerprint)
                    ? RemoteNutConnectionState.HostKeyTrustRequired
                    : RemoteNutConnectionState.HostKeyMismatch;
            return new RemoteNutConnectionResult(state, hostKey: receivedHostKey, message: "SSH host key verification failed.");
        }
        catch (Exception)
        {
            sshClient?.Dispose();
            sftpClient?.Dispose();
            return new RemoteNutConnectionResult(RemoteNutConnectionState.ConnectionFailed, hostKey: receivedHostKey, message: "Remote connection could not be established.");
        }
    }

    private static SshClient CreateSshClient(RemoteNutConnectionRequest request, Action<RemoteNutHostKeyInfo> hostKeyReceived)
    {
        var client = new SshClient(CreateConnectionInfo(request));
        client.HostKeyReceived += (_, eventArgs) => ValidateHostKey(request, eventArgs, hostKeyReceived);
        return client;
    }

    private static SftpClient CreateSftpClient(RemoteNutConnectionRequest request, Action<RemoteNutHostKeyInfo> hostKeyReceived)
    {
        var client = new SftpClient(CreateConnectionInfo(request));
        client.HostKeyReceived += (_, eventArgs) => ValidateHostKey(request, eventArgs, hostKeyReceived);
        return client;
    }

    private static ConnectionInfo CreateConnectionInfo(RemoteNutConnectionRequest request)
    {
        AuthenticationMethod authentication = request.Authentication switch
        {
            RemoteNutPasswordAuthentication password => new PasswordAuthenticationMethod(request.Username, new string(password.Password.Span)),
            RemoteNutPrivateKeyAuthentication key when key.Passphrase.IsEmpty => new PrivateKeyAuthenticationMethod(request.Username, new PrivateKeyFile(key.PrivateKeyPath)),
            RemoteNutPrivateKeyAuthentication key => new PrivateKeyAuthenticationMethod(request.Username, new PrivateKeyFile(key.PrivateKeyPath, new string(key.Passphrase.Span))),
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unsupported remote authentication type.")
        };
        return new ConnectionInfo(request.Host, request.Port, request.Username, authentication);
    }

    private static void ValidateHostKey(RemoteNutConnectionRequest request, HostKeyEventArgs eventArgs, Action<RemoteNutHostKeyInfo> hostKeyReceived)
    {
        var fingerprint = SshHostKeyFingerprint.Create(eventArgs.HostKey);
        var hostKey = new RemoteNutHostKeyInfo(request.Host, request.Port, eventArgs.HostKeyName, fingerprint);
        hostKeyReceived(hostKey);
        eventArgs.CanTrust = SshHostKeyFingerprint.Matches(request.TrustedHostKeyFingerprint, eventArgs.HostKey);
    }

    private static async Task ConnectBoundedAsync(BaseClient client, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operationCancellation = CreateOperationToken(cancellationToken, ConnectTimeout);
        try
        {
            await client.ConnectAsync(operationCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Remote connection timed out.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static CancellationTokenSource CreateOperationToken(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }
}

public sealed class SshNetRemoteNutManagementSession : IRemoteNutManagementSession, IRemoteNutWriteIntentSession
{
    private static readonly TimeSpan SftpTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly SshClient _sshClient;
    private readonly SftpClient _sftpClient;
    private readonly HashSet<string> _validatedConfigurationDirectories = new(StringComparer.Ordinal);
    private readonly RemoteSafeWriteCapabilityState _safeWriteCapability = new();
    private bool _disposed;

    public SshNetRemoteNutManagementSession(SshClient sshClient, SftpClient sftpClient)
    {
        _sshClient = sshClient ?? throw new ArgumentNullException(nameof(sshClient));
        _sftpClient = sftpClient ?? throw new ArgumentNullException(nameof(sftpClient));
        _sftpClient.OperationTimeout = SftpTimeout;
        HomeDirectory = _sftpClient.WorkingDirectory;
    }

    public RemoteNutPlatform Platform { get; private set; } = RemoteNutPlatform.Unknown;

    public bool IsSafeWriteCapabilityValidFor(string configurationDirectory) =>
        Platform == RemoteNutPlatform.Windows &&
        TryGetValidatedConfigurationDirectory(configurationDirectory, out var sftpDirectory) &&
        _safeWriteCapability.IsValidFor(sftpDirectory);

    public string HomeDirectory { get; }

    public IRemoteNutConfigurationPathPolicy PathPolicy => SftpRemoteNutConfigurationPathPolicy.Instance;

    public void ApplyWriteIntent(bool canWrite) => _safeWriteCapability.ClearVerification();

    public async Task<RemoteNutDirectoryListing> BrowseDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        var sftpPath = RemotePathMapper.ToSftpPath(directory);
        var entries = await ExecuteSftpAsync(async token =>
        {
            var listed = new List<RemoteNutDirectoryEntry>();
            await foreach (var entry in _sftpClient.ListDirectoryAsync(sftpPath, token))
            {
                if (entry.Name is not "." and not ".." && entry.IsDirectory)
                {
                    listed.Add(new RemoteNutDirectoryEntry(entry.Name, entry.FullName, true, entry.IsSymbolicLink));
                }
            }

            return listed.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
        }, cancellationToken);
        return new RemoteNutDirectoryListing(sftpPath, GetParentPath(sftpPath), entries);
    }

    public async Task<RemoteNutDirectoryValidationResult> ValidateConfigurationDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        var sftpPath = RemotePathMapper.ToSftpPath(directory);
        try
        {
            var present = await ExecuteSftpAsync(async token =>
            {
                var names = new List<string>();
                await foreach (var entry in _sftpClient.ListDirectoryAsync(sftpPath, token))
                {
                    if (!entry.IsDirectory && RemoteNutConfigurationFiles.IsRecognized(entry.Name))
                    {
                        names.Add(entry.Name);
                    }
                }

                return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            }, cancellationToken);
            var result = new RemoteNutDirectoryValidationResult(
                RemoteNutTransportStatus.Success,
                sftpPath,
                present,
                present.Length == 0 ? "No recognized NUT configuration file was found in the selected directory." : null);
            _validatedConfigurationDirectories.Add(sftpPath);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.Cancelled, sftpPath, message: "Remote directory validation was cancelled.");
        }
        catch (SftpPermissionDeniedException)
        {
            return new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.AccessDenied, sftpPath, message: "The selected remote directory cannot be accessed.");
        }
        catch (SftpPathNotFoundException)
        {
            return new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.NotFound, sftpPath, message: "The selected remote directory was not found.");
        }
        catch (TimeoutException)
        {
            return new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.Timeout, sftpPath, message: "Remote directory validation timed out.");
        }
        catch
        {
            return new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.Failed, sftpPath, message: "The selected remote directory could not be validated.");
        }
    }

    public async Task<RemoteNutFileReadResult> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var sftpPath = RemotePathMapper.ToSftpPath(path);
        try
        {
            var bytes = await ExecuteSftpAsync(async token =>
            {
                using var stream = new MemoryStream();
                await _sftpClient.DownloadFileAsync(sftpPath, stream, token);
                return stream.ToArray();
            }, cancellationToken);
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.Success, bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.Cancelled, message: "The remote file operation was cancelled.");
        }
        catch (SftpPathNotFoundException)
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.NotFound, message: "The remote file was not found.");
        }
        catch (SftpPermissionDeniedException)
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.AccessDenied, message: "The remote file cannot be accessed.");
        }
        catch (TimeoutException)
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.Timeout, message: "The remote file operation timed out.");
        }
        catch
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.Failed, message: "The remote file could not be read.");
        }
    }

    public async Task<RemoteNutWriteCapabilityResult> ProbeSafeWriteCapabilityAsync(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_safeWriteCapability.TryBeginProbe())
        {
            return new RemoteNutWriteCapabilityResult(false, Platform, message: "The remote write state is indeterminate. Disconnect and reconnect before probing again.");
        }

        if (!TryGetValidatedConfigurationDirectory(directory, out var sftpDirectory))
        {
            return new RemoteNutWriteCapabilityResult(false, Platform, message: "The remote configuration directory was not validated for this session.");
        }

        if (!await ProbeWindowsPlatformAsync(cancellationToken))
        {
            return new RemoteNutWriteCapabilityResult(false, Platform, message: "Remote configuration writing is available only for Windows servers managed through OpenSSH.");
        }

        var token = Guid.NewGuid().ToString("N");
        var sourceName = $".nutmanager-capability-{token}-source.tmp";
        var candidateName = $".nutmanager-capability-{token}-candidate.tmp";
        var backupName = $".nutmanager-capability-{token}-backup.tmp";
        string? cleanupPath = null;
        RemoteNutWriteCapabilityResult result;
        try
        {
            await WriteNewAsync(RemotePathMapper.Combine(sftpDirectory, sourceName), new byte[] { 0x31 }, cancellationToken);
            await WriteNewAsync(RemotePathMapper.Combine(sftpDirectory, candidateName), new byte[] { 0x32 }, cancellationToken);
            var command = RemoteWindowsCommandBuilder.BuildWindowsCapabilityProbe(sftpDirectory, sourceName, candidateName, backupName);
            var output = await ExecuteWindowsCommandAsync(command, cancellationToken);
            if (!RemoteWindowsCommandBuilder.IsExactSuccessMarker(output.ExitStatus, output.Output, "NUTMANAGER_PROBE_OK"))
            {
                result = new RemoteNutWriteCapabilityResult(false, Platform, message: "The Windows replace capability probe failed.");
            }
            else
            {
                result = new RemoteNutWriteCapabilityResult(true, Platform);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = new RemoteNutWriteCapabilityResult(false, Platform, message: "The remote write capability probe failed.");
        }
        finally
        {
            foreach (var name in new[] { sourceName, candidateName, backupName })
            {
                if (!await DeleteCapabilityProbeTemporaryFileAsync(sftpDirectory, name))
                {
                    cleanupPath ??= RemotePathMapper.Combine(sftpDirectory, name);
                }
            }
        }

        if (cleanupPath is not null)
        {
            return new RemoteNutWriteCapabilityResult(
                false,
                Platform,
                cleanupPath,
                "The remote capability probe cleanup could not be confirmed. Review the remote temporary file before retrying.");
        }

        if (result.IsSupported && !_safeWriteCapability.TryCompleteProbe(sftpDirectory))
        {
            return new RemoteNutWriteCapabilityResult(false, Platform, message: "The remote write state is indeterminate. Disconnect and reconnect before probing again.");
        }

        return result;
    }

    public async Task<RemoteNutFileReadResult> UploadCandidateAsync(RemoteNutCandidateUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RemoteNutConfigurationFiles.IsRecognized(request.TargetFileName) || !RemoteNutGeneratedTemporaryFile.IsValidName(request.TemporaryFileName))
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.InvalidPath, message: "The remote candidate target is invalid.");
        }

        if (!TryGetValidatedConfigurationDirectory(request.ConfigurationDirectory, out var configurationDirectory))
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.InvalidPath, message: "The remote configuration directory was not validated for this session.");
        }

        if (!IsSafeWriteCapabilityValidFor(configurationDirectory))
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.Unsupported, message: "Remote candidate upload is available only after a verified Windows safe-write capability probe.");
        }

        var path = RemotePathMapper.Combine(configurationDirectory, request.TemporaryFileName);
        try
        {
            await WriteNewAsync(path, request.CandidateBytes, cancellationToken);
            return await ReadFileAsync(path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.Cancelled, message: "The remote candidate upload was cancelled.");
        }
        catch (SftpPermissionDeniedException)
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.AccessDenied, message: "The remote candidate file cannot be created.");
        }
        catch (TimeoutException)
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.Timeout, message: "The remote candidate upload timed out.");
        }
        catch
        {
            return new RemoteNutFileReadResult(RemoteNutTransportStatus.Failed, message: "The remote candidate file could not be created.");
        }
    }

    public void InvalidateSafeWriteCapability() => _safeWriteCapability.InvalidateSession();

    public async Task<RemoteNutTemporaryCleanupResult> DeleteGeneratedTemporaryFileAsync(string configurationDirectory, string temporaryFileName, CancellationToken cancellationToken = default)
    {
        if (!RemoteNutGeneratedTemporaryFile.IsValidName(temporaryFileName))
        {
            return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.InvalidPath, "The remote temporary candidate path is invalid.");
        }

        string path;
        try
        {
            if (!TryGetValidatedConfigurationDirectory(configurationDirectory, out var sftpDirectory))
            {
                return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.InvalidPath, "The remote configuration directory was not validated for this session.");
            }

            path = RemotePathMapper.Combine(sftpDirectory, temporaryFileName);
        }
        catch (ArgumentException)
        {
            return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.InvalidPath, "The remote temporary candidate path is invalid.");
        }

        try
        {
            var existed = await ExecuteSftpAsync(async token =>
            {
                if (!await _sftpClient.ExistsAsync(path, token))
                {
                    return false;
                }

                await _sftpClient.DeleteFileAsync(path, token);
                return true;
            }, cancellationToken);
            return new RemoteNutTemporaryCleanupResult(existed ? RemoteNutTransportStatus.Success : RemoteNutTransportStatus.NotFound);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.Cancelled, "The remote temporary candidate cleanup was cancelled.");
        }
        catch (SftpPermissionDeniedException)
        {
            return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.AccessDenied, "The remote temporary candidate cannot be deleted.");
        }
        catch (TimeoutException)
        {
            return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.Timeout, "The remote temporary candidate cleanup timed out.");
        }
        catch
        {
            return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.Failed, "The remote temporary candidate cleanup failed.");
        }
    }

    public async Task<RemoteNutCommitResult> CommitConfigurationAsync(RemoteNutConfigurationCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (Platform != RemoteNutPlatform.Windows || !IsCommitRequestSafe(request) || !IsSafeWriteCapabilityValidFor(request.ConfigurationDirectory))
        {
            return new RemoteNutCommitResult(RemoteNutTransportStatus.Unsupported, message: "Remote Windows safe write is not available.");
        }

        try
        {
            var output = await ExecuteWindowsCommandAsync(RemoteWindowsCommandBuilder.BuildWindowsCommit(request), CancellationToken.None);
            return RemoteWindowsCommandBuilder.IsExactSuccessMarker(output.ExitStatus, output.Output, "NUTMANAGER_COMMIT_OK")
                ? new RemoteNutCommitResult(RemoteNutTransportStatus.Success, RemotePathMapper.Combine(request.ConfigurationDirectory, request.BackupFileName))
                : new RemoteNutCommitResult(RemoteNutTransportStatus.Failed, message: "The remote configuration commit was rejected.");
        }
        catch (TimeoutException)
        {
            InvalidateSafeWriteCapability();
            return new RemoteNutCommitResult(RemoteNutTransportStatus.OutcomeUnknown, message: "The remote configuration commit outcome could not be confirmed.");
        }
        catch
        {
            InvalidateSafeWriteCapability();
            return new RemoteNutCommitResult(RemoteNutTransportStatus.OutcomeUnknown, message: "The remote configuration commit outcome could not be confirmed.");
        }
    }

    public async Task<RemoteNutCommitResult> RollbackConfigurationAsync(RemoteNutConfigurationRollbackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Platform != RemoteNutPlatform.Windows || !RemoteNutConfigurationFiles.IsRecognized(request.TargetFileName) ||
            !IsSafeWriteCapabilityValidFor(request.ConfigurationDirectory) ||
            !IsGeneratedBackupName(request.BackupFileName) || !RemoteNutGeneratedTemporaryFile.IsValidName(request.RollbackFileName) || !IsGeneratedBackupName(request.RecoveryFileName))
        {
            return new RemoteNutCommitResult(RemoteNutTransportStatus.Unsupported, message: "Remote Windows rollback is not available.");
        }

        try
        {
            var output = await ExecuteWindowsCommandAsync(RemoteWindowsCommandBuilder.BuildWindowsRollback(request), CancellationToken.None);
            return RemoteWindowsCommandBuilder.IsExactSuccessMarker(output.ExitStatus, output.Output, "NUTMANAGER_ROLLBACK_OK")
                ? new RemoteNutCommitResult(RemoteNutTransportStatus.Success, recoveryPath: RemotePathMapper.Combine(request.ConfigurationDirectory, request.RecoveryFileName))
                : new RemoteNutCommitResult(RemoteNutTransportStatus.Failed, message: "The remote rollback was rejected.");
        }
        catch
        {
            InvalidateSafeWriteCapability();
            return new RemoteNutCommitResult(RemoteNutTransportStatus.OutcomeUnknown, message: "The remote rollback outcome could not be confirmed.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _sftpClient.Dispose();
            _sshClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private bool TryGetValidatedConfigurationDirectory(string configurationDirectory, out string sftpDirectory)
    {
        try
        {
            sftpDirectory = RemotePathMapper.ToSftpPath(configurationDirectory);
            return _validatedConfigurationDirectories.Contains(sftpDirectory);
        }
        catch (ArgumentException)
        {
            sftpDirectory = string.Empty;
            return false;
        }
    }

    private async Task<bool> ProbeWindowsPlatformAsync(CancellationToken cancellationToken)
    {
        if (Platform != RemoteNutPlatform.Unknown)
        {
            return Platform == RemoteNutPlatform.Windows;
        }

        try
        {
            var output = await ExecuteWindowsCommandAsync(RemoteWindowsCommandBuilder.BuildWindowsPlatformProbe(), cancellationToken);
            Platform = RemoteWindowsCommandBuilder.IsExactSuccessMarker(output.ExitStatus, output.Output, "NUTMANAGER_WINDOWS") ? RemoteNutPlatform.Windows : RemoteNutPlatform.NonWindows;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Platform = RemoteNutPlatform.Unknown;
        }

        return Platform == RemoteNutPlatform.Windows;
    }

    private async Task<T> ExecuteSftpAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var operationCancellation = CreateOperationToken(cancellationToken, timeout ?? SftpTimeout);
        try
        {
            return await operation(operationCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Remote SFTP operation timed out.");
        }
    }

    private async Task WriteNewAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await ExecuteSftpAsync(async token =>
        {
            using var stream = await _sftpClient.OpenAsync(path, FileMode.CreateNew, FileAccess.Write, token);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
            return 0;
        }, cancellationToken);
    }

    private async Task<RemoteWindowsCommandResult> ExecuteWindowsCommandAsync(string command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var commandInstance = _sshClient.CreateCommand(command);
        commandInstance.CommandTimeout = CommitTimeout;
        using var operationCancellation = CreateOperationToken(cancellationToken, CommitTimeout);
        try
        {
            await commandInstance.ExecuteAsync(operationCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Remote Windows command timed out.");
        }

        var output = commandInstance.Result ?? string.Empty;
        return new RemoteWindowsCommandResult(commandInstance.ExitStatus, output.Length > 4096 ? output[..4096] : output);
    }

    private async Task<bool> DeleteCapabilityProbeTemporaryFileAsync(string configurationDirectory, string temporaryFileName)
    {
        if (!temporaryFileName.StartsWith(".nutmanager-capability-", StringComparison.Ordinal) || !RemoteNutGeneratedTemporaryFile.IsValidName(temporaryFileName))
        {
            return false;
        }

        var path = RemotePathMapper.Combine(configurationDirectory, temporaryFileName);
        try
        {
            ThrowIfDisposed();
            var cleanup = await ExecuteSftpAsync(async token =>
            {
                if (await _sftpClient.ExistsAsync(path, token))
                {
                    await _sftpClient.DeleteFileAsync(path, token);
                }
                return true;
            }, CancellationToken.None, CleanupTimeout);
            return cleanup;
        }
        catch
        {
            return false;
        }
    }

    private static CancellationTokenSource CreateOperationToken(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static bool IsGeneratedBackupName(string name) => RemoteNutGeneratedBackupFile.IsValidName(name);

    private static bool IsCommitRequestSafe(RemoteNutConfigurationCommitRequest request) =>
        RemoteNutConfigurationFiles.IsRecognized(request.TargetFileName) &&
        RemoteNutGeneratedTemporaryFile.IsValidName(request.TemporaryFileName) &&
        IsGeneratedBackupName(request.BackupFileName);

    private static string? GetParentPath(string path)
    {
        var slash = path.TrimEnd('/').LastIndexOf('/');
        return slash < 0 ? null : slash == 0 ? "/" : path[..slash];
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SshNetRemoteNutManagementSession));
        }
    }

    private sealed record RemoteWindowsCommandResult(int? ExitStatus, string Output);
}
