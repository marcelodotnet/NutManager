using NutManager.Core.Configuration;
using NutManager.Core.Models;

namespace NutManager.Core.Services;

public enum RemoteNutConnectionState
{
    Disconnected,
    Connecting,
    HostKeyTrustRequired,
    Connected,
    Validating,
    Ready,
    AuthenticationFailed,
    HostKeyMismatch,
    AccessDenied,
    Timeout,
    ConnectionFailed,
    Failed
}

public enum RemoteNutPlatform
{
    Unknown,
    Windows,
    NonWindows
}

public enum RemoteNutTransportStatus
{
    Success,
    NotFound,
    AccessDenied,
    InvalidPath,
    Unsupported,
    Timeout,
    Cancelled,
    Failed,
    OutcomeUnknown
}

public static class RemoteNutConfigurationFiles
{
    private static readonly IReadOnlyDictionary<NutConfigurationFileKind, string> Names = new Dictionary<NutConfigurationFileKind, string>
    {
        [NutConfigurationFileKind.NutConf] = "nut.conf",
        [NutConfigurationFileKind.UpsConf] = "ups.conf",
        [NutConfigurationFileKind.UpsdConf] = "upsd.conf",
        [NutConfigurationFileKind.UpsdUsers] = "upsd.users",
        [NutConfigurationFileKind.UpsmonConf] = "upsmon.conf"
    };

    public static IReadOnlyList<string> AllNames { get; } = Names.Values.ToArray();

    public static string GetFileName(NutConfigurationFileKind fileKind) => Names.TryGetValue(fileKind, out var name)
        ? name
        : throw new ArgumentOutOfRangeException(nameof(fileKind));

    public static bool IsRecognized(string fileName) => AllNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
}

public static class RemoteNutGeneratedTemporaryFile
{
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.StartsWith(".nutmanager-", StringComparison.Ordinal) &&
        name.EndsWith(".tmp", StringComparison.Ordinal) &&
        name.Length > ".nutmanager-".Length + ".tmp".Length &&
        name.IndexOfAny(['/', '\\']) < 0 &&
        !name.Contains("..", StringComparison.Ordinal);
}

public static class RemoteNutGeneratedBackupFile
{
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.StartsWith(".nutmanager-", StringComparison.Ordinal) &&
        name.EndsWith(".bak", StringComparison.Ordinal) &&
        name.Length > ".nutmanager-".Length + ".bak".Length &&
        name.IndexOfAny(['/', '\\']) < 0 &&
        !name.Contains("..", StringComparison.Ordinal) &&
        !name.Any(char.IsControl);
}

public sealed class RemoteNutHostKeyInfo
{
    public RemoteNutHostKeyInfo(string host, int port, string algorithm, string fingerprint)
    {
        Host = NutMonitoringProfile.ValidateRequiredText(host, nameof(host), 255);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Port = port;
        Algorithm = NutMonitoringProfile.ValidateRequiredText(algorithm, nameof(algorithm), 128);
        Fingerprint = NutMonitoringProfile.ValidateRequiredText(fingerprint, nameof(fingerprint), 512);
        if (!IsCanonicalSha256Fingerprint(Fingerprint))
        {
            throw new ArgumentException("The host key fingerprint is invalid.", nameof(fingerprint));
        }
    }

    public string Host { get; }

    public int Port { get; }

    public string Algorithm { get; }

    public string Fingerprint { get; }

    private static bool IsCanonicalSha256Fingerprint(string fingerprint)
    {
        const string prefix = "SHA256:";
        var encoded = fingerprint.StartsWith(prefix, StringComparison.Ordinal) ? fingerprint[prefix.Length..] : string.Empty;
        if (encoded.Length != 43 || encoded.Contains('='))
        {
            return false;
        }

        try
        {
            var hash = Convert.FromBase64String(encoded + "=");
            return hash.Length == 32 && string.Equals(Convert.ToBase64String(hash).TrimEnd('='), encoded, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public abstract class RemoteNutAuthentication
{
    private protected RemoteNutAuthentication()
    {
    }
}

public sealed class RemoteNutPasswordAuthentication : RemoteNutAuthentication
{
    public RemoteNutPasswordAuthentication(ReadOnlyMemory<char> password)
    {
        if (password.IsEmpty)
        {
            throw new ArgumentException("A password is required.", nameof(password));
        }

        Password = password;
    }

    public ReadOnlyMemory<char> Password { get; }
}

public sealed class RemoteNutPrivateKeyAuthentication : RemoteNutAuthentication
{
    public RemoteNutPrivateKeyAuthentication(string privateKeyPath, ReadOnlyMemory<char> passphrase = default)
    {
        PrivateKeyPath = NutMonitoringProfile.ValidateRequiredText(privateKeyPath, nameof(privateKeyPath), 1024);
        Passphrase = passphrase;
    }

    public string PrivateKeyPath { get; }

    public ReadOnlyMemory<char> Passphrase { get; }
}

/// <summary>
/// Base request for a configuration-file transport. It intentionally carries no
/// monitoring endpoint because configuration access never redirects NUT TCP polling.
/// </summary>
public abstract class RemoteNutConfigurationConnectionRequest
{
    protected RemoteNutConfigurationConnectionRequest(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile identifier is required.", nameof(profileId));
        }

        ProfileId = profileId;
    }

    public Guid ProfileId { get; }
}

public sealed class RemoteNutConnectionRequest : RemoteNutConfigurationConnectionRequest
{
    public RemoteNutConnectionRequest(Guid profileId, string host, int port, string username, string? trustedHostKeyFingerprint, RemoteNutAuthentication authentication)
        : base(profileId)
    {
        Host = NutMonitoringProfile.ValidateRequiredText(host, nameof(host), 255);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Port = port;
        Username = NutMonitoringProfile.ValidateRequiredText(username, nameof(username), 255);
        TrustedHostKeyFingerprint = string.IsNullOrWhiteSpace(trustedHostKeyFingerprint) ? null : trustedHostKeyFingerprint.Trim();
        Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    }

    public string Host { get; }

    public int Port { get; }

    public string Username { get; }

    public string? TrustedHostKeyFingerprint { get; }

    public RemoteNutAuthentication Authentication { get; }
}

/// <summary>
/// Session-only SMB connection request. The password is intentionally absent from
/// persisted profile metadata and is never included in diagnostic result models.
/// </summary>
public sealed class SmbRemoteNutConnectionRequest : RemoteNutConfigurationConnectionRequest
{
    public SmbRemoteNutConnectionRequest(
        Guid profileId,
        string sharePath,
        SmbAuthenticationMode authenticationMode,
        string? username,
        ReadOnlyMemory<char> password,
        bool canWrite)
        : base(profileId)
    {
        if (!Enum.IsDefined(authenticationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(authenticationMode));
        }

        SharePath = SmbUncPath.NormalizeShareRoot(sharePath);
        AuthenticationMode = authenticationMode;
        Username = NutMonitoringProfile.NormalizeOptionalText(username, nameof(username), 255);
        if (authenticationMode == SmbAuthenticationMode.ExplicitCredentials && (Username is null || password.IsEmpty))
        {
            throw new ArgumentException("SMB explicit credentials require a username and a session password.");
        }

        Password = password;
        CanWrite = canWrite;
    }

    public string SharePath { get; }

    public SmbAuthenticationMode AuthenticationMode { get; }

    public string? Username { get; }

    public ReadOnlyMemory<char> Password { get; }

    public bool CanWrite { get; }
}

public sealed class RemoteNutConnectionResult
{
    public RemoteNutConnectionResult(RemoteNutConnectionState state, IRemoteNutConfigurationSession? session = null, RemoteNutHostKeyInfo? hostKey = null, string? message = null)
    {
        State = state;
        Session = session;
        HostKey = hostKey;
        Message = message;
    }

    public RemoteNutConnectionState State { get; }

    public IRemoteNutConfigurationSession? Session { get; }

    public RemoteNutHostKeyInfo? HostKey { get; }

    public string? Message { get; }
}

public sealed record RemoteNutDirectoryEntry(string Name, string FullPath, bool IsDirectory, bool IsSymbolicLink);

public sealed class RemoteNutDirectoryListing
{
    public RemoteNutDirectoryListing(string currentPath, string? parentPath, IReadOnlyList<RemoteNutDirectoryEntry> entries)
    {
        CurrentPath = NutMonitoringProfile.ValidateRequiredText(currentPath, nameof(currentPath), 4096);
        ParentPath = parentPath;
        Entries = entries?.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray() ?? throw new ArgumentNullException(nameof(entries));
    }

    public string CurrentPath { get; }

    public string? ParentPath { get; }

    public IReadOnlyList<RemoteNutDirectoryEntry> Entries { get; }
}

public sealed class RemoteNutDirectoryValidationResult
{
    public RemoteNutDirectoryValidationResult(RemoteNutTransportStatus status, string directory, IReadOnlyList<string>? presentFileNames = null, string? message = null)
    {
        Status = status;
        Directory = directory;
        PresentFileNames = presentFileNames?.ToArray() ?? Array.Empty<string>();
        Message = message;
    }

    public RemoteNutTransportStatus Status { get; }

    public string Directory { get; }

    public IReadOnlyList<string> PresentFileNames { get; }

    public string? Message { get; }

    public bool IsValid => Status == RemoteNutTransportStatus.Success;
}

public sealed class RemoteNutFileReadResult
{
    public RemoteNutFileReadResult(RemoteNutTransportStatus status, ReadOnlyMemory<byte> bytes = default, string? message = null)
    {
        Status = status;
        Bytes = bytes;
        Message = message;
    }

    public RemoteNutTransportStatus Status { get; }

    public ReadOnlyMemory<byte> Bytes { get; }

    public string? Message { get; }
}

public sealed class RemoteNutTemporaryCleanupResult
{
    public RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus status, string? message = null)
    {
        Status = status;
        Message = message;
    }

    public RemoteNutTransportStatus Status { get; }

    public string? Message { get; }

    public bool IsClean => Status is RemoteNutTransportStatus.Success or RemoteNutTransportStatus.NotFound;
}

public sealed class RemoteNutWriteCapabilityResult
{
    public RemoteNutWriteCapabilityResult(bool isSupported, RemoteNutPlatform platform, string? cleanupPath = null, string? message = null)
    {
        IsSupported = isSupported;
        Platform = platform;
        CleanupPath = cleanupPath;
        Message = message;
    }

    public bool IsSupported { get; }

    public RemoteNutPlatform Platform { get; }

    public string? CleanupPath { get; }

    public string? Message { get; }
}

public sealed class RemoteNutCandidateUploadRequest
{
    public RemoteNutCandidateUploadRequest(string configurationDirectory, string targetFileName, string temporaryFileName, ReadOnlyMemory<byte> candidateBytes)
    {
        ConfigurationDirectory = NutMonitoringProfile.ValidateRequiredText(configurationDirectory, nameof(configurationDirectory), 4096);
        TargetFileName = NutMonitoringProfile.ValidateRequiredText(targetFileName, nameof(targetFileName), 128);
        TemporaryFileName = NutMonitoringProfile.ValidateRequiredText(temporaryFileName, nameof(temporaryFileName), 256);
        CandidateBytes = candidateBytes;
    }

    public string ConfigurationDirectory { get; }

    public string TargetFileName { get; }

    public string TemporaryFileName { get; }

    public ReadOnlyMemory<byte> CandidateBytes { get; }
}

public sealed class RemoteNutConfigurationCommitRequest
{
    public RemoteNutConfigurationCommitRequest(string configurationDirectory, string targetFileName, string temporaryFileName, string backupFileName, string expectedOriginalFingerprint, string expectedCandidateFingerprint)
    {
        ConfigurationDirectory = NutMonitoringProfile.ValidateRequiredText(configurationDirectory, nameof(configurationDirectory), 4096);
        TargetFileName = NutMonitoringProfile.ValidateRequiredText(targetFileName, nameof(targetFileName), 128);
        TemporaryFileName = NutMonitoringProfile.ValidateRequiredText(temporaryFileName, nameof(temporaryFileName), 256);
        BackupFileName = NutMonitoringProfile.ValidateRequiredText(backupFileName, nameof(backupFileName), 256);
        ExpectedOriginalFingerprint = NutMonitoringProfile.ValidateRequiredText(expectedOriginalFingerprint, nameof(expectedOriginalFingerprint), 128);
        ExpectedCandidateFingerprint = NutMonitoringProfile.ValidateRequiredText(expectedCandidateFingerprint, nameof(expectedCandidateFingerprint), 128);
    }

    public string ConfigurationDirectory { get; }

    public string TargetFileName { get; }

    public string TemporaryFileName { get; }

    public string BackupFileName { get; }

    public string ExpectedOriginalFingerprint { get; }

    public string ExpectedCandidateFingerprint { get; }
}

public sealed class RemoteNutConfigurationRollbackRequest
{
    public RemoteNutConfigurationRollbackRequest(string configurationDirectory, string targetFileName, string backupFileName, string rollbackFileName, string recoveryFileName, string expectedOriginalFingerprint)
    {
        ConfigurationDirectory = NutMonitoringProfile.ValidateRequiredText(configurationDirectory, nameof(configurationDirectory), 4096);
        TargetFileName = NutMonitoringProfile.ValidateRequiredText(targetFileName, nameof(targetFileName), 128);
        BackupFileName = NutMonitoringProfile.ValidateRequiredText(backupFileName, nameof(backupFileName), 256);
        RollbackFileName = NutMonitoringProfile.ValidateRequiredText(rollbackFileName, nameof(rollbackFileName), 256);
        RecoveryFileName = NutMonitoringProfile.ValidateRequiredText(recoveryFileName, nameof(recoveryFileName), 256);
        ExpectedOriginalFingerprint = NutMonitoringProfile.ValidateRequiredText(expectedOriginalFingerprint, nameof(expectedOriginalFingerprint), 128);
    }

    public string ConfigurationDirectory { get; }

    public string TargetFileName { get; }

    public string BackupFileName { get; }

    public string RollbackFileName { get; }

    public string RecoveryFileName { get; }

    public string ExpectedOriginalFingerprint { get; }
}

public sealed class RemoteNutCommitResult
{
    public RemoteNutCommitResult(RemoteNutTransportStatus status, string? backupPath = null, string? recoveryPath = null, string? message = null)
    {
        Status = status;
        BackupPath = backupPath;
        RecoveryPath = recoveryPath;
        Message = message;
    }

    public RemoteNutTransportStatus Status { get; }

    public string? BackupPath { get; }

    public string? RecoveryPath { get; }

    public string? Message { get; }
}

public interface IRemoteNutConfigurationTransport
{
    Task<RemoteNutConnectionResult> ConnectAsync(RemoteNutConfigurationConnectionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies transport-specific remote path text semantics without applying local host
/// filesystem semantics. Implementations belong to their file transport.
/// </summary>
public interface IRemoteNutConfigurationPathPolicy
{
    string NormalizeDirectory(string directory);

    string NormalizePath(string path);

    string CombineDirectChild(string directory, string childName);

    bool PathsEqual(string left, string right);

    string? GetParentDirectory(string directory);
}

/// <summary>
/// Legacy SSH/SFTP name retained for source compatibility. SMB implements the generic
/// configuration transport contract directly and never instantiates an SSH client.
/// </summary>
public interface IRemoteNutManagementTransport : IRemoteNutConfigurationTransport
{
    Task<RemoteNutConnectionResult> ConnectAsync(RemoteNutConnectionRequest request, CancellationToken cancellationToken = default);

    Task<RemoteNutConnectionResult> IRemoteNutConfigurationTransport.ConnectAsync(RemoteNutConfigurationConnectionRequest request, CancellationToken cancellationToken) =>
        request is RemoteNutConnectionRequest sshRequest
            ? ConnectAsync(sshRequest, cancellationToken)
            : Task.FromResult(new RemoteNutConnectionResult(RemoteNutConnectionState.Failed, message: "The selected configuration transport is not supported by SSH/SFTP."));
}

public interface IRemoteNutConfigurationSession : IAsyncDisposable
{
    RemoteNutPlatform Platform { get; }

    /// <summary>
    /// Gets whether this session completed a safe-write capability probe for the exact
    /// validated remote configuration directory. A platform probe alone is insufficient.
    /// </summary>
    bool IsSafeWriteCapabilityValidFor(string configurationDirectory);

    string HomeDirectory { get; }

    IRemoteNutConfigurationPathPolicy PathPolicy { get; }

    Task<RemoteNutDirectoryListing> BrowseDirectoryAsync(string directory, CancellationToken cancellationToken = default);

    Task<RemoteNutDirectoryValidationResult> ValidateConfigurationDirectoryAsync(string directory, CancellationToken cancellationToken = default);

    Task<RemoteNutFileReadResult> ReadFileAsync(string path, CancellationToken cancellationToken = default);

    Task<RemoteNutWriteCapabilityResult> ProbeSafeWriteCapabilityAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prevents further remote writes after an indeterminate commit or rollback outcome.
    /// A new transport session, directory validation, and capability probe are required before writing again.
    /// </summary>
    void InvalidateSafeWriteCapability();

    Task<RemoteNutFileReadResult> UploadCandidateAsync(RemoteNutCandidateUploadRequest request, CancellationToken cancellationToken = default);

    Task<RemoteNutTemporaryCleanupResult> DeleteGeneratedTemporaryFileAsync(string configurationDirectory, string temporaryFileName, CancellationToken cancellationToken = default);

    Task<RemoteNutCommitResult> CommitConfigurationAsync(RemoteNutConfigurationCommitRequest request, CancellationToken cancellationToken = default);

    Task<RemoteNutCommitResult> RollbackConfigurationAsync(RemoteNutConfigurationRollbackRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Receives the persisted profile's current write intent without performing remote I/O or
/// authorizing writes. Implementations must clear any previously verified safe-write capability;
/// enabling write intent only permits a new explicit capability probe.
/// </summary>
public interface IRemoteNutWriteIntentSession
{
    void ApplyWriteIntent(bool canWrite);
}

public interface IRemoteNutManagementSession : IRemoteNutConfigurationSession
{
}
