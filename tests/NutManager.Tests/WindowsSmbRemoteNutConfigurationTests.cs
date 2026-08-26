using System.Text;
using System.Security.Cryptography;
using NutManager.Core.Configuration;
using NutManager.Core.Models;
using NutManager.Core.Services;
using NutManager.Infrastructure.Configuration;
using NutManager.Infrastructure.Remote.Smb;
using Xunit;

namespace NutManager.Tests;

public sealed class WindowsSmbRemoteNutConfigurationTests
{
    private const string Share = @"\\server\share";
    private const string ConfigurationDirectory = @"\\server\share\NUT\etc";

    [Theory]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"\\SERVER\Share\NUT\etc", @"\\SERVER\Share\NUT\etc")]
    public void SmbUncPathsNormalizeWithoutHostFilesystemSemantics(string input, string expected) =>
        Assert.Equal(expected, SmbUncPath.NormalizeUncPath(input));

    [Theory]
    [InlineData(@"C:\NUT\etc")]
    [InlineData(@"\\server")]
    [InlineData(@"relative\share")]
    [InlineData(@"\\server\share\..\other")]
    public void InvalidSmbShareRootsAreRejected(string path) =>
        Assert.Throws<ArgumentException>(() => SmbUncPath.NormalizeShareRoot(path));

    [Fact]
    public void SmbConfigurationDirectoryCannotEscapeItsConfiguredShare()
    {
        Assert.Equal(ConfigurationDirectory, SmbUncPath.NormalizeConfigurationDirectory(Share, ConfigurationDirectory));
        Assert.Throws<ArgumentException>(() => SmbUncPath.NormalizeConfigurationDirectory(Share, @"\\other\share\NUT\etc"));
        Assert.False(SmbUncPath.IsWithinShare(Share, @"\\server\other\NUT\etc"));
    }

    [Fact]
    public async Task CurrentWindowsIdentityDoesNotCreateAnIsolatedToken()
    {
        var identities = new FakeIdentityFactory();
        var transport = new WindowsSmbRemoteNutConfigurationTransport(new FakeSmbFileSystem(), identities, () => true);
        var request = new SmbRemoteNutConnectionRequest(Guid.NewGuid(), Share, SmbAuthenticationMode.CurrentWindowsIdentity, null, default, true);

        var result = await transport.ConnectAsync(request);

        Assert.Equal(RemoteNutConnectionState.Connected, result.State);
        Assert.Equal(0, identities.ExplicitIdentityCalls);
        await result.Session!.DisposeAsync();
        Assert.False(identities.CurrentIdentity.IsExplicitCredentialIdentity);
    }

    [Fact]
    public async Task ExplicitSmbCredentialsCreateAndDisposeAnIsolatedToken()
    {
        var identities = new FakeIdentityFactory();
        var transport = new WindowsSmbRemoteNutConfigurationTransport(new FakeSmbFileSystem(), identities, () => true);
        var request = new SmbRemoteNutConnectionRequest(Guid.NewGuid(), Share, SmbAuthenticationMode.ExplicitCredentials, "DOMAIN\\nut", "fictional-password".AsMemory(), true);

        var result = await transport.ConnectAsync(request);

        Assert.Equal(RemoteNutConnectionState.Connected, result.State);
        Assert.Equal(1, identities.ExplicitIdentityCalls);
        Assert.Equal(Share, identities.LastShare);
        Assert.Equal("DOMAIN\\nut", identities.LastUsername);
        Assert.True(identities.ExplicitIdentity.RunCalls > 0);
        Assert.DoesNotContain("fictional-password", result.Message ?? string.Empty, StringComparison.Ordinal);
        await result.Session!.DisposeAsync();
        Assert.Equal(1, identities.ExplicitIdentity.DisposeCalls);
    }

    [Fact]
    public async Task FailedShareVerificationDoesNotReturnAUsableSession()
    {
        var identities = new FakeIdentityFactory();
        var transport = new WindowsSmbRemoteNutConfigurationTransport(new FakeSmbFileSystem { ListThrowsUnauthorized = true }, identities, () => true);
        var request = new SmbRemoteNutConnectionRequest(Guid.NewGuid(), Share, SmbAuthenticationMode.ExplicitCredentials, "DOMAIN\\nut", "fictional-password".AsMemory(), true);

        var result = await transport.ConnectAsync(request);

        Assert.Equal(RemoteNutConnectionState.AccessDenied, result.State);
        Assert.Null(result.Session);
        Assert.Equal(1, identities.ExplicitIdentity.DisposeCalls);
    }

    [Fact]
    public async Task RedirectorCredentialConflictFailsClosedWithoutDisconnectingAnything()
    {
        var identities = new FakeIdentityFactory();
        var transport = new WindowsSmbRemoteNutConfigurationTransport(
            new FakeSmbFileSystem { ListException = new CredentialConflictIOException() },
            identities,
            () => true);
        var request = new SmbRemoteNutConnectionRequest(Guid.NewGuid(), Share, SmbAuthenticationMode.ExplicitCredentials, "DOMAIN\\nut", "fictional-password".AsMemory(), true);

        var result = await transport.ConnectAsync(request);

        Assert.Equal(RemoteNutConnectionState.AuthenticationFailed, result.State);
        Assert.Contains("conflito de credenciais", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Session);
        Assert.Equal(1, identities.ExplicitIdentity.DisposeCalls);
    }

    [Fact]
    public async Task NativeLogonPasswordBufferIsZeroedAfterTheAttempt()
    {
        var nativeLogon = new RecordingNativeLogon();
        var factory = new WindowsSmbSessionIdentityFactory(nativeLogon);

        var result = await factory.CreateExplicitIdentityAsync(Share, "DOMAIN\\nut", "fictional-password".AsMemory(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(nativeLogon.PasswordBuffer);
        Assert.All(nativeLogon.PasswordBuffer!, character => Assert.Equal('\0', character));
    }

    [Theory]
    [InlineData("DOMAIN\\nut", "nut", "DOMAIN")]
    [InlineData("SERVER\\nut", "nut", "SERVER")]
    [InlineData("nut@domain.example", "nut@domain.example", null)]
    [InlineData("nut", "nut", "server")]
    public async Task ExplicitSmbUsernamesUseDeterministicOutboundAuthorities(string username, string expectedAccount, string? expectedAuthority)
    {
        var nativeLogon = new RecordingNativeLogon();
        var factory = new WindowsSmbSessionIdentityFactory(nativeLogon);

        await factory.CreateExplicitIdentityAsync(Share, username, "fictional-password".AsMemory(), CancellationToken.None);

        Assert.Equal(expectedAccount, nativeLogon.AccountName);
        Assert.Equal(expectedAuthority, nativeLogon.Authority);
    }

    [Fact]
    public async Task SafeWriteProbeBindsOnlyItsExactValidatedSmbDirectory()
    {
        var fileSystem = new FakeSmbFileSystem();
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);

        var probe = await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory);

        Assert.True(probe.IsSupported);
        Assert.True(session.IsSafeWriteCapabilityValidFor(ConfigurationDirectory));
        Assert.False(session.IsSafeWriteCapabilityValidFor(@"\\server\share\other"));
        Assert.Equal(1, fileSystem.ReplaceCalls);
        Assert.DoesNotContain(fileSystem.FilePaths, path => path.Contains("capability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SmbSafeWriteDirectoryComparisonUsesUncCaseInsensitivity()
    {
        var fileSystem = new FakeSmbFileSystem();
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);

        Assert.True(session.IsSafeWriteCapabilityValidFor(@"\\SERVER\SHARE\nut\ETC"));
    }

    [Fact]
    public async Task ProbeFailureOrCleanupFailureDoesNotEnableSmbWrites()
    {
        var unsupported = new FakeSmbFileSystem { ReplaceThrows = true };
        var unsupportedSession = CreateSession(unsupported);
        await unsupportedSession.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.False((await unsupportedSession.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        Assert.False(unsupportedSession.IsSafeWriteCapabilityValidFor(ConfigurationDirectory));

        var cleanupFailure = new FakeSmbFileSystem { FailCapabilityCleanup = true };
        var cleanupSession = CreateSession(cleanupFailure);
        await cleanupSession.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        var cleanup = await cleanupSession.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory);
        Assert.False(cleanup.IsSupported);
        Assert.NotNull(cleanup.CleanupPath);
        Assert.False(cleanupSession.IsSafeWriteCapabilityValidFor(ConfigurationDirectory));
    }

    [Fact]
    public async Task ManageProfileCannotCreateCandidateBeforeSafeWriteProbe()
    {
        var fileSystem = new FakeSmbFileSystem();
        fileSystem.SetFile(SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf"), "MODE=standalone\n");
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        var pipeline = new RemoteNutConfigurationFilePipeline(session, ConfigurationDirectory, true);
        var load = await pipeline.LoadAsync(SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf"), NutConfigurationFileKind.NutConf);
        var snapshot = Assert.IsType<NutConfigurationFileSnapshot>(load.Snapshot);
        Assert.IsType<NutConfigurationAssignmentNode>(snapshot.Document.Nodes.Single()).SetValue("netserver");

        var result = await pipeline.ApplyAsync(pipeline.Prepare(snapshot));

        Assert.Equal(NutConfigurationApplyStatus.Failed, result.Status);
        Assert.Equal(0, fileSystem.ReplaceCalls);
        Assert.DoesNotContain(fileSystem.FilePaths, path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SmbPipelineUsesCreateNewReplaceBackupAndVerification()
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        fileSystem.SetFile(targetPath, "MODE=standalone\n");
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        var pipeline = new RemoteNutConfigurationFilePipeline(session, ConfigurationDirectory, true);
        var load = await pipeline.LoadAsync(targetPath, NutConfigurationFileKind.NutConf);
        var snapshot = Assert.IsType<NutConfigurationFileSnapshot>(load.Snapshot);
        Assert.IsType<NutConfigurationAssignmentNode>(snapshot.Document.Nodes.Single()).SetValue("netserver");

        var result = await pipeline.ApplyAsync(pipeline.Prepare(snapshot));

        Assert.Equal(NutConfigurationApplyStatus.Success, result.Status);
        Assert.Equal("MODE=netserver\n", fileSystem.GetText(targetPath));
        Assert.NotNull(result.BackupPath);
        Assert.Equal("MODE=standalone\n", fileSystem.GetText(result.BackupPath!));
        Assert.True(fileSystem.ReplaceCalls >= 2);
    }

    [Fact]
    public async Task OutcomeUnknownInvalidatesSmbWriteCapabilityWithoutRetry()
    {
        var fileSystem = new FakeSmbFileSystem();
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);

        session.InvalidateSafeWriteCapability();

        Assert.False(session.IsSafeWriteCapabilityValidFor(ConfigurationDirectory));
        Assert.False((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
    }

    [Fact]
    public async Task SmbCommitRejectsExternalTargetChangeBeforeReplace()
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        var candidate = Encoding.UTF8.GetBytes("MODE=netserver\n");
        fileSystem.SetFile(targetPath, Encoding.UTF8.GetString(original));
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        await session.UploadCandidateAsync(new RemoteNutCandidateUploadRequest(ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", candidate));
        fileSystem.SetFile(targetPath, "MODE=external\n");

        var result = await session.CommitConfigurationAsync(new RemoteNutConfigurationCommitRequest(
            ConfigurationDirectory,
            "nut.conf",
            ".nutmanager-nut.conf-candidate.tmp",
            ".nutmanager-nut.conf-original.bak",
            Fingerprint(original),
            Fingerprint(candidate)));

        Assert.Equal(RemoteNutTransportStatus.Failed, result.Status);
        Assert.Equal("MODE=external\n", fileSystem.GetText(targetPath));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("candidate")]
    public async Task SmbCommitFinalRevalidationRejectsExternalParticipantChangesWithoutReplacing(string participant)
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var candidatePath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, ".nutmanager-nut.conf-candidate.tmp");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        var candidate = Encoding.UTF8.GetBytes("MODE=netserver\n");
        fileSystem.SetFile(targetPath, Encoding.UTF8.GetString(original));
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        await session.UploadCandidateAsync(new RemoteNutCandidateUploadRequest(ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", candidate));
        var replacesBeforeCommit = fileSystem.ReplaceCalls;
        fileSystem.AfterWriteNewFile = path =>
        {
            if (path.EndsWith("original.bak", StringComparison.OrdinalIgnoreCase))
            {
                fileSystem.SetFile(participant == "target" ? targetPath : candidatePath, "MODE=external\n");
            }
        };

        var result = await session.CommitConfigurationAsync(new RemoteNutConfigurationCommitRequest(
            ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", ".nutmanager-nut.conf-original.bak", Fingerprint(original), Fingerprint(candidate)));

        Assert.Equal(RemoteNutTransportStatus.Failed, result.Status);
        Assert.Equal(replacesBeforeCommit, fileSystem.ReplaceCalls);
        Assert.Equal("MODE=external\n", fileSystem.GetText(participant == "target" ? targetPath : candidatePath));
    }

    [Fact]
    public async Task SmbCommitFinalRevalidationPreservesAnExternallyChangedBackupReservation()
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var backupPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, ".nutmanager-nut.conf-original.bak");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        var candidate = Encoding.UTF8.GetBytes("MODE=netserver\n");
        fileSystem.SetFile(targetPath, Encoding.UTF8.GetString(original));
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        await session.UploadCandidateAsync(new RemoteNutCandidateUploadRequest(ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", candidate));
        var replacesBeforeCommit = fileSystem.ReplaceCalls;
        fileSystem.AfterWriteNewFile = path =>
        {
            if (path.EndsWith("original.bak", StringComparison.OrdinalIgnoreCase))
            {
                fileSystem.SetFile(backupPath, "EXTERNAL BACKUP");
            }
        };

        var result = await session.CommitConfigurationAsync(new RemoteNutConfigurationCommitRequest(
            ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", ".nutmanager-nut.conf-original.bak", Fingerprint(original), Fingerprint(candidate)));

        Assert.Equal(RemoteNutTransportStatus.OutcomeUnknown, result.Status);
        Assert.Equal(replacesBeforeCommit, fileSystem.ReplaceCalls);
        Assert.Equal("EXTERNAL BACKUP", fileSystem.GetText(backupPath));
        Assert.False(session.IsSafeWriteCapabilityValidFor(ConfigurationDirectory));
    }

    [Fact]
    public async Task SmbRollbackRestoresOriginalAndPreservesReplacedContentInRecoveryBackup()
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var backupName = ".nutmanager-nut.conf-original.bak";
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        fileSystem.SetFile(targetPath, "MODE=netserver\n");
        fileSystem.SetFile(SmbUncPath.CombineDirectChild(ConfigurationDirectory, backupName), Encoding.UTF8.GetString(original));
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);

        var result = await session.RollbackConfigurationAsync(new RemoteNutConfigurationRollbackRequest(
            ConfigurationDirectory,
            "nut.conf",
            backupName,
            ".nutmanager-nut.conf-rollback.tmp",
            ".nutmanager-nut.conf-recovery.bak",
            Fingerprint(original)));

        Assert.Equal(RemoteNutTransportStatus.Success, result.Status);
        Assert.Equal("MODE=standalone\n", fileSystem.GetText(targetPath));
        Assert.Equal("MODE=netserver\n", fileSystem.GetText(result.RecoveryPath!));
    }

    [Fact]
    public async Task SmbCommitPerformsTheFinalReparseChecksBeforeTheFinalReadsAndReplace()
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        var candidate = Encoding.UTF8.GetBytes("MODE=netserver\n");
        fileSystem.SetFile(targetPath, Encoding.UTF8.GetString(original));
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        await session.UploadCandidateAsync(new RemoteNutCandidateUploadRequest(ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", candidate));
        fileSystem.BeginOperationTraceAfterWriteNewPathSuffix = "original.bak";

        var result = await session.CommitConfigurationAsync(new RemoteNutConfigurationCommitRequest(
            ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", ".nutmanager-nut.conf-original.bak", Fingerprint(original), Fingerprint(candidate)));

        Assert.Equal(RemoteNutTransportStatus.Success, result.Status);
        Assert.Equal(
            new[]
            {
                "Reparse:nut.conf",
                "Reparse:.nutmanager-nut.conf-candidate.tmp",
                "Reparse:.nutmanager-nut.conf-original.bak",
                "Read:nut.conf",
                "Read:.nutmanager-nut.conf-candidate.tmp",
                "Read:.nutmanager-nut.conf-original.bak",
                "Replace:.nutmanager-nut.conf-candidate.tmp"
            },
            fileSystem.OperationTrace.Take(7));
    }

    [Fact]
    public async Task SmbRollbackPerformsTheFinalReparseChecksBeforeTheFinalReadsAndReplace()
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var backupPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, ".nutmanager-nut.conf-original.bak");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        fileSystem.SetFile(targetPath, "MODE=netserver\n");
        fileSystem.SetFile(backupPath, Encoding.UTF8.GetString(original));
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        fileSystem.BeginOperationTraceAfterWriteNewPathSuffix = "recovery.bak";

        var result = await session.RollbackConfigurationAsync(new RemoteNutConfigurationRollbackRequest(
            ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-original.bak", ".nutmanager-nut.conf-rollback.tmp", ".nutmanager-nut.conf-recovery.bak", Fingerprint(original)));

        Assert.Equal(RemoteNutTransportStatus.Success, result.Status);
        Assert.Equal(
            new[]
            {
                "Reparse:nut.conf",
                "Reparse:.nutmanager-nut.conf-rollback.tmp",
                "Reparse:.nutmanager-nut.conf-recovery.bak",
                "Read:.nutmanager-nut.conf-original.bak",
                "Read:.nutmanager-nut.conf-rollback.tmp",
                "Read:nut.conf",
                "Read:.nutmanager-nut.conf-recovery.bak",
                "Replace:.nutmanager-nut.conf-rollback.tmp"
            },
            fileSystem.OperationTrace.Take(8));
    }

    [Theory]
    [InlineData("target", RemoteNutTransportStatus.Failed)]
    [InlineData("backup", RemoteNutTransportStatus.Failed)]
    [InlineData("rollback", RemoteNutTransportStatus.OutcomeUnknown)]
    [InlineData("recovery", RemoteNutTransportStatus.OutcomeUnknown)]
    public async Task SmbRollbackFinalRevalidationRejectsExternalParticipantChangesWithoutReplacing(string participant, RemoteNutTransportStatus expectedStatus)
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var backupPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, ".nutmanager-nut.conf-original.bak");
        var rollbackPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, ".nutmanager-nut.conf-rollback.tmp");
        var recoveryPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, ".nutmanager-nut.conf-recovery.bak");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        fileSystem.SetFile(targetPath, "MODE=netserver\n");
        fileSystem.SetFile(backupPath, Encoding.UTF8.GetString(original));
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        var replacesBeforeRollback = fileSystem.ReplaceCalls;
        fileSystem.AfterWriteNewFile = path =>
        {
            if (!path.EndsWith("recovery.bak", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var changedPath = participant switch
            {
                "target" => targetPath,
                "backup" => backupPath,
                "rollback" => rollbackPath,
                _ => recoveryPath
            };
            fileSystem.SetFile(changedPath, "EXTERNAL CONTENT");
        };

        var result = await session.RollbackConfigurationAsync(new RemoteNutConfigurationRollbackRequest(
            ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-original.bak", ".nutmanager-nut.conf-rollback.tmp", ".nutmanager-nut.conf-recovery.bak", Fingerprint(original)));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(replacesBeforeRollback, fileSystem.ReplaceCalls);
        Assert.Equal("EXTERNAL CONTENT", fileSystem.GetText(participant switch
        {
            "target" => targetPath,
            "backup" => backupPath,
            "rollback" => rollbackPath,
            _ => recoveryPath
        }));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("recovery")]
    public async Task SmbRollbackPostReplaceVerificationFailureIsCriticalAndDoesNotRetry(string participant)
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var backupPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, ".nutmanager-nut.conf-original.bak");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        fileSystem.SetFile(targetPath, "MODE=netserver\n");
        fileSystem.SetFile(backupPath, Encoding.UTF8.GetString(original));
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        var replacesBeforeRollback = fileSystem.ReplaceCalls;
        fileSystem.AfterReplace = (_, target, recovery) => fileSystem.SetFile(participant == "target" ? target : recovery, "EXTERNAL CONTENT");

        var result = await session.RollbackConfigurationAsync(new RemoteNutConfigurationRollbackRequest(
            ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-original.bak", ".nutmanager-nut.conf-rollback.tmp", ".nutmanager-nut.conf-recovery.bak", Fingerprint(original)));

        Assert.Equal(RemoteNutTransportStatus.OutcomeUnknown, result.Status);
        Assert.Equal(replacesBeforeRollback + 1, fileSystem.ReplaceCalls);
        Assert.False(session.IsSafeWriteCapabilityValidFor(ConfigurationDirectory));
    }

    [Fact]
    public async Task CancelledSmbProbeDoesNotCreateWriteCapability()
    {
        var session = CreateSession(new FakeSmbFileSystem());
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory, cancellation.Token));

        Assert.False(session.IsSafeWriteCapabilityValidFor(ConfigurationDirectory));
    }

    [Fact]
    public async Task CommitDoesNotOverwriteAPreexistingGeneratedBackup()
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var backupPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, ".nutmanager-nut.conf-existing.bak");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        var candidate = Encoding.UTF8.GetBytes("MODE=netserver\n");
        fileSystem.SetFile(targetPath, Encoding.UTF8.GetString(original));
        fileSystem.SetFile(backupPath, "EXTERNAL BACKUP");
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        await session.UploadCandidateAsync(new RemoteNutCandidateUploadRequest(ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", candidate));

        var result = await session.CommitConfigurationAsync(new RemoteNutConfigurationCommitRequest(
            ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", ".nutmanager-nut.conf-existing.bak", Fingerprint(original), Fingerprint(candidate)));

        Assert.Equal(RemoteNutTransportStatus.Failed, result.Status);
        Assert.Equal("EXTERNAL BACKUP", fileSystem.GetText(backupPath));
        Assert.Equal("MODE=standalone\n", fileSystem.GetText(targetPath));
    }

    [Fact]
    public async Task RollbackDoesNotOverwriteAPreexistingGeneratedRecovery()
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var backupName = ".nutmanager-nut.conf-original.bak";
        var recoveryPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, ".nutmanager-nut.conf-existing-recovery.bak");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        fileSystem.SetFile(targetPath, "MODE=netserver\n");
        fileSystem.SetFile(SmbUncPath.CombineDirectChild(ConfigurationDirectory, backupName), Encoding.UTF8.GetString(original));
        fileSystem.SetFile(recoveryPath, "EXTERNAL RECOVERY");
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);

        var result = await session.RollbackConfigurationAsync(new RemoteNutConfigurationRollbackRequest(
            ConfigurationDirectory, "nut.conf", backupName, ".nutmanager-nut.conf-rollback.tmp", ".nutmanager-nut.conf-existing-recovery.bak", Fingerprint(original)));

        Assert.Equal(RemoteNutTransportStatus.Failed, result.Status);
        Assert.Equal("EXTERNAL RECOVERY", fileSystem.GetText(recoveryPath));
        Assert.Equal("MODE=netserver\n", fileSystem.GetText(targetPath));
    }

    [Fact]
    public async Task KnownPreReplaceFailureCleansOnlyItsOwnedBackupReservation()
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        var candidate = Encoding.UTF8.GetBytes("MODE=netserver\n");
        fileSystem.SetFile(targetPath, Encoding.UTF8.GetString(original));
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        await session.UploadCandidateAsync(new RemoteNutCandidateUploadRequest(ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", candidate));
        fileSystem.ReplaceThrows = true;

        var result = await session.CommitConfigurationAsync(new RemoteNutConfigurationCommitRequest(
            ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", ".nutmanager-nut.conf-original.bak", Fingerprint(original), Fingerprint(candidate)));

        Assert.Equal(RemoteNutTransportStatus.Failed, result.Status);
        Assert.DoesNotContain(SmbUncPath.CombineDirectChild(ConfigurationDirectory, ".nutmanager-nut.conf-original.bak"), fileSystem.FilePaths);
        Assert.Equal("MODE=standalone\n", fileSystem.GetText(targetPath));
    }

    [Fact]
    public async Task ReservationCleanupFailureIsCriticalAndPreservesTheOwnedPathForReview()
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        var candidate = Encoding.UTF8.GetBytes("MODE=netserver\n");
        fileSystem.SetFile(targetPath, Encoding.UTF8.GetString(original));
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        await session.UploadCandidateAsync(new RemoteNutCandidateUploadRequest(ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", candidate));
        fileSystem.ReplaceThrows = true;
        fileSystem.FailDeletePathsContaining = "original.bak";

        var result = await session.CommitConfigurationAsync(new RemoteNutConfigurationCommitRequest(
            ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", ".nutmanager-nut.conf-original.bak", Fingerprint(original), Fingerprint(candidate)));

        Assert.Equal(RemoteNutTransportStatus.OutcomeUnknown, result.Status);
        Assert.EndsWith("original.bak", result.BackupPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.BackupPath!, fileSystem.FilePaths);
    }

    [Fact]
    public async Task MutationCancellationAfterReplaceStartsWaitsForTheActualReplace()
    {
        var fileSystem = new FakeSmbFileSystem();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        var candidate = Encoding.UTF8.GetBytes("MODE=netserver\n");
        fileSystem.SetFile(targetPath, Encoding.UTF8.GetString(original));
        var session = CreateSession(fileSystem);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        fileSystem.EnableReplaceBlock();
        await session.UploadCandidateAsync(new RemoteNutCandidateUploadRequest(ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", candidate));
        using var cancellation = new CancellationTokenSource();

        var commit = session.CommitConfigurationAsync(new RemoteNutConfigurationCommitRequest(
            ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", ".nutmanager-nut.conf-original.bak", Fingerprint(original), Fingerprint(candidate)), cancellation.Token);
        await fileSystem.ReplaceStarted.Task;
        cancellation.Cancel();
        Assert.False(commit.IsCompleted);
        fileSystem.AllowReplace.TrySetResult();

        Assert.Equal(RemoteNutTransportStatus.Success, (await commit).Status);
    }

    [Fact]
    public async Task ReadOnlySessionCanProbeOnlyAfterPersistedWriteIntentIsApplied()
    {
        var fileSystem = new FakeSmbFileSystem();
        var session = new WindowsSmbRemoteNutConfigurationSession(Share, false, fileSystem, new FakeIdentity(false));
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);

        Assert.False((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);

        session.ApplyWriteIntent(true);

        Assert.False(session.IsSafeWriteCapabilityValidFor(ConfigurationDirectory));
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
    }

    [Fact]
    public async Task DisposeWaitsForTheSessionMutationBeforeDisposingItsIdentity()
    {
        var fileSystem = new FakeSmbFileSystem();
        var identity = new FakeIdentity(true);
        var session = new WindowsSmbRemoteNutConfigurationSession(Share, true, fileSystem, identity);
        await session.ValidateConfigurationDirectoryAsync(ConfigurationDirectory);
        Assert.True((await session.ProbeSafeWriteCapabilityAsync(ConfigurationDirectory)).IsSupported);
        fileSystem.EnableReplaceBlock();
        var targetPath = SmbUncPath.CombineDirectChild(ConfigurationDirectory, "nut.conf");
        var original = Encoding.UTF8.GetBytes("MODE=standalone\n");
        var candidate = Encoding.UTF8.GetBytes("MODE=netserver\n");
        fileSystem.SetFile(targetPath, Encoding.UTF8.GetString(original));
        await session.UploadCandidateAsync(new RemoteNutCandidateUploadRequest(ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", candidate));
        var commit = session.CommitConfigurationAsync(new RemoteNutConfigurationCommitRequest(
            ConfigurationDirectory, "nut.conf", ".nutmanager-nut.conf-candidate.tmp", ".nutmanager-nut.conf-original.bak", Fingerprint(original), Fingerprint(candidate)));
        await fileSystem.ReplaceStarted.Task;
        var dispose = session.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.Equal(0, identity.DisposeCalls);
        fileSystem.AllowReplace.TrySetResult();
        await commit;
        await dispose;
        Assert.Equal(1, identity.DisposeCalls);
    }

    private static WindowsSmbRemoteNutConfigurationSession CreateSession(FakeSmbFileSystem fileSystem) =>
        new(Share, true, fileSystem, new FakeIdentity(false));

    private static string Fingerprint(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class FakeIdentityFactory : IWindowsSmbSessionIdentityFactory
    {
        public int ExplicitIdentityCalls { get; private set; }
        public string? LastShare { get; private set; }
        public string? LastUsername { get; private set; }
        public FakeIdentity CurrentIdentity { get; } = new(false);
        public FakeIdentity ExplicitIdentity { get; } = new(true);

        public IWindowsSmbSessionIdentity CreateCurrentIdentity() => CurrentIdentity;

        public Task<WindowsSmbIdentityCreationResult> CreateExplicitIdentityAsync(string sharePath, string username, ReadOnlyMemory<char> password, CancellationToken cancellationToken)
        {
            ExplicitIdentityCalls++;
            LastShare = sharePath;
            LastUsername = username;
            return Task.FromResult(new WindowsSmbIdentityCreationResult(ExplicitIdentity));
        }
    }

    private sealed class FakeIdentity : IWindowsSmbSessionIdentity
    {
        public FakeIdentity(bool isExplicitCredentialIdentity) => IsExplicitCredentialIdentity = isExplicitCredentialIdentity;

        public bool IsExplicitCredentialIdentity { get; }
        public int RunCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            RunCalls++;
            return await operation(cancellationToken);
        }

        public async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        {
            RunCalls++;
            await operation(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingNativeLogon : IWindowsSmbNativeLogon
    {
        public char[]? PasswordBuffer { get; private set; }
        public string? AccountName { get; private set; }
        public string? Authority { get; private set; }

        public bool TryLogon(string accountName, string? authority, char[] passwordBuffer, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token)
        {
            AccountName = accountName;
            Authority = authority;
            PasswordBuffer = passwordBuffer;
            token = null!;
            return false;
        }
    }

    private sealed class CredentialConflictIOException : IOException
    {
        public CredentialConflictIOException() : base("credential conflict") => HResult = unchecked((int)0x800704C3);
    }

    private sealed class FakeSmbFileSystem : ISmbFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public bool ReplaceThrows { get; set; }
        public bool FailCapabilityCleanup { get; set; }
        public bool ListThrowsUnauthorized { get; set; }
        public Exception? ListException { get; set; }
        public string? FailDeletePathsContaining { get; set; }
        public bool BlockReplace { get; private set; }
        public int ReplaceCalls { get; private set; }
        public Action<string>? AfterWriteNewFile { get; set; }
        public Action<string, string, string>? AfterReplace { get; set; }
        public string? BeginOperationTraceAfterWriteNewPathSuffix { get; set; }
        public List<string> OperationTrace { get; } = [];
        public IReadOnlyCollection<string> FilePaths => _files.Keys;
        public TaskCompletionSource ReplaceStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowReplace { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetFile(string path, string value) => _files[path] = Encoding.UTF8.GetBytes(value);
        public string GetText(string path) => Encoding.UTF8.GetString(_files[path]);

        public void EnableReplaceBlock()
        {
            BlockReplace = true;
            ReplaceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            AllowReplace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task<IReadOnlyList<SmbFileSystemEntry>> ListDirectoryAsync(string directory, CancellationToken cancellationToken)
        {
            if (ListThrowsUnauthorized)
            {
                throw new UnauthorizedAccessException();
            }

            if (ListException is not null)
            {
                throw ListException;
            }

            var prefix = directory.TrimEnd('\\') + "\\";
            var entries = _files.Keys.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path[prefix.Length..].IndexOf('\\') < 0)
                .Select(path => new SmbFileSystemEntry(path[prefix.Length..], path, false, false))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Task.FromResult<IReadOnlyList<SmbFileSystemEntry>>(entries);
        }

        public Task<ReadOnlyMemory<byte>> ReadFileAsync(string path, CancellationToken cancellationToken)
        {
            TraceOperation("Read", path);
            if (!_files.TryGetValue(path, out var bytes))
            {
                throw new FileNotFoundException();
            }

            return Task.FromResult<ReadOnlyMemory<byte>>(bytes.ToArray());
        }

        public Task WriteNewFileAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (!_files.TryAdd(path, bytes.ToArray()))
            {
                throw new IOException("CreateNew collision");
            }

            if (!string.IsNullOrWhiteSpace(BeginOperationTraceAfterWriteNewPathSuffix) &&
                path.EndsWith(BeginOperationTraceAfterWriteNewPathSuffix, StringComparison.OrdinalIgnoreCase))
            {
                OperationTrace.Clear();
                BeginOperationTraceAfterWriteNewPathSuffix = null;
            }

            AfterWriteNewFile?.Invoke(path);

            return Task.CompletedTask;
        }

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(_files.ContainsKey(path));

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
        {
            if ((FailCapabilityCleanup && path.Contains("capability", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(FailDeletePathsContaining) && path.Contains(FailDeletePathsContaining, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException("cleanup failure");
            }

            _files.Remove(path);
            return Task.CompletedTask;
        }

        public async Task ReplaceFileAsync(string candidatePath, string targetPath, string backupPath, CancellationToken cancellationToken)
        {
            TraceOperation("Replace", candidatePath);
            ReplaceCalls++;
            if (ReplaceThrows)
            {
                throw new IOException("unsupported");
            }

            if (BlockReplace)
            {
                ReplaceStarted.TrySetResult();
                await AllowReplace.Task;
                BlockReplace = false;
            }

            _files[backupPath] = _files[targetPath].ToArray();
            _files[targetPath] = _files[candidatePath].ToArray();
            _files.Remove(candidatePath);
            AfterReplace?.Invoke(candidatePath, targetPath, backupPath);
        }

        public Task<bool> IsReparsePointAsync(string path, CancellationToken cancellationToken)
        {
            TraceOperation("Reparse", path);
            return Task.FromResult(false);
        }

        private void TraceOperation(string operation, string path)
        {
            if (BeginOperationTraceAfterWriteNewPathSuffix is null)
            {
                OperationTrace.Add($"{operation}:{path[(path.LastIndexOf('\\') + 1)..]}");
            }
        }
    }
}
