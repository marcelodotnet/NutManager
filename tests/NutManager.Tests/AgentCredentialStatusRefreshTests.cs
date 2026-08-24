using NutManager.App.Services;
using NutManager.App.ViewModels;
using NutManager.Core.Agent;
using NutManager.Core.Models;
using NutManager.Core.Services;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// The status the Settings page reports for the two credentials, and the startup path that used to
/// skip one of them.
///
/// The defect these tests exist for was found on a working installation: the agent was connected over
/// HTTPS with an alternate Windows account, the Administration page reported the real NUT service and
/// its process id, and Settings said "account configured, but no credential". The secret was in the
/// Credential Manager the whole time — the bootstrap refreshed only the configuration transport's
/// credential, so the agent's status stayed at its constructed default until the operator happened to
/// switch profiles.
///
/// Storage state is never inferred from connectivity. These tests assert the status against the
/// stores, which is the only thing that can answer it honestly.
/// </summary>
public sealed class AgentCredentialStatusRefreshTests
{
    private const string Account = @"SBRA\PT90";
    private const string Endpoint = "https://gandalf.sbra.local:5199/";
    private const string Sentinel = "session-secret-sentinel";

    // ---------------------------------------------------------------- the bug

    [Fact]
    public async Task StartupReportsAnAgentCredentialThatIsAlreadyStored()
    {
        var profile = GandalfProfile();
        var credentials = new PerProfileCredentialStore();
        credentials.Put(profile.Id, RemoteCredentialKind.WindowsAgentPassword);
        var settings = CreateViewModel(profile, credentials);

        // Exactly what the bootstrap does.
        await settings.RefreshCredentialStatusesAsync();

        Assert.True(settings.HasStoredAgentCredential);
        Assert.NotEqual(settings.Localizer.Get("Agent.Credential.Missing"), settings.AgentCredentialStatusText);
        Assert.Equal(settings.Localizer.Get("Agent.Credential.Stored"), settings.AgentCredentialStatusText);
        Assert.Equal(settings.Localizer.Get("Agent.Credential.Change"), settings.AgentAuthenticateText);
        Assert.True(settings.CanForgetAgentCredential);
    }

    [Fact]
    public async Task ValidatedCredentialForAnUnchangedSavedProfilePersistsBeforeRestart()
    {
        var profile = GandalfProfile();
        var credentials = new PerProfileCredentialStore();
        using var session = new NutAgentSessionCredentialStore();
        var profiles = new ManagedNutServerProfiles(
            ManagedNutServerProfiles.CurrentSchemaVersion,
            profile.Id,
            [profile]);
        var profileStore = new ProfileStore(profiles);
        var coordinator = new NutAgentCredentialCoordinator(
            new SuccessfulPrompt(),
            session,
            (_, _) => new SuccessfulAgentClient());
        var settings = new SettingsPageViewModel(
            new ApplicationSettings(),
            null,
            profiles,
            profileStore,
            new ManagedNutServerProfileUpdateService(profileStore, credentials),
            credentials,
            agentCredentials: coordinator);

        await settings.AuthenticateAgentCredentialCommand.ExecuteAsync(null);

        Assert.True(settings.HasStoredAgentCredential);
        Assert.Equal(settings.Localizer.Get("Agent.Credential.Stored"), settings.AgentCredentialStatusText);

        var restarted = CreateViewModel(profile, credentials);
        await restarted.RefreshCredentialStatusesAsync();
        Assert.True(restarted.HasStoredAgentCredential);
        Assert.Equal(restarted.Localizer.Get("Agent.Credential.Stored"), restarted.AgentCredentialStatusText);
    }

    [Fact]
    public async Task RefreshingOnlyTheConfigurationCredentialIsNotEnough()
    {
        // The regression in its exact shape: the configuration refresh alone leaves the agent's
        // status untouched, which is what the bootstrap used to do.
        var profile = GandalfProfile();
        var credentials = new PerProfileCredentialStore();
        credentials.Put(profile.Id, RemoteCredentialKind.WindowsAgentPassword);
        var settings = CreateViewModel(profile, credentials);

        await settings.RefreshStoredCredentialStatusAsync();
        Assert.False(settings.HasStoredAgentCredential);

        await settings.RefreshCredentialStatusesAsync();
        Assert.True(settings.HasStoredAgentCredential);
    }

    // ---------------------------------------------------------------- the other states

    [Fact]
    public async Task AnAccountWithNoSecretAnywhereReportsMissing()
    {
        var profile = GandalfProfile();
        var settings = CreateViewModel(profile, new PerProfileCredentialStore());

        await settings.RefreshCredentialStatusesAsync();

        Assert.False(settings.HasStoredAgentCredential);
        Assert.Null(settings.ValidatedAgentAccount);
        Assert.Equal(settings.Localizer.Get("Agent.Credential.Missing"), settings.AgentCredentialStatusText);
    }

    [Fact]
    public async Task ASessionCredentialIsReportedWithoutBeingStored()
    {
        var profile = GandalfProfile();
        var session = new NutAgentSessionCredentialStore();
        session.Store(profile.Id, Account, Sentinel);
        var settings = CreateViewModel(profile, new PerProfileCredentialStore(), session);

        await settings.RefreshCredentialStatusesAsync();

        Assert.Equal(Account, settings.ValidatedAgentAccount);
        Assert.False(settings.HasStoredAgentCredential);
        Assert.Contains(Account, settings.AgentCredentialStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionCredentialDoesNotHideAStoredOne()
    {
        var profile = GandalfProfile();
        var credentials = new PerProfileCredentialStore();
        credentials.Put(profile.Id, RemoteCredentialKind.WindowsAgentPassword);
        var session = new NutAgentSessionCredentialStore();
        session.Store(profile.Id, Account, Sentinel);
        var settings = CreateViewModel(profile, credentials, session);

        await settings.RefreshCredentialStatusesAsync();

        // The session account leads the presentation, matching what the runtime prefers, but the
        // stored indicator is a separate fact and stays true.
        Assert.Equal(Account, settings.ValidatedAgentAccount);
        Assert.True(settings.HasStoredAgentCredential);
    }

    [Fact]
    public async Task AStoredCredentialIsNeverReportedAsValidated()
    {
        var profile = GandalfProfile();
        var credentials = new PerProfileCredentialStore();
        credentials.Put(profile.Id, RemoteCredentialKind.WindowsAgentPassword);
        var settings = CreateViewModel(profile, credentials);

        await settings.RefreshCredentialStatusesAsync();

        // No handshake ran, so nothing may claim the credential still works.
        Assert.Null(settings.ValidatedAgentAccount);
        Assert.DoesNotContain(Account, settings.AgentCredentialStatusText, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- profile isolation

    [Fact]
    public async Task StatusDoesNotLeakBetweenProfiles()
    {
        var withCredential = GandalfProfile();
        var without = GandalfProfile("Other");
        var credentials = new PerProfileCredentialStore();
        credentials.Put(withCredential.Id, RemoteCredentialKind.WindowsAgentPassword);

        var profiles = new ManagedNutServerProfiles(
            ManagedNutServerProfiles.CurrentSchemaVersion, withCredential.Id, [withCredential, without]);
        var settings = CreateViewModel(profiles, credentials, new NutAgentSessionCredentialStore());

        await settings.RefreshCredentialStatusesAsync();
        Assert.True(settings.HasStoredAgentCredential);

        settings.SelectedManagedProfile = without;
        await settings.RefreshCredentialStatusesAsync();
        Assert.False(settings.HasStoredAgentCredential);
        Assert.Null(settings.ValidatedAgentAccount);

        settings.SelectedManagedProfile = withCredential;
        await settings.RefreshCredentialStatusesAsync();
        Assert.True(settings.HasStoredAgentCredential);
    }

    // ---------------------------------------------------------------- the two lifecycles

    [Fact]
    public async Task TheConfigurationAndAgentCredentialsAreIndependent()
    {
        // The real GANDALF arrangement: SMB over the current Windows identity, which needs no
        // protected credential, while the agent holds one of its own. Both statements are true at
        // the same time and neither may contradict the other.
        var profile = GandalfProfile();
        var credentials = new PerProfileCredentialStore();
        credentials.Put(profile.Id, RemoteCredentialKind.WindowsAgentPassword);
        var settings = CreateViewModel(profile, credentials);

        await settings.RefreshCredentialStatusesAsync();

        Assert.Equal(settings.Localizer.Get("Credential.NotRequired"), settings.StoredCredentialText);
        Assert.Equal(settings.Localizer.Get("Agent.Credential.Stored"), settings.AgentCredentialStatusText);
    }

    [Fact]
    public async Task TheConfigurationCredentialTextSaysWhatItIsFor()
    {
        var profile = GandalfProfile();
        var settings = CreateViewModel(profile, new PerProfileCredentialStore());

        await settings.RefreshCredentialStatusesAsync();

        // Sitting directly under the agent's own credential, an unqualified "no credential required"
        // reads as a statement about the agent. It has to name the configuration files.
        foreach (var language in new[] { UiLanguagePreference.PtBr, UiLanguagePreference.EnUs })
        {
            var localizer = new App.Localization.NutManagerLocalizer(language);
            var expected = language == UiLanguagePreference.PtBr ? "configuração" : "configuration";

            Assert.Contains(expected, localizer.Get("Credential.NotRequired"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expected, localizer.Get("Credential.Configuration.Label"), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TheAgentCredentialKeepsItsOwnTargetKind()
    {
        // Storing an SMB secret must never make the agent look configured.
        var profile = GandalfProfile();
        var credentials = new PerProfileCredentialStore();
        credentials.Put(profile.Id, RemoteCredentialKind.SmbPassword);
        credentials.Put(profile.Id, RemoteCredentialKind.SshPassword);
        var settings = CreateViewModel(profile, credentials);

        await settings.RefreshCredentialStatusesAsync();

        Assert.False(settings.HasStoredAgentCredential);
    }

    // ---------------------------------------------------------------- presentation

    [Fact]
    public void TheViewShowsTheAccountAndTheCredentialAsSeparateFields()
    {
        var view = Repository.Read(Path.Combine("src", "NutManager.App", "Views", "SettingsPageView.axaml"));

        // The account was never bound: the credential status was printed under the "Account" label,
        // so a configured account read as no account at all.
        Assert.Contains("AgentAccountStatusText", view, StringComparison.Ordinal);
        Assert.Contains("AgentCredentialLabel", view, StringComparison.Ordinal);
        Assert.Contains("ConfigurationCredentialLabel", view, StringComparison.Ordinal);

        var accountLabel = view.IndexOf("AgentAccountText", StringComparison.Ordinal);
        var accountValue = view.IndexOf("AgentAccountStatusText", StringComparison.Ordinal);
        var credentialLabel = view.IndexOf("AgentCredentialLabel", StringComparison.Ordinal);

        Assert.True(accountLabel < accountValue, "the account label precedes the account");
        Assert.True(accountValue < credentialLabel, "the credential label follows the account");
    }

    [Fact]
    public void TheNewLabelsResolveInBothCulturesAndCarryAccessibleNames()
    {
        foreach (var language in new[] { UiLanguagePreference.PtBr, UiLanguagePreference.EnUs })
        {
            var localizer = new App.Localization.NutManagerLocalizer(language);
            foreach (var key in new[] { "Agent.Credential.Label", "Credential.Configuration.Label", "Credential.Configuration.Help" })
            {
                var value = localizer.Get(key);
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} missing for {language}");
                Assert.NotEqual(key, value);
            }
        }

        var view = Repository.Read(Path.Combine("src", "NutManager.App", "Views", "SettingsPageView.axaml"));
        Assert.Contains("AutomationProperties.Name=\"{Binding AgentAccountStatusText}\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSecretReachesThePresentationSurface()
    {
        var profile = GandalfProfile();
        var session = new NutAgentSessionCredentialStore();
        session.Store(profile.Id, Account, Sentinel);
        var settings = CreateViewModel(profile, new PerProfileCredentialStore(), session);

        await settings.RefreshCredentialStatusesAsync();

        foreach (var text in new[]
                 {
                     settings.AgentCredentialStatusText, settings.AgentAccountStatusText,
                     settings.StoredCredentialText, settings.AgentCredentialLabel,
                     settings.ConfigurationCredentialLabel
                 })
        {
            Assert.DoesNotContain(Sentinel, text, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static SettingsPageViewModel CreateViewModel(
        ManagedNutServerProfile profile,
        PerProfileCredentialStore credentials,
        INutAgentSessionCredentialStore? session = null) =>
        CreateViewModel(
            new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, profile.Id, [profile]),
            credentials,
            session ?? new NutAgentSessionCredentialStore());

    private static SettingsPageViewModel CreateViewModel(
        ManagedNutServerProfiles profiles,
        PerProfileCredentialStore credentials,
        INutAgentSessionCredentialStore session)
    {
        var store = new ProfileStore(profiles);
        return new SettingsPageViewModel(
            new ApplicationSettings(),
            null,
            profiles,
            store,
            new ManagedNutServerProfileUpdateService(store, credentials),
            credentials,
            agentCredentials: new NutAgentCredentialCoordinator(new UnusedPrompt(), session));
    }

    /// <summary>The real arrangement: SMB over the current Windows identity, agent over HTTPS.</summary>
    private static ManagedNutServerProfile GandalfProfile(string name = "GANDALF") => new(
        Guid.NewGuid(),
        name,
        new NutMonitoringProfile("gandalf.sbra.local"),
        new NutManagementProfile(
            NutManagementMode.Remote,
            "gandalf.sbra.local",
            @"\\Gandalf\etc",
            configurationTransport: RemoteConfigurationTransportKind.Smb,
            smbSharePath: @"\\Gandalf\etc",
            smbAuthenticationMode: SmbAuthenticationMode.CurrentWindowsIdentity,
            agent: new NutAgentProfileSettings(
                NutAgentTransportKind.Https, Endpoint, NutAgentAuthenticationMode.AlternateWindowsAccount, Account)),
        ManagedNutServerAccessMode.Manage);

    private sealed class UnusedPrompt : IWindowsCredentialPrompt
    {
        public Task<WindowsCredentialPromptResult> RequestAsync(
            WindowsCredentialPromptRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refreshing a status must never prompt.");
    }

    private sealed class SuccessfulPrompt : IWindowsCredentialPrompt
    {
        public Task<WindowsCredentialPromptResult> RequestAsync(
            WindowsCredentialPromptRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowsCredentialPromptResult.Success(Account, Sentinel, false));
    }

    private sealed class SuccessfulAgentClient : INutManagerAgentClient
    {
        public Task<NutAgentClientResult<NutAgentHandshake>> HandshakeAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(NutAgentClientResult<NutAgentHandshake>.Ok(
                new NutAgentHandshake(
                    NutAgentOptions.ProtocolVersion,
                    "1.0.0",
                    "GANDALF",
                    [NutAgentOperation.Handshake, NutAgentOperation.GetStatus],
                    true,
                    null),
                NutAgentResultCode.Success));

        public Task<NutAgentClientResult<NutAgentServiceStatus>> GetStatusAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(NutAgentClientResult<NutAgentServiceStatus>.Failure(NutAgentClientStatus.Failed));

        public Task<NutAgentClientResult<NutAgentHardwareSnapshot>> GetHardwareSnapshotAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(NutAgentClientResult<NutAgentHardwareSnapshot>.Failure(NutAgentClientStatus.Failed));

        public Task<NutAgentClientResult<NutAgentOperationResult>> StartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Credential validation must not mutate the service.");

        public Task<NutAgentClientResult<NutAgentOperationResult>> StopAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Credential validation must not mutate the service.");

        public Task<NutAgentClientResult<NutAgentOperationResult>> RestartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Credential validation must not mutate the service.");
    }

    /// <summary>
    /// Keyed by profile and kind, because the isolation this class exists to prove is exactly what a
    /// single-slot fake would hide.
    /// </summary>
    private sealed class PerProfileCredentialStore : IRemoteCredentialStore
    {
        private readonly HashSet<(Guid Profile, RemoteCredentialKind Kind)> _held = [];

        public void Put(Guid profileId, RemoteCredentialKind kind) => _held.Add((profileId, kind));

        public Task<RemoteCredentialStoreResult> ContainsAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteCredentialStoreResult(
                _held.Contains((profileId, kind)) ? RemoteCredentialStoreStatus.Success : RemoteCredentialStoreStatus.NotFound));

        public Task<RemoteCredentialReadResult> ReadAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteCredentialReadResult(RemoteCredentialStoreStatus.NotFound));

        public Task<RemoteCredentialStoreResult> WriteAsync(Guid profileId, RemoteCredentialKind kind, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default)
        {
            _held.Add((profileId, kind));
            return Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success));
        }

        public Task<RemoteCredentialStoreResult> DeleteAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default)
        {
            _held.Remove((profileId, kind));
            return Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success));
        }

        public Task<RemoteCredentialStoreResult> DeleteAllForProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            _held.RemoveWhere(entry => entry.Profile == profileId);
            return Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success));
        }
    }

    private sealed class ProfileStore(ManagedNutServerProfiles profiles) : IManagedNutServerProfileStore
    {
        public ManagedNutServerProfiles Current { get; private set; } = profiles;

        public Task<ManagedNutServerProfiles?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ManagedNutServerProfiles?>(Current);

        public Task SaveAsync(ManagedNutServerProfiles value, CancellationToken cancellationToken = default)
        {
            Current = value;
            return Task.CompletedTask;
        }
    }
}
