using NutManager.App.Services;
using NutManager.App.ViewModels;
using NutManager.Core.Configuration;
using NutManager.Core.Models;
using NutManager.Core.Services;
using NutManager.Infrastructure.Remote.Ssh;
using NutManager.Infrastructure.Remote.Smb;
using Xunit;

namespace NutManager.Tests;

public sealed class RemoteManagementSessionViewModelTests
{
    private const string CanonicalFingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task UnknownHostKeyRequiresExplicitTrustAndPersistsOnlyFingerprintMetadata()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.Manage);
        var store = new RecordingStore(new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, profile.Id, [profile]));
        var transport = new FakeTransport(new RemoteNutConnectionResult(
            RemoteNutConnectionState.HostKeyTrustRequired,
            hostKey: new RemoteNutHostKeyInfo("management.example", 22, "ssh-ed25519", CanonicalFingerprint)));
        var viewModel = new RemoteManagementSessionViewModel(profile, transport, new ManagedNutServerProfileUpdateService(store));

        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory());

        Assert.Equal(RemoteNutConnectionState.HostKeyTrustRequired, viewModel.ConnectionState);
        Assert.True(viewModel.CanTrustHostKey);
        Assert.Equal("ssh-ed25519", viewModel.PresentedHostKey!.Algorithm);
        Assert.Equal(CanonicalFingerprint, viewModel.PresentedHostKey.Fingerprint);
        await viewModel.TrustPresentedHostKeyAsync();

        Assert.Equal(CanonicalFingerprint, viewModel.TrustedHostKeyFingerprint);
        Assert.NotNull(store.Saved);
        Assert.Equal(CanonicalFingerprint, store.Saved!.ActiveProfile.Management.TrustedHostKeyFingerprint);
        Assert.DoesNotContain("fictional-password", store.Saved.ActiveProfile.Management.TrustedHostKeyFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyRemoteSessionCanValidateAndReadButCannotProbeOrEdit()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.ReadOnly);
        var session = new FakeSession(RemoteNutPlatform.Windows);
        var viewModel = new RemoteManagementSessionViewModel(profile, new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, session)));

        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory());
        await viewModel.ValidateCurrentDirectoryAsync();

        Assert.True(viewModel.CanReadConfiguration);
        Assert.False(viewModel.CanProbeWriteCapability);
        Assert.False(viewModel.CanEditConfiguration);
    }

    [Fact]
    public async Task SuccessfulSshConnectionValidatesTheConfiguredDirectoryAutomatically()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.ReadOnly);
        var session = new FakeSession(RemoteNutPlatform.Windows);
        var viewModel = new RemoteManagementSessionViewModel(
            profile,
            new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, session)));

        Assert.False(viewModel.ShowsDirectoryBrowser);
        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory());

        Assert.Equal(1, session.ValidateCalls);
        Assert.Equal(RemoteNutConnectionState.Ready, viewModel.ConnectionState);
        Assert.True(viewModel.IsDirectoryValidated);
        Assert.True(viewModel.ShowsDirectoryBrowser);

        await viewModel.DisconnectAsync();

        Assert.False(viewModel.ShowsDirectoryBrowser);
    }

    [Fact]
    public async Task HostKeyMismatchNeverPersistsThePresentedKey()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.Manage);
        var store = new RecordingStore(new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, profile.Id, [profile]));
        var transport = new FakeTransport(new RemoteNutConnectionResult(
            RemoteNutConnectionState.HostKeyMismatch,
            hostKey: new RemoteNutHostKeyInfo("management.example", 22, "ssh-ed25519", CanonicalFingerprint)));
        var viewModel = new RemoteManagementSessionViewModel(profile, transport, new ManagedNutServerProfileUpdateService(store));

        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory());

        Assert.Equal(RemoteNutConnectionState.HostKeyMismatch, viewModel.ConnectionState);
        Assert.False(viewModel.CanTrustHostKey);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task ManageRemoteSessionRequiresExplicitWindowsCapabilityProbeBeforeEditing()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.Manage);
        var session = new FakeSession(RemoteNutPlatform.Windows);
        var viewModel = new RemoteManagementSessionViewModel(profile, new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, session)));
        INutConfigurationFilePipeline? configuredPipeline = null;
        viewModel.ConfigurationContextChanged += (pipeline, _, _) => configuredPipeline = pipeline;

        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory());
        Assert.True(viewModel.CanReadConfiguration);
        Assert.False(viewModel.CanEditConfiguration);
        Assert.True(viewModel.IsWriteCapabilityUnverified);
        Assert.False(viewModel.IsWriteCapabilitySupported);

        await viewModel.ProbeWriteCapabilityAsync();

        Assert.True(viewModel.CanEditConfiguration);
        Assert.False(viewModel.IsWriteCapabilityUnverified);
        Assert.True(viewModel.IsWriteCapabilitySupported);
        Assert.False(viewModel.IsWriteCapabilityRejected);
        Assert.NotNull(configuredPipeline);
        Assert.Equal(1, session.ProbeCalls);
    }

    [Fact]
    public async Task CapabilityProbeCleanupFailureBlocksEditingAndIsCritical()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.Manage);
        var session = new FakeSession(RemoteNutPlatform.Windows)
        {
            ProbeResult = new RemoteNutWriteCapabilityResult(false, RemoteNutPlatform.Windows, "/etc/nut/.nutmanager-probe.tmp", "cleanup failed")
        };
        var viewModel = new RemoteManagementSessionViewModel(profile, new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, session)));

        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory());
        await viewModel.ValidateCurrentDirectoryAsync();
        await viewModel.ProbeWriteCapabilityAsync();

        Assert.True(viewModel.IsWriteCapabilityCritical);
        Assert.True(viewModel.IsWriteCapabilityRejected);
        Assert.False(viewModel.CanEditConfiguration);
        Assert.Contains("CRÍTICO", viewModel.WriteCapabilityCriticalText);
    }

    [Fact]
    public async Task IndeterminateRemoteWriteDisablesEditingUntilReconnectAndProbe()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.Manage);
        var session = new FakeSession(RemoteNutPlatform.Windows);
        var viewModel = new RemoteManagementSessionViewModel(profile, new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, session)));

        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory());
        await viewModel.ValidateCurrentDirectoryAsync();
        await viewModel.ProbeWriteCapabilityAsync();
        Assert.True(viewModel.CanEditConfiguration);

        viewModel.InvalidateWriteCapabilityAfterUncertainOutcome();

        Assert.False(viewModel.CanEditConfiguration);
        Assert.Contains("conecte novamente", viewModel.WriteCapabilityText);
    }

    [Fact]
    public async Task EditingRemoteDirectoryTextInvalidatesThePreviouslyValidatedContext()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.ReadOnly);
        var session = new FakeSession(RemoteNutPlatform.Windows);
        var viewModel = new RemoteManagementSessionViewModel(profile, new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, session)));

        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory());
        await viewModel.ValidateCurrentDirectoryAsync();
        Assert.True(viewModel.CanReadConfiguration);

        viewModel.CurrentDirectory = "/other/nut";

        Assert.False(viewModel.IsDirectoryValidated);
        Assert.False(viewModel.CanReadConfiguration);
        Assert.False(viewModel.CanUseCurrentDirectory);
    }

    [Fact]
    public async Task SmbProfileUsesOnlyTheSmbConnectionRequestAndDoesNotRequireHostKeyTrust()
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "SMB",
            new NutMonitoringProfile("monitor.example"),
            new NutManagementProfile(
                NutManagementMode.Remote,
                configurationTransport: RemoteConfigurationTransportKind.Smb,
                smbSharePath: @"\\server\share",
                smbConfigurationDirectory: @"\\server\share\NUT\etc"),
            ManagedNutServerAccessMode.Manage);
        var session = new FakeSession(RemoteNutPlatform.Unknown, new SmbRemoteNutConfigurationPathPolicy(@"\\server\share"));
        var transport = new FakeSmbTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, session));
        var viewModel = new RemoteManagementSessionViewModel(profile, transport);

        await viewModel.ConnectWithCurrentWindowsIdentityAsync();
        await viewModel.ValidateCurrentDirectoryAsync();
        await viewModel.ProbeWriteCapabilityAsync();

        Assert.True(viewModel.IsSmb);
        Assert.False(viewModel.IsSshSftp);
        Assert.False(viewModel.ShowsDirectoryBrowser);
        Assert.False(viewModel.CanTrustHostKey);
        Assert.Equal(1, transport.ConnectCalls);
        Assert.IsType<SmbRemoteNutConnectionRequest>(transport.LastRequest);
        Assert.True(viewModel.CanEditConfiguration);
    }

    [Fact]
    public async Task ManualRememberedSshCredentialIsStoredOnlyAfterSuccessfulExplicitConnection()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.Manage);
        var profileStore = new RecordingStore(new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, profile.Id, [profile]));
        var credentials = new FakeCredentialStore();
        var transport = new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, new FakeSession(RemoteNutPlatform.Windows)));
        var viewModel = new RemoteManagementSessionViewModel(profile, transport, new ManagedNutServerProfileUpdateService(profileStore, credentials), credentials);

        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory(), rememberCredential: true);

        Assert.Equal(1, transport.ConnectCalls);
        Assert.Equal(1, credentials.WriteCalls);
        Assert.Equal(RemoteCredentialKind.SshPassword, credentials.LastKind);
        Assert.Equal(RemoteNutConnectionState.Ready, viewModel.ConnectionState);
    }

    [Fact]
    public async Task FailedManualRememberedCredentialIsNotStored()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.Manage);
        var profileStore = new RecordingStore(new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, profile.Id, [profile]));
        var credentials = new FakeCredentialStore();
        var viewModel = new RemoteManagementSessionViewModel(
            profile,
            new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.AuthenticationFailed)),
            new ManagedNutServerProfileUpdateService(profileStore, credentials),
            credentials);

        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory(), rememberCredential: true);

        Assert.Equal(0, credentials.WriteCalls);
    }

    [Fact]
    public void ConstructionNeverStartsAnAutomaticConnectionOrExposesSecretTextProperties()
    {
        var transport = new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, new FakeSession(RemoteNutPlatform.Windows)));
        _ = new RemoteManagementSessionViewModel(RemoteProfile(ManagedNutServerAccessMode.Manage), transport, credentialStore: new FakeCredentialStore());

        Assert.Equal(0, transport.ConnectCalls);
        Assert.DoesNotContain(
            typeof(RemoteManagementSessionViewModel).GetProperties(),
            property => property.PropertyType == typeof(string) &&
                        (property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task FailedSavedCredentialIsNotRetriedOrDeleted()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.Manage);
        var credentials = new FakeCredentialStore();
        await credentials.WriteAsync(profile.Id, RemoteCredentialKind.SshPassword, "fictional-password".AsMemory());
        var transport = new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.AuthenticationFailed));
        var viewModel = new RemoteManagementSessionViewModel(profile, transport, credentialStore: credentials);
        await viewModel.RefreshStoredCredentialStatusAsync();

        await viewModel.ConnectWithStoredCredentialAsync();

        Assert.Equal(1, transport.ConnectCalls);
        Assert.Equal(0, credentials.DeleteCalls);
        Assert.Equal(RemoteNutConnectionState.AuthenticationFailed, viewModel.ConnectionState);
    }

    [Fact]
    public async Task SavedCredentialIsDisposedAfterAnExplicitConnectionAttempt()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.Manage);
        var credentials = new FakeCredentialStore();
        await credentials.WriteAsync(profile.Id, RemoteCredentialKind.SshPassword, "fictional-password".AsMemory());
        var viewModel = new RemoteManagementSessionViewModel(
            profile,
            new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, new FakeSession(RemoteNutPlatform.Windows))),
            credentialStore: credentials);
        await viewModel.RefreshStoredCredentialStatusAsync();

        await viewModel.ConnectWithStoredCredentialAsync();

        Assert.NotNull(credentials.LastReadMemory);
        Assert.All(credentials.LastReadMemory!.Value.Span.ToArray(), value => Assert.Equal('\0', value));
    }

    [Fact]
    public async Task ExplicitSmbCredentialCanBeRememberedOnlyAfterItsConnectionSucceeds()
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "SMB",
            new NutMonitoringProfile("monitor.example"),
            new NutManagementProfile(
                NutManagementMode.Remote,
                configurationTransport: RemoteConfigurationTransportKind.Smb,
                smbSharePath: @"\\server\share",
                smbAuthenticationMode: SmbAuthenticationMode.ExplicitCredentials,
                smbUsername: "DOMAIN\\nut"),
            ManagedNutServerAccessMode.Manage);
        var profileStore = new RecordingStore(new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, profile.Id, [profile]));
        var credentials = new FakeCredentialStore();
        var transport = new FakeSmbTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, new FakeSession(RemoteNutPlatform.Windows, new SmbRemoteNutConfigurationPathPolicy(@"\\server\share"))));
        var viewModel = new RemoteManagementSessionViewModel(profile, transport, new ManagedNutServerProfileUpdateService(profileStore, credentials), credentials);

        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory(), rememberCredential: true);

        Assert.Equal(1, transport.ConnectCalls);
        Assert.Equal(RemoteCredentialKind.SmbPassword, credentials.LastKind);
        Assert.Equal(1, credentials.WriteCalls);
    }

    [Fact]
    public async Task ManualPrivateKeyOverrideNeverReadsTheConfiguredKeyPassphrase()
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "Key",
            new NutMonitoringProfile("monitor.example"),
            new NutManagementProfile(NutManagementMode.Remote, "management.example", "/etc/nut", sshUsername: "nutadmin", sshAuthenticationMode: SshAuthenticationMode.PrivateKey, sshPrivateKeyPath: @"C:\keys\configured.key"),
            ManagedNutServerAccessMode.Manage);
        var credentials = new FakeCredentialStore();
        await credentials.WriteAsync(profile.Id, RemoteCredentialKind.SshPrivateKeyPassphrase, "fictional-password".AsMemory());
        var viewModel = new RemoteManagementSessionViewModel(profile, new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, new FakeSession(RemoteNutPlatform.Windows))), credentialStore: credentials);

        await viewModel.ConnectWithPrivateKeyAsync(@"C:\keys\session-only.key", "session-passphrase".AsMemory());

        Assert.Equal(0, credentials.ReadCalls);
    }

    [Fact]
    public async Task PrivateKeySshProfileRejectsPasswordAuthenticationBeforeTransportOrCredentialWrite()
    {
        var profile = PrivateKeyRemoteProfile();
        var credentials = new FakeCredentialStore();
        var transport = new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, new FakeSession(RemoteNutPlatform.Windows)));
        var viewModel = new RemoteManagementSessionViewModel(profile, transport, credentialStore: credentials);

        await viewModel.ConnectWithPasswordAsync("fictional-password".AsMemory(), rememberCredential: true);

        Assert.Equal(0, transport.ConnectCalls);
        Assert.Equal(0, credentials.WriteCalls);
        Assert.Contains("chave privada", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PasswordSshProfileRejectsPrivateKeyAuthenticationBeforeTransportOrCredentialWrite()
    {
        var profile = RemoteProfile(ManagedNutServerAccessMode.Manage);
        var credentials = new FakeCredentialStore();
        var transport = new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, new FakeSession(RemoteNutPlatform.Windows)));
        var viewModel = new RemoteManagementSessionViewModel(profile, transport, credentialStore: credentials);

        await viewModel.ConnectWithPrivateKeyAsync(@"C:\keys\session-only.key", "fictional-password".AsMemory(), rememberPassphrase: true);

        Assert.Equal(0, transport.ConnectCalls);
        Assert.Equal(0, credentials.WriteCalls);
        Assert.Contains("senha", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrivateKeyProfileConnectsWithItsConfiguredKey()
    {
        var profile = PrivateKeyRemoteProfile();
        var transport = new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, new FakeSession(RemoteNutPlatform.Windows)));
        var viewModel = new RemoteManagementSessionViewModel(profile, transport);

        await viewModel.ConnectWithPrivateKeyAsync(profile.Management.SshPrivateKeyPath!);

        Assert.Equal(1, transport.ConnectCalls);
        Assert.Equal(RemoteNutConnectionState.Ready, viewModel.ConnectionState);
    }

    [Fact]
    public async Task CurrentIdentitySmbNeverQueriesTheCredentialStore()
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "SMB",
            new NutMonitoringProfile("monitor.example"),
            new NutManagementProfile(NutManagementMode.Remote, configurationTransport: RemoteConfigurationTransportKind.Smb, smbSharePath: @"\\server\share"),
            ManagedNutServerAccessMode.Manage);
        var credentials = new FakeCredentialStore();
        var viewModel = new RemoteManagementSessionViewModel(profile, new FakeSmbTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, new FakeSession(RemoteNutPlatform.Unknown, new SmbRemoteNutConfigurationPathPolicy(@"\\server\share")))), credentialStore: credentials);

        await viewModel.RefreshStoredCredentialStatusAsync();

        Assert.Equal(0, credentials.ContainsCalls);
        Assert.Contains("Nenhuma credencial", viewModel.StoredCredentialText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupRestoresCurrentIdentitySmbAndValidatesTheSavedDirectory()
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "SMB",
            new NutMonitoringProfile("monitor.example"),
            new NutManagementProfile(
                NutManagementMode.Remote,
                configurationTransport: RemoteConfigurationTransportKind.Smb,
                smbSharePath: @"\\server\share"),
            ManagedNutServerAccessMode.Manage);
        var session = new FakeSession(RemoteNutPlatform.Unknown, new SmbRemoteNutConfigurationPathPolicy(@"\\server\share"));
        var transport = new FakeSmbTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, session));
        var viewModel = new RemoteManagementSessionViewModel(profile, transport, credentialStore: new FakeCredentialStore());

        await viewModel.TryConnectAndValidateConfiguredSmbAsync();

        Assert.Equal(1, transport.ConnectCalls);
        Assert.Equal(1, session.ValidateCalls);
        Assert.True(viewModel.IsDirectoryValidated);
        Assert.False(viewModel.CanEditConfiguration);
        Assert.Equal(0, session.ProbeCalls);
    }

    [Fact]
    public async Task ReadOnlySmbSessionAcceptsAnExplicitProbeAfterAccessChangesToManage()
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "SMB",
            new NutMonitoringProfile("monitor.example"),
            new NutManagementProfile(
                NutManagementMode.Remote,
                configurationTransport: RemoteConfigurationTransportKind.Smb,
                smbSharePath: @"\\server\share"),
            ManagedNutServerAccessMode.ReadOnly);
        var session = new FakeSession(
            RemoteNutPlatform.Unknown,
            new SmbRemoteNutConfigurationPathPolicy(@"\\server\share"),
            canWrite: false);
        var viewModel = new RemoteManagementSessionViewModel(
            profile,
            new FakeSmbTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, session)),
            credentialStore: new FakeCredentialStore());
        await viewModel.TryConnectAndValidateConfiguredSmbAsync();

        Assert.False(viewModel.CanProbeWriteCapability);

        viewModel.ApplyAccessMode(ManagedNutServerAccessMode.Manage);

        Assert.True(viewModel.CanProbeWriteCapability);
        Assert.Null(viewModel.WriteCapability);
        await viewModel.ProbeWriteCapabilityAsync();
        Assert.True(viewModel.IsWriteCapabilitySupported);
        Assert.Equal(1, session.ProbeCalls);
    }

    [Fact]
    public async Task StartupNeverPromptsOrConnectsExplicitSmbWithoutAProtectedCredential()
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "SMB",
            new NutMonitoringProfile("monitor.example"),
            new NutManagementProfile(
                NutManagementMode.Remote,
                configurationTransport: RemoteConfigurationTransportKind.Smb,
                smbSharePath: @"\\server\share",
                smbAuthenticationMode: SmbAuthenticationMode.ExplicitCredentials,
                smbUsername: @"DOMAIN\operator"),
            ManagedNutServerAccessMode.Manage);
        var transport = new FakeSmbTransport(new RemoteNutConnectionResult(
            RemoteNutConnectionState.Connected,
            new FakeSession(RemoteNutPlatform.Unknown, new SmbRemoteNutConfigurationPathPolicy(@"\\server\share"))));
        var credentials = new FakeCredentialStore();
        var viewModel = new RemoteManagementSessionViewModel(profile, transport, credentialStore: credentials);

        await viewModel.TryConnectAndValidateConfiguredSmbAsync();

        Assert.Equal(0, transport.ConnectCalls);
        Assert.Equal(0, credentials.ReadCalls);
        Assert.Contains("credencial SMB protegida", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartupReusesAProtectedSmbCredentialExactlyOnceAndValidates()
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "SMB",
            new NutMonitoringProfile("monitor.example"),
            new NutManagementProfile(
                NutManagementMode.Remote,
                configurationTransport: RemoteConfigurationTransportKind.Smb,
                smbSharePath: @"\\server\share",
                smbAuthenticationMode: SmbAuthenticationMode.ExplicitCredentials,
                smbUsername: @"DOMAIN\operator"),
            ManagedNutServerAccessMode.Manage);
        var credentials = new FakeCredentialStore();
        await credentials.WriteAsync(profile.Id, RemoteCredentialKind.SmbPassword, "fictional-password".AsMemory());
        var session = new FakeSession(RemoteNutPlatform.Unknown, new SmbRemoteNutConfigurationPathPolicy(@"\\server\share"));
        var transport = new FakeSmbTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, session));
        var viewModel = new RemoteManagementSessionViewModel(profile, transport, credentialStore: credentials);

        await viewModel.TryConnectAndValidateConfiguredSmbAsync();

        Assert.Equal(1, transport.ConnectCalls);
        Assert.Equal(1, credentials.ReadCalls);
        Assert.Equal(1, session.ValidateCalls);
        Assert.Equal(0, session.ProbeCalls);
        Assert.True(viewModel.IsDirectoryValidated);
    }

    [Fact]
    public async Task EmptyPrivateKeyPassphraseIsNeverStored()
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "Key",
            new NutMonitoringProfile("monitor.example"),
            new NutManagementProfile(NutManagementMode.Remote, "management.example", "/etc/nut", sshUsername: "nutadmin", sshAuthenticationMode: SshAuthenticationMode.PrivateKey, sshPrivateKeyPath: @"C:\keys\fictional.key"),
            ManagedNutServerAccessMode.Manage);
        var profileStore = new RecordingStore(new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, profile.Id, [profile]));
        var credentials = new FakeCredentialStore();
        var viewModel = new RemoteManagementSessionViewModel(profile, new FakeTransport(new RemoteNutConnectionResult(RemoteNutConnectionState.Connected, new FakeSession(RemoteNutPlatform.Windows))), new ManagedNutServerProfileUpdateService(profileStore, credentials), credentials);

        await viewModel.ConnectWithPrivateKeyAsync(profile.Management.SshPrivateKeyPath!, default, rememberPassphrase: true);

        Assert.Equal(0, credentials.WriteCalls);
    }

    private static ManagedNutServerProfile RemoteProfile(ManagedNutServerAccessMode accessMode) => new(
        Guid.NewGuid(),
        "Remote",
        new NutMonitoringProfile("monitor.example"),
        new NutManagementProfile(NutManagementMode.Remote, "management.example", "/etc/nut", sshUsername: "nutadmin"),
        accessMode);

    private static ManagedNutServerProfile PrivateKeyRemoteProfile() => new(
        Guid.NewGuid(),
        "Private key remote",
        new NutMonitoringProfile("monitor.example"),
        new NutManagementProfile(
            NutManagementMode.Remote,
            "management.example",
            "/etc/nut",
            sshUsername: "nutadmin",
            sshAuthenticationMode: SshAuthenticationMode.PrivateKey,
            sshPrivateKeyPath: @"C:\keys\fictional.key"),
        ManagedNutServerAccessMode.Manage);

    private sealed class RecordingStore : IManagedNutServerProfileStore
    {
        private readonly ManagedNutServerProfiles _loaded;
        public RecordingStore(ManagedNutServerProfiles loaded) => _loaded = loaded;
        public ManagedNutServerProfiles? Saved { get; private set; }
        public Task<ManagedNutServerProfiles?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<ManagedNutServerProfiles?>(Saved ?? _loaded);
        public Task SaveAsync(ManagedNutServerProfiles profiles, CancellationToken cancellationToken)
        {
            Saved = profiles;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransport : IRemoteNutManagementTransport
    {
        private readonly RemoteNutConnectionResult _result;
        public FakeTransport(RemoteNutConnectionResult result) => _result = result;
        public int ConnectCalls { get; private set; }
        public Task<RemoteNutConnectionResult> ConnectAsync(RemoteNutConnectionRequest request, CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeSmbTransport : IRemoteNutConfigurationTransport
    {
        private readonly RemoteNutConnectionResult _result;

        public FakeSmbTransport(RemoteNutConnectionResult result) => _result = result;

        public int ConnectCalls { get; private set; }

        public RemoteNutConfigurationConnectionRequest? LastRequest { get; private set; }

        public Task<RemoteNutConnectionResult> ConnectAsync(RemoteNutConfigurationConnectionRequest request, CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeSession : IRemoteNutManagementSession, IRemoteNutWriteIntentSession
    {
        private bool _canWrite;

        public FakeSession(
            RemoteNutPlatform platform,
            IRemoteNutConfigurationPathPolicy? pathPolicy = null,
            bool canWrite = true)
        {
            Platform = platform;
            PathPolicy = pathPolicy ?? SftpRemoteNutConfigurationPathPolicy.Instance;
            _canWrite = canWrite;
        }
        public RemoteNutPlatform Platform { get; }
        public IRemoteNutConfigurationPathPolicy PathPolicy { get; }
        public bool IsSafeWriteCapabilityValidFor(string configurationDirectory) => true;
        public string HomeDirectory => "/etc/nut";
        public int ProbeCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public RemoteNutWriteCapabilityResult? ProbeResult { get; init; }
        public Task<RemoteNutDirectoryListing> BrowseDirectoryAsync(string directory, CancellationToken cancellationToken = default) => Task.FromResult(new RemoteNutDirectoryListing(directory, "/etc", []));
        public Task<RemoteNutDirectoryValidationResult> ValidateConfigurationDirectoryAsync(string directory, CancellationToken cancellationToken = default)
        {
            ValidateCalls++;
            return Task.FromResult(new RemoteNutDirectoryValidationResult(RemoteNutTransportStatus.Success, directory, ["nut.conf"]));
        }
        public Task<RemoteNutFileReadResult> ReadFileAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new RemoteNutFileReadResult(RemoteNutTransportStatus.NotFound));
        public Task<RemoteNutWriteCapabilityResult> ProbeSafeWriteCapabilityAsync(string directory, CancellationToken cancellationToken = default)
        {
            ProbeCalls++;
            if (!_canWrite)
            {
                return Task.FromResult(new RemoteNutWriteCapabilityResult(false, Platform, message: "Read-only profile."));
            }

            return Task.FromResult(ProbeResult ?? new RemoteNutWriteCapabilityResult(true, Platform));
        }
        public void ApplyWriteIntent(bool canWrite) => _canWrite = canWrite;
        public void InvalidateSafeWriteCapability() { }
        public Task<RemoteNutFileReadResult> UploadCandidateAsync(RemoteNutCandidateUploadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new RemoteNutFileReadResult(RemoteNutTransportStatus.Unsupported));
        public Task<RemoteNutTemporaryCleanupResult> DeleteGeneratedTemporaryFileAsync(string configurationDirectory, string temporaryFileName, CancellationToken cancellationToken = default) => Task.FromResult(new RemoteNutTemporaryCleanupResult(RemoteNutTransportStatus.NotFound));
        public Task<RemoteNutCommitResult> CommitConfigurationAsync(RemoteNutConfigurationCommitRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new RemoteNutCommitResult(RemoteNutTransportStatus.Unsupported));
        public Task<RemoteNutCommitResult> RollbackConfigurationAsync(RemoteNutConfigurationRollbackRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new RemoteNutCommitResult(RemoteNutTransportStatus.Unsupported));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCredentialStore : IRemoteCredentialStore
    {
        private readonly Dictionary<(Guid ProfileId, RemoteCredentialKind Kind), char[]> _secrets = [];
        public int ContainsCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public RemoteCredentialKind? LastKind { get; private set; }
        public ReadOnlyMemory<char>? LastReadMemory { get; private set; }

        public Task<RemoteCredentialStoreResult> ContainsAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default)
        {
            ContainsCalls++;
            return Task.FromResult(new RemoteCredentialStoreResult(_secrets.ContainsKey((profileId, kind)) ? RemoteCredentialStoreStatus.Success : RemoteCredentialStoreStatus.NotFound));
        }

        public Task<RemoteCredentialReadResult> ReadAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            if (!_secrets.TryGetValue((profileId, kind), out var secret))
            {
                return Task.FromResult(new RemoteCredentialReadResult(RemoteCredentialStoreStatus.NotFound));
            }

            var protectedSecret = new RemoteCredentialSecret(secret);
            LastReadMemory = protectedSecret.Memory;
            return Task.FromResult(new RemoteCredentialReadResult(RemoteCredentialStoreStatus.Success, protectedSecret));
        }

        public Task<RemoteCredentialStoreResult> WriteAsync(Guid profileId, RemoteCredentialKind kind, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            LastKind = kind;
            _secrets[(profileId, kind)] = secret.ToArray();
            return Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success));
        }

        public Task<RemoteCredentialStoreResult> DeleteAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            _secrets.Remove((profileId, kind));
            return Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success));
        }

        public async Task<RemoteCredentialStoreResult> DeleteAllForProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            foreach (var kind in Enum.GetValues<RemoteCredentialKind>())
            {
                await DeleteAsync(profileId, kind, cancellationToken);
            }

            return new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success);
        }
    }
}
