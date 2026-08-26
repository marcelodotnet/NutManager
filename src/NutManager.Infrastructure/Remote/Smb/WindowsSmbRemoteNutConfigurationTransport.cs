using System.Security.Cryptography;
using NutManager.Core.Configuration;
using NutManager.Core.Models;
using NutManager.Core.Services;

namespace NutManager.Infrastructure.Remote.Smb;

/// <summary>
/// Windows SMB configuration transport. SMB is only a user-supplied UNC file
/// transport: it never uses SSH, PowerShell, mapped drives, or a global WNet
/// connection lifecycle.
/// </summary>
public sealed class WindowsSmbRemoteNutConfigurationTransport : IRemoteNutConfigurationTransport
{
    private readonly ISmbFileSystem _fileSystem;
    private readonly IWindowsSmbSessionIdentityFactory _identityFactory;
    private readonly Func<bool> _isWindows;

    public WindowsSmbRemoteNutConfigurationTransport(
        ISmbFileSystem? fileSystem = null,
        IWindowsSmbSessionIdentityFactory? identityFactory = null,
        Func<bool>? isWindows = null)
    {
        _fileSystem = fileSystem ?? new WindowsSmbFileSystem();
        _identityFactory = identityFactory ?? new WindowsSmbSessionIdentityFactory();
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
    }

    public async Task<RemoteNutConnectionResult> ConnectAsync(RemoteNutConfigurationConnectionRequest request, CancellationToken cancellationToken = default)
    {
        if (request is not SmbRemoteNutConnectionRequest smbRequest)
        {
            return new RemoteNutConnectionResult(RemoteNutConnectionState.Failed, message: "The SMB transport accepts only SMB configuration requests.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_isWindows())
        {
            return new RemoteNutConnectionResult(RemoteNutConnectionState.Failed, message: "O transporte SMB de configuração está disponível somente no Windows.");
        }

        IWindowsSmbSessionIdentity identity;
        if (smbRequest.AuthenticationMode == SmbAuthenticationMode.ExplicitCredentials)
        {
            var identityResult = await _identityFactory.CreateExplicitIdentityAsync(
                smbRequest.SharePath,
                smbRequest.Username!,
                smbRequest.Password,
                cancellationToken).ConfigureAwait(false);
            if (!identityResult.IsSuccess)
            {
                return new RemoteNutConnectionResult(RemoteNutConnectionState.AuthenticationFailed, message: identityResult.Message ?? "Não foi possível criar uma identidade Windows isolada para as credenciais SMB informadas.");
            }

            identity = identityResult.Identity!;
        }
        else
        {
            identity = _identityFactory.CreateCurrentIdentity();
        }

        try
        {
            // LOGON_NEW_CREDENTIALS only establishes an outbound identity. A bounded,
            // read-only share operation is required before this session is considered connected.
            await identity.RunAsync(
                token => _fileSystem.ListDirectoryAsync(smbRequest.SharePath, token),
                cancellationToken).ConfigureAwait(false);
            return new RemoteNutConnectionResult(
                RemoteNutConnectionState.Connected,
                new WindowsSmbRemoteNutConfigurationSession(smbRequest.SharePath, smbRequest.CanWrite, _fileSystem, identity));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await identity.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            await identity.DisposeAsync().ConfigureAwait(false);
            return new RemoteNutConnectionResult(RemoteNutConnectionState.AccessDenied, message: "O compartilhamento SMB não aceitou a identidade Windows selecionada.");
        }
        catch (IOException exception) when (IsCredentialConflict(exception))
        {
            await identity.DisposeAsync().ConfigureAwait(false);
            return new RemoteNutConnectionResult(RemoteNutConnectionState.AuthenticationFailed, message: "O Windows reportou um conflito de credenciais para este servidor SMB. O NutManager não desconectará nenhuma conexão existente.");
        }
        catch (Exception)
        {
            await identity.DisposeAsync().ConfigureAwait(false);
            // "Somente leitura" used to appear here to describe the probe, which is a read-only
            // listing. On screen it sat next to "Acesso: Gerenciar" and read as the profile's
            // access mode, so a management profile appeared to have been downgraded. The access
            // mode is a separate fact and no connection failure may imply anything about it.
            return new RemoteNutConnectionResult(RemoteNutConnectionState.ConnectionFailed, message: "Não foi possível acessar o compartilhamento SMB configurado.");
        }
    }

    private static bool IsCredentialConflict(IOException exception) => ((uint)exception.HResult & 0xFFFF) == 1219;
}

public sealed class WindowsSmbRemoteNutConfigurationSession : IRemoteNutConfigurationSession, IRemoteNutWriteIntentSession
{
    private readonly string _shareRoot;
    private bool _canWrite;
    private readonly ISmbFileSystem _fileSystem;
    private readonly IWindowsSmbSessionIdentity _identity;
    private readonly SmbRemoteNutConfigurationPathPolicy _pathPolicy;
    private readonly HashSet<string> _validatedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly RemoteSafeWriteCapabilityState _safeWriteCapability = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _disposeStarted;
    private bool _disposed;

    public WindowsSmbRemoteNutConfigurationSession(
        string shareRoot,
        bool canWrite,
        ISmbFileSystem fileSystem,
        IWindowsSmbSessionIdentity identity)
    {
        _shareRoot = SmbUncPath.NormalizeShareRoot(shareRoot);
        _canWrite = canWrite;
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _pathPolicy = new SmbRemoteNutConfigurationPathPolicy(_shareRoot);
    }

    public RemoteNutPlatform Platform => RemoteNutPlatform.Unknown;

    public string HomeDirectory => _shareRoot;

    public IRemoteNutConfigurationPathPolicy PathPolicy => _pathPolicy;

    public void ApplyWriteIntent(bool canWrite)
    {
        _canWrite = canWrite;
        _safeWriteCapability.ClearVerification();
    }

    public bool IsSafeWriteCapabilityValidFor(string configurationDirectory) =>
        _canWrite &&
        TryGetValidatedDirectory(configurationDirectory, out var normalizedDirectory) &&
        _safeWriteCapability.IsValidFor(normalizedDirectory);

    public Task<RemoteNutDirectoryListing> BrowseDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        ExecuteReadAsync(async token =>
        {
            var normalizedDirectory = NormalizeDirectory(directory);
            try
            {
                var entries = await _fileSystem.ListDirectoryAsync(normalizedDirectory, token).ConfigureAwait(false);
                return new RemoteNutDirectoryListing(
                    normalizedDirectory,
                    SmbUncPath.GetParentWithinShare(_shareRoot, normalizedDirectory),
                    entries.Select(entry => new RemoteNutDirectoryEntry(entry.Name, entry.FullPath, entry.IsDirectory, entry.IsReparsePoint)).ToArray());
            }
            catch (UnauthorizedAccessException)
            {
                return new RemoteNutDirectoryListing(normalizedDirectory, SmbUncPath.GetParentWithinShare(_shareRoot, normalizedDirectory), []);
            }
        }, cancellationToken);

    public Task<RemoteNutDirectoryValidationResult> ValidateConfigurationDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        ExecuteReadAsync(async token =>
        {
            string normalizedDirectory;
            try
            {
                normalizedDirectory = NormalizeDirectory(directory);
            }
            catch (ArgumentException)
            {
                return new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.InvalidPath, directory, message: "O diretório SMB selecionado está fora do compartilhamento configurado.");
            }

            try
            {
                if (await _fileSystem.IsReparsePointAsync(normalizedDirectory, token).ConfigureAwait(false))
                {
                    return new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.Unsupported, normalizedDirectory, message: "O diretório SMB selecionado é um reparse point e não pode ser usado para escrita.");
                }

                var entries = await _fileSystem.ListDirectoryAsync(normalizedDirectory, token).ConfigureAwait(false);
                var names = entries.Where(entry => !entry.IsDirectory && RemoteNutConfigurationFiles.IsRecognized(entry.Name))
                    .Select(entry => entry.Name)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                _validatedDirectories.Add(normalizedDirectory);
                return new RemoteNutDirectoryValidationResult(
                    RemoteNutTransportStatus.Success,
                    normalizedDirectory,
                    names,
                    names.Length == 0 ? "Nenhum arquivo de configuração NUT reconhecido foi encontrado no diretório selecionado." : null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.Cancelled, normalizedDirectory, message: "A validação SMB foi cancelada.");
            }
            catch (UnauthorizedAccessException)
            {
                return new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.AccessDenied, normalizedDirectory, message: "O diretório SMB selecionado não pode ser acessado.");
            }
            catch (DirectoryNotFoundException)
            {
                return new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.NotFound, normalizedDirectory, message: "O diretório SMB selecionado não foi encontrado.");
            }
            catch (IOException)
            {
                return new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.Failed, normalizedDirectory, message: "Não foi possível validar o diretório SMB selecionado.");
            }
        }, cancellationToken);

    public Task<RemoteNutFileReadResult> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        ExecuteReadAsync(async token =>
        {
            if (!TryGetValidatedTarget(path, out var normalizedPath))
            {
                return new RemoteNutFileReadResult(RemoteNutTransportStatus.InvalidPath, message: "O arquivo SMB não pertence a um diretório de configuração validado.");
            }

            try
            {
                if (await _fileSystem.IsReparsePointAsync(normalizedPath, token).ConfigureAwait(false))
                {
                    return new RemoteNutFileReadResult(RemoteNutTransportStatus.Unsupported, message: "O arquivo SMB é um reparse point e não pode ser acessado pela configuração remota.");
                }

                return new RemoteNutFileReadResult(RemoteNutTransportStatus.Success, await _fileSystem.ReadFileAsync(normalizedPath, token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new RemoteNutFileReadResult(RemoteNutTransportStatus.Cancelled, message: "A leitura SMB foi cancelada.");
            }
            catch (FileNotFoundException)
            {
                return new RemoteNutFileReadResult(RemoteNutTransportStatus.NotFound, message: "O arquivo SMB não foi encontrado.");
            }
            catch (UnauthorizedAccessException)
            {
                return new RemoteNutFileReadResult(RemoteNutTransportStatus.AccessDenied, message: "O arquivo SMB não pode ser acessado.");
            }
            catch (IOException)
            {
                return new RemoteNutFileReadResult(RemoteNutTransportStatus.Failed, message: "O arquivo SMB não pôde ser lido.");
            }
        }, cancellationToken);

    public Task<RemoteNutWriteCapabilityResult> ProbeSafeWriteCapabilityAsync(string directory, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async () =>
        {
            if (!_canWrite)
            {
                return new RemoteNutWriteCapabilityResult(false, Platform, message: "O perfil SMB está configurado como somente leitura.");
            }

            if (!_safeWriteCapability.TryBeginProbe())
            {
                return new RemoteNutWriteCapabilityResult(false, Platform, message: "O resultado de uma escrita SMB anterior é indeterminado. Desconecte e conecte novamente antes de tentar gravar.");
            }

            if (!TryGetValidatedDirectory(directory, out var normalizedDirectory))
            {
                return new RemoteNutWriteCapabilityResult(false, Platform, message: "O diretório SMB precisa ser validado nesta sessão antes do teste de escrita.");
            }

            var token = Guid.NewGuid().ToString("N");
            var sourcePath = _pathPolicy.CombineDirectChild(normalizedDirectory, $".nutmanager-smb-capability-{token}-source.tmp");
            var candidatePath = _pathPolicy.CombineDirectChild(normalizedDirectory, $".nutmanager-smb-capability-{token}-candidate.tmp");
            var backupPath = _pathPolicy.CombineDirectChild(normalizedDirectory, $".nutmanager-smb-capability-{token}-backup.bak");
            var original = new byte[] { 0x31 };
            var candidate = new byte[] { 0x32 };
            var reservation = CreateReservationMarker();
            var sourceOwned = false;
            var candidateOwned = false;
            var backupOwned = false;
            ReadOnlyMemory<byte> sourceExpected = original;
            ReadOnlyMemory<byte> backupExpected = reservation;
            RemoteNutWriteCapabilityResult result;
            try
            {
                await _fileSystem.WriteNewFileAsync(sourcePath, original, CancellationToken.None).ConfigureAwait(false);
                sourceOwned = true;
                await _fileSystem.WriteNewFileAsync(candidatePath, candidate, CancellationToken.None).ConfigureAwait(false);
                candidateOwned = true;
                await _fileSystem.WriteNewFileAsync(backupPath, reservation, CancellationToken.None).ConfigureAwait(false);
                backupOwned = true;
                if (await IsAnyWriteReparsePointAsync(CancellationToken.None, sourcePath, candidatePath, backupPath).ConfigureAwait(false) ||
                    !(await _fileSystem.ReadFileAsync(sourcePath, CancellationToken.None).ConfigureAwait(false)).Span.SequenceEqual(original) ||
                    !(await _fileSystem.ReadFileAsync(candidatePath, CancellationToken.None).ConfigureAwait(false)).Span.SequenceEqual(candidate) ||
                    !(await _fileSystem.ReadFileAsync(backupPath, CancellationToken.None).ConfigureAwait(false)).Span.SequenceEqual(reservation))
                {
                    result = new RemoteNutWriteCapabilityResult(false, Platform, message: "A verificação dos arquivos temporários SMB falhou.");
                }
                else
                {
                    await _fileSystem.ReplaceFileAsync(candidatePath, sourcePath, backupPath, CancellationToken.None).ConfigureAwait(false);
                    candidateOwned = false;
                    sourceExpected = candidate;
                    backupExpected = original;
                    result = !(await _fileSystem.ReadFileAsync(sourcePath, CancellationToken.None).ConfigureAwait(false)).Span.SequenceEqual(candidate) ||
                        !(await _fileSystem.ReadFileAsync(backupPath, CancellationToken.None).ConfigureAwait(false)).Span.SequenceEqual(original)
                        ? new RemoteNutWriteCapabilityResult(false, Platform, message: "O compartilhamento SMB não confirmou a semântica necessária de File.Replace.")
                        : new RemoteNutWriteCapabilityResult(true, Platform);
                }
            }
            catch
            {
                result = new RemoteNutWriteCapabilityResult(false, Platform, message: "O compartilhamento SMB não suporta a substituição segura exigida.");
            }

            var cleanupPath = await CleanupOwnedFilesAsync(
                (sourcePath, sourceOwned, sourceExpected),
                (candidatePath, candidateOwned, candidate),
                (backupPath, backupOwned, backupExpected)).ConfigureAwait(false);
            if (cleanupPath is not null)
            {
                return new RemoteNutWriteCapabilityResult(false, Platform, cleanupPath, "A limpeza do teste SMB não pôde ser confirmada. Revise o arquivo temporário antes de tentar novamente.");
            }

            if (!result.IsSupported)
            {
                return result;
            }

            return _safeWriteCapability.TryCompleteProbe(normalizedDirectory)
                ? result
                : new RemoteNutWriteCapabilityResult(false, Platform, message: "O estado de escrita SMB foi invalidado durante a verificação.");
        }, cancellationToken);

    public void InvalidateSafeWriteCapability() => _safeWriteCapability.InvalidateSession();

    public Task<RemoteNutFileReadResult> UploadCandidateAsync(RemoteNutCandidateUploadRequest request, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async () =>
        {
            if (!RemoteNutConfigurationFiles.IsRecognized(request.TargetFileName) || !RemoteNutGeneratedTemporaryFile.IsValidName(request.TemporaryFileName) ||
                !IsSafeWriteCapabilityValidFor(request.ConfigurationDirectory))
            {
                return new RemoteNutFileReadResult(RemoteNutTransportStatus.Unsupported, message: "A escrita SMB exige uma capacidade de substituição segura verificada para este diretório.");
            }

            var candidatePath = _pathPolicy.CombineDirectChild(NormalizeDirectory(request.ConfigurationDirectory), request.TemporaryFileName);
            try
            {
                await _fileSystem.WriteNewFileAsync(candidatePath, request.CandidateBytes, CancellationToken.None).ConfigureAwait(false);
                return new RemoteNutFileReadResult(RemoteNutTransportStatus.Success, await _fileSystem.ReadFileAsync(candidatePath, CancellationToken.None).ConfigureAwait(false));
            }
            catch (UnauthorizedAccessException)
            {
                return new RemoteNutFileReadResult(RemoteNutTransportStatus.AccessDenied, message: "O candidato SMB não pode ser criado.");
            }
            catch (IOException)
            {
                return new RemoteNutFileReadResult(RemoteNutTransportStatus.Failed, message: "O candidato SMB não pode ser criado.");
            }
        }, cancellationToken);

    public Task<RemoteNutTemporaryCleanupResult> DeleteGeneratedTemporaryFileAsync(string configurationDirectory, string temporaryFileName, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async () =>
        {
            if (!RemoteNutGeneratedTemporaryFile.IsValidName(temporaryFileName) || !TryGetValidatedDirectory(configurationDirectory, out var normalizedDirectory))
            {
                return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.InvalidPath, "O caminho temporário SMB é inválido.");
            }

            var path = _pathPolicy.CombineDirectChild(normalizedDirectory, temporaryFileName);
            try
            {
                if (!await _fileSystem.FileExistsAsync(path, CancellationToken.None).ConfigureAwait(false))
                {
                    return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.NotFound);
                }

                await _fileSystem.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
                return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.Success);
            }
            catch (UnauthorizedAccessException)
            {
                return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.AccessDenied, "O candidato SMB não pode ser removido.");
            }
            catch (IOException)
            {
                return new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.Failed, "A limpeza do candidato SMB falhou.");
            }
        }, cancellationToken);

    public Task<RemoteNutCommitResult> CommitConfigurationAsync(RemoteNutConfigurationCommitRequest request, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(() => CommitCoreAsync(request), cancellationToken);

    public Task<RemoteNutCommitResult> RollbackConfigurationAsync(RemoteNutConfigurationRollbackRequest request, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(() => RollbackCoreAsync(request), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _disposed = true;
            await _identity.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private async Task<RemoteNutCommitResult> CommitCoreAsync(RemoteNutConfigurationCommitRequest request)
    {
        if (!IsSafeCommitRequest(request) || !IsSafeWriteCapabilityValidFor(request.ConfigurationDirectory))
        {
            return new RemoteNutCommitResult(RemoteNutTransportStatus.Unsupported, message: "A substituição SMB segura não está disponível para este diretório.");
        }

        var directory = NormalizeDirectory(request.ConfigurationDirectory);
        var targetPath = _pathPolicy.CombineDirectChild(directory, request.TargetFileName);
        var candidatePath = _pathPolicy.CombineDirectChild(directory, request.TemporaryFileName);
        var backupPath = _pathPolicy.CombineDirectChild(directory, request.BackupFileName);
        var reservation = CreateReservationMarker();
        var backupReserved = false;
        try
        {
            if (await IsAnyWriteReparsePointAsync(CancellationToken.None, targetPath, candidatePath).ConfigureAwait(false))
            {
                return new RemoteNutCommitResult(RemoteNutTransportStatus.Unsupported, message: "O caminho SMB de escrita é um reparse point.");
            }

            var target = await _fileSystem.ReadFileAsync(targetPath, CancellationToken.None).ConfigureAwait(false);
            var candidate = await _fileSystem.ReadFileAsync(candidatePath, CancellationToken.None).ConfigureAwait(false);
            if (!FingerprintMatches(target.Span, request.ExpectedOriginalFingerprint) || !FingerprintMatches(candidate.Span, request.ExpectedCandidateFingerprint))
            {
                return new RemoteNutCommitResult(RemoteNutTransportStatus.Failed, message: "A configuração SMB foi alterada externamente antes da substituição.");
            }

            await _fileSystem.WriteNewFileAsync(backupPath, reservation, CancellationToken.None).ConfigureAwait(false);
            backupReserved = true;
            if (await IsAnyWriteReparsePointAsync(CancellationToken.None, targetPath, candidatePath, backupPath).ConfigureAwait(false))
            {
                var cleanup = await TryDeleteOwnedFileAsync(backupPath, reservation).ConfigureAwait(false);
                return cleanup
                    ? new RemoteNutCommitResult(RemoteNutTransportStatus.Unsupported, message: "O backup SMB reservado não é um arquivo seguro.")
                    : ManualReservationResult(backupPath);
            }

            // Keep all SMB I/O before these final reads. The comparisons below are
            // in-memory only, so ReplaceFileAsync follows the final content check directly.
            var finalTarget = await _fileSystem.ReadFileAsync(targetPath, CancellationToken.None).ConfigureAwait(false);
            var finalCandidate = await _fileSystem.ReadFileAsync(candidatePath, CancellationToken.None).ConfigureAwait(false);
            var finalBackup = await _fileSystem.ReadFileAsync(backupPath, CancellationToken.None).ConfigureAwait(false);
            if (!finalBackup.Span.SequenceEqual(reservation))
            {
                InvalidateSafeWriteCapability();
                return ManualReservationResult(backupPath);
            }

            if (!FingerprintMatches(finalTarget.Span, request.ExpectedOriginalFingerprint) ||
                !FingerprintMatches(finalCandidate.Span, request.ExpectedCandidateFingerprint))
            {
                var cleanup = await TryDeleteOwnedFileAsync(backupPath, reservation).ConfigureAwait(false);
                return cleanup
                    ? new RemoteNutCommitResult(RemoteNutTransportStatus.Failed, message: "A configuração SMB foi alterada externamente antes da substituição final.")
                    : ManualReservationResult(backupPath);
            }

            await _fileSystem.ReplaceFileAsync(candidatePath, targetPath, backupPath, CancellationToken.None).ConfigureAwait(false);
            return new RemoteNutCommitResult(RemoteNutTransportStatus.Success, backupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var knownNotExecuted = await IsProvenUnreplacedAsync(targetPath, candidatePath, request.ExpectedOriginalFingerprint, request.ExpectedCandidateFingerprint).ConfigureAwait(false);
            if (knownNotExecuted)
            {
                if (!backupReserved || await TryDeleteOwnedFileAsync(backupPath, reservation).ConfigureAwait(false))
                {
                    return new RemoteNutCommitResult(RemoteNutTransportStatus.Failed, message: "A substituição SMB foi rejeitada antes de ser concluída.");
                }

                return ManualReservationResult(backupPath);
            }

            InvalidateSafeWriteCapability();
            return new RemoteNutCommitResult(RemoteNutTransportStatus.OutcomeUnknown, backupReserved ? backupPath : null, message: "O resultado da substituição SMB não pôde ser confirmado.");
        }
    }

    private async Task<RemoteNutCommitResult> RollbackCoreAsync(RemoteNutConfigurationRollbackRequest request)
    {
        if (!RemoteNutConfigurationFiles.IsRecognized(request.TargetFileName) || !RemoteNutGeneratedBackupFile.IsValidName(request.BackupFileName) ||
            !RemoteNutGeneratedTemporaryFile.IsValidName(request.RollbackFileName) || !RemoteNutGeneratedBackupFile.IsValidName(request.RecoveryFileName) ||
            !IsSafeWriteCapabilityValidFor(request.ConfigurationDirectory))
        {
            return new RemoteNutCommitResult(RemoteNutTransportStatus.Unsupported, message: "O rollback SMB seguro não está disponível para este diretório.");
        }

        var directory = NormalizeDirectory(request.ConfigurationDirectory);
        var targetPath = _pathPolicy.CombineDirectChild(directory, request.TargetFileName);
        var backupPath = _pathPolicy.CombineDirectChild(directory, request.BackupFileName);
        var rollbackPath = _pathPolicy.CombineDirectChild(directory, request.RollbackFileName);
        var recoveryPath = _pathPolicy.CombineDirectChild(directory, request.RecoveryFileName);
        var reservation = CreateReservationMarker();
        var rollbackOwned = false;
        var recoveryReserved = false;
        ReadOnlyMemory<byte> original = default;
        ReadOnlyMemory<byte> replacedContent = default;
        var replacementPrepared = false;
        try
        {
            if (await IsAnyWriteReparsePointAsync(CancellationToken.None, targetPath, backupPath).ConfigureAwait(false))
            {
                return new RemoteNutCommitResult(RemoteNutTransportStatus.Unsupported, message: "O caminho SMB de rollback é um reparse point.");
            }

            original = await _fileSystem.ReadFileAsync(backupPath, CancellationToken.None).ConfigureAwait(false);
            if (!FingerprintMatches(original.Span, request.ExpectedOriginalFingerprint))
            {
                return new RemoteNutCommitResult(RemoteNutTransportStatus.Failed, message: "O backup SMB não corresponde à configuração original.");
            }

            await _fileSystem.WriteNewFileAsync(rollbackPath, original, CancellationToken.None).ConfigureAwait(false);
            rollbackOwned = true;
            replacedContent = await _fileSystem.ReadFileAsync(targetPath, CancellationToken.None).ConfigureAwait(false);
            await _fileSystem.WriteNewFileAsync(recoveryPath, reservation, CancellationToken.None).ConfigureAwait(false);
            recoveryReserved = true;
            if (await IsAnyWriteReparsePointAsync(CancellationToken.None, targetPath, rollbackPath, recoveryPath).ConfigureAwait(false))
            {
                var rollbackCleanup = await TryDeleteOwnedFileAsync(rollbackPath, original).ConfigureAwait(false);
                var recoveryCleanup = await TryDeleteOwnedFileAsync(recoveryPath, reservation).ConfigureAwait(false);
                return rollbackCleanup && recoveryCleanup
                    ? new RemoteNutCommitResult(RemoteNutTransportStatus.Unsupported, message: "O recovery SMB reservado não é um arquivo seguro.")
                    : ManualReservationResult(recoveryPath, isRecoveryPath: true);
            }

            // Keep all SMB I/O before these final reads. The comparisons below are
            // in-memory only, so ReplaceFileAsync follows the final content check directly.
            var finalBackup = await _fileSystem.ReadFileAsync(backupPath, CancellationToken.None).ConfigureAwait(false);
            var finalRollback = await _fileSystem.ReadFileAsync(rollbackPath, CancellationToken.None).ConfigureAwait(false);
            var finalTarget = await _fileSystem.ReadFileAsync(targetPath, CancellationToken.None).ConfigureAwait(false);
            var finalRecovery = await _fileSystem.ReadFileAsync(recoveryPath, CancellationToken.None).ConfigureAwait(false);
            if (!finalRecovery.Span.SequenceEqual(reservation))
            {
                InvalidateSafeWriteCapability();
                return ManualReservationResult(recoveryPath, isRecoveryPath: true);
            }

            if (!finalRollback.Span.SequenceEqual(original.Span))
            {
                var recoveryCleanup = await TryDeleteOwnedFileAsync(recoveryPath, reservation).ConfigureAwait(false);
                InvalidateSafeWriteCapability();
                return recoveryCleanup
                    ? new RemoteNutCommitResult(RemoteNutTransportStatus.OutcomeUnknown, recoveryPath: recoveryPath, message: "O arquivo temporário de rollback SMB foi alterado antes da substituição.")
                    : ManualReservationResult(recoveryPath, isRecoveryPath: true);
            }

            if (!finalBackup.Span.SequenceEqual(original.Span) || !finalTarget.Span.SequenceEqual(replacedContent.Span))
            {
                var rollbackCleanup = await TryDeleteOwnedFileAsync(rollbackPath, original).ConfigureAwait(false);
                var recoveryCleanup = await TryDeleteOwnedFileAsync(recoveryPath, reservation).ConfigureAwait(false);
                return rollbackCleanup && recoveryCleanup
                    ? new RemoteNutCommitResult(RemoteNutTransportStatus.Failed, message: "A configuração SMB foi alterada externamente antes do rollback final.")
                    : ManualReservationResult(recoveryPath, isRecoveryPath: true);
            }

            replacementPrepared = true;
            await _fileSystem.ReplaceFileAsync(rollbackPath, targetPath, recoveryPath, CancellationToken.None).ConfigureAwait(false);
            var restoredTarget = await _fileSystem.ReadFileAsync(targetPath, CancellationToken.None).ConfigureAwait(false);
            var recovery = await _fileSystem.ReadFileAsync(recoveryPath, CancellationToken.None).ConfigureAwait(false);
            if (!restoredTarget.Span.SequenceEqual(original.Span) || !recovery.Span.SequenceEqual(replacedContent.Span))
            {
                InvalidateSafeWriteCapability();
                return new RemoteNutCommitResult(RemoteNutTransportStatus.OutcomeUnknown, recoveryPath: recoveryPath, message: "O rollback SMB foi concluído, mas o conteúdo final não pôde ser confirmado.");
            }

            return new RemoteNutCommitResult(RemoteNutTransportStatus.Success, recoveryPath: recoveryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!replacementPrepared)
            {
                var rollbackCleanup = !rollbackOwned || await TryDeleteOwnedFileAsync(rollbackPath, original).ConfigureAwait(false);
                var recoveryCleanup = !recoveryReserved || await TryDeleteOwnedFileAsync(recoveryPath, reservation).ConfigureAwait(false);
                if (rollbackCleanup && recoveryCleanup)
                {
                    return new RemoteNutCommitResult(RemoteNutTransportStatus.Failed, message: "O rollback SMB foi rejeitado antes de ser concluído.");
                }

                return ManualReservationResult(recoveryReserved ? recoveryPath : rollbackPath, isRecoveryPath: true);
            }

            if (recoveryReserved && replacementPrepared)
            {
                var currentTarget = await TryReadAsync(targetPath).ConfigureAwait(false);
                var rollbackStillPresent = await TryReadAsync(rollbackPath).ConfigureAwait(false);
                if (currentTarget is not null && rollbackStillPresent is not null &&
                    currentTarget.Value.Span.SequenceEqual(replacedContent.Span) &&
                    rollbackStillPresent.Value.Span.SequenceEqual(original.Span))
                {
                    var rollbackCleanup = !rollbackOwned || await TryDeleteOwnedFileAsync(rollbackPath, rollbackStillPresent.Value).ConfigureAwait(false);
                    var recoveryCleanup = await TryDeleteOwnedFileAsync(recoveryPath, reservation).ConfigureAwait(false);
                    if (rollbackCleanup && recoveryCleanup)
                    {
                        return new RemoteNutCommitResult(RemoteNutTransportStatus.Failed, message: "O rollback SMB foi rejeitado antes de ser concluído.");
                    }
                }
            }

            InvalidateSafeWriteCapability();
            return new RemoteNutCommitResult(RemoteNutTransportStatus.OutcomeUnknown, recoveryPath: recoveryReserved ? recoveryPath : null, message: "O resultado do rollback SMB não pôde ser confirmado.");
        }
    }

    private string NormalizeDirectory(string directory) => _pathPolicy.NormalizeDirectory(directory);

    private bool TryGetValidatedDirectory(string directory, out string normalizedDirectory)
    {
        try
        {
            normalizedDirectory = NormalizeDirectory(directory);
            return _validatedDirectories.Contains(normalizedDirectory);
        }
        catch (ArgumentException)
        {
            normalizedDirectory = string.Empty;
            return false;
        }
    }

    private bool TryGetValidatedTarget(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        try
        {
            var normalized = _pathPolicy.NormalizePath(path);
            var separator = normalized.LastIndexOf('\\');
            if (separator <= 0)
            {
                return false;
            }

            var directory = NormalizeDirectory(normalized[..separator]);
            var fileName = normalized[(separator + 1)..];
            if (!IsAllowedConfigurationChildName(fileName) || !_validatedDirectories.Contains(directory))
            {
                return false;
            }

            normalizedPath = _pathPolicy.CombineDirectChild(directory, fileName);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<bool> IsAnyWriteReparsePointAsync(CancellationToken cancellationToken, params string[] paths)
    {
        foreach (var path in paths)
        {
            if (await _fileSystem.IsReparsePointAsync(path, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string?> CleanupOwnedFilesAsync(params (string Path, bool Owned, ReadOnlyMemory<byte> ExpectedBytes)[] files)
    {
        foreach (var file in files)
        {
            if (file.Owned && !await TryDeleteOwnedFileAsync(file.Path, file.ExpectedBytes).ConfigureAwait(false))
            {
                return file.Path;
            }
        }

        return null;
    }

    private async Task<bool> TryDeleteOwnedFileAsync(string path, ReadOnlyMemory<byte> expectedBytes)
    {
        try
        {
            if (!await _fileSystem.FileExistsAsync(path, CancellationToken.None).ConfigureAwait(false))
            {
                return true;
            }

            var current = await _fileSystem.ReadFileAsync(path, CancellationToken.None).ConfigureAwait(false);
            if (!current.Span.SequenceEqual(expectedBytes.Span))
            {
                return false;
            }

            await _fileSystem.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsProvenUnreplacedAsync(string targetPath, string candidatePath, string originalFingerprint, string candidateFingerprint)
    {
        var target = await TryReadAsync(targetPath).ConfigureAwait(false);
        var candidate = await TryReadAsync(candidatePath).ConfigureAwait(false);
        return target is not null && candidate is not null &&
            FingerprintMatches(target.Value.Span, originalFingerprint) &&
            FingerprintMatches(candidate.Value.Span, candidateFingerprint);
    }

    private async Task<ReadOnlyMemory<byte>?> TryReadAsync(string path)
    {
        try
        {
            return await _fileSystem.ReadFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static RemoteNutCommitResult ManualReservationResult(string path, bool isRecoveryPath = false)
    {
        return isRecoveryPath
            ? new RemoteNutCommitResult(RemoteNutTransportStatus.OutcomeUnknown, recoveryPath: path, message: "A reserva de recovery SMB não pôde ser limpa com segurança. Revise o caminho antes de tentar novamente.")
            : new RemoteNutCommitResult(RemoteNutTransportStatus.OutcomeUnknown, path, message: "A reserva de backup SMB não pôde ser limpa com segurança. Revise o caminho antes de tentar novamente.");
    }

    private static bool IsSafeCommitRequest(RemoteNutConfigurationCommitRequest request) =>
        RemoteNutConfigurationFiles.IsRecognized(request.TargetFileName) &&
        RemoteNutGeneratedTemporaryFile.IsValidName(request.TemporaryFileName) &&
        RemoteNutGeneratedBackupFile.IsValidName(request.BackupFileName);

    private static bool IsAllowedConfigurationChildName(string fileName) =>
        RemoteNutConfigurationFiles.IsRecognized(fileName) ||
        RemoteNutGeneratedTemporaryFile.IsValidName(fileName) ||
        RemoteNutGeneratedBackupFile.IsValidName(fileName);

    private static byte[] CreateReservationMarker()
    {
        var marker = new byte[32];
        RandomNumberGenerator.Fill(marker);
        return marker;
    }

    private static bool FingerprintMatches(ReadOnlySpan<byte> bytes, string expectedFingerprint) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedFingerprint, StringComparison.OrdinalIgnoreCase);

    private async Task<T> ExecuteReadAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            return await _identity.RunAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<T> ExecuteMutationAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfUnavailable();
            // Once a synchronous/atomic mutation is dispatched, cancellation cannot safely
            // report completion before it finishes. The session gate holds until the real work ends.
            return await _identity.RunAsync(_ => operation(), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed || Volatile.Read(ref _disposeStarted) != 0)
        {
            throw new ObjectDisposedException(nameof(WindowsSmbRemoteNutConfigurationSession));
        }
    }
}
