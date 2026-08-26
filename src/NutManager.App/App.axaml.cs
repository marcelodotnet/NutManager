using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using NutManager.App.Presentation.Themes;
using NutManager.App.Services;
using NutManager.App.ViewModels;
using NutManager.Core.Agent;
using NutManager.Core.Models;
using NutManager.Core.Services;
using NutManager.Infrastructure.Agent;
using NutManager.Infrastructure.Configuration;
using NutManager.Infrastructure.Credentials.Windows;
using NutManager.Infrastructure.NutProtocol;
using NutManager.Infrastructure.Persistence;
using NutManager.Infrastructure.Polling;
using NutManager.Infrastructure.Platform.Windows;
using NutManager.Infrastructure.Remote.Smb;
using NutManager.Infrastructure.Remote.Ssh;

namespace NutManager.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            { DataContext = new MainWindowViewModel() };
            desktop.MainWindow.Opened += async (_, _) => await BootstrapAsync(desktop.MainWindow);
        }

        // Icon drawings come from the library, applied after the theme dictionaries are composed so
        // anything it does not supply keeps the geometry drawn for it.
        NutIconLibrary.Apply(this);
        base.OnFrameworkInitializationCompleted();
    }

    private async Task BootstrapAsync(Window window)
    {
        var store = new JsonApplicationSettingsStore();
        var profileStore = new JsonManagedNutServerProfileStore();
        ApplicationSettings settings;
        string? loadError = null;
        try { settings = await store.LoadAsync(CancellationToken.None); }
        catch (Exception) { settings = new ApplicationSettings(); loadError = "Não foi possível carregar as configurações locais."; }

        var profileBootstrap = await new ManagedNutServerBootstrapper(profileStore).LoadAsync(settings, CancellationToken.None);
        var runtimeProfile = profileBootstrap.RuntimeContext;
        var credentialStore = new WindowsCredentialManagerRemoteCredentialStore();

        // A credential the operator validated but asked not to save lives here and nowhere else, for
        // exactly as long as the process does — which is what "do not remember this" has to mean.
        var agentSessionCredentials = new NutAgentSessionCredentialStore();
        var agentCredentials = new NutAgentCredentialCoordinator(new WindowsCredentialPrompt(), agentSessionCredentials);
        var profileMutator = new ManagedNutServerProfileUpdateService(profileStore, credentialStore);

        var endpoint = runtimeProfile.Endpoint;
        INutClient client = new NutTcpClient();

        var polling = new UpsPollingCoordinator(client, endpoint, settings.PollingInterval);
        var overview = new OverviewPageViewModel(polling, settings.Language, endpoint);
        var devices = new DevicesPageViewModel(client, endpoint, polling, runtimeProfile.Profile.Monitoring.PreferredUpsName, settings.Language);
        var isLocalManagement = runtimeProfile.Profile.Management.Mode == NutManagementMode.Local;
        IRemoteNutConfigurationTransport? remoteTransport = runtimeProfile.Profile.Management.ConfigurationTransport switch
        {
            RemoteConfigurationTransportKind.Smb => new WindowsSmbRemoteNutConfigurationTransport(),
            _ => new SshNetRemoteNutManagementTransport()
        };
        var remoteManagement = isLocalManagement
            ? null
            : new RemoteManagementSessionViewModel(
                runtimeProfile.Profile,
                remoteTransport,
                profileMutator,
                credentialStore,
                settings.Language,
                new WindowsCredentialPrompt());
        // The host comes from the profile's own NUT endpoint, not from the SMB share path: the share
        // is a configuration transport that may point anywhere, while the endpoint is the machine
        // whose NUT is being monitored. The agent client uses the current Windows identity and no
        // credential from any store, so it is created for a remote profile regardless of how SMB is
        // faring.
        //
        // There is deliberately no fallback here. If the agent cannot be reached, the panel says so;
        // it does not quietly try the remote SCM instead, because a silent second path would mean an
        // operator could never tell which one answered — or why control is unavailable on a server
        // where monitoring appears to work.
        var agentSettings = runtimeProfile.Profile.Management.Agent;
        var agentTransport = agentSettings.Transport;
        var agentClient = await NutAgentClientFactory.CreateAsync(
            agentSettings, runtimeProfile.Profile.Id, credentialStore, CancellationToken.None, agentSessionCredentials);

        var remoteWindowsService = isLocalManagement
            ? null
            : new RemoteWindowsServiceViewModel(
                runtimeProfile.Endpoint.Host,
                agentClient,
                settings.Language,
                transport: agentTransport);
        var remoteWindowsServiceControl = remoteWindowsService is null
            ? null
            : new RemoteWindowsServiceControlViewModel(remoteWindowsService, agentClient, settings.Language);

        // Polling starts with the application rather than with the panel that used to own it.
        //
        // It began life bound to the Windows service view, on the reasoning that an unwatched panel
        // should cost nothing. That was right while the panel was the only thing showing agent state.
        // It is not any more: the Overview reports the agent as connected, and bound to that view the
        // figure was whatever the first handshake produced and then never moved — an agent that had
        // stopped hours ago still read as connected until the application was restarted, which is
        // worse than not showing it at all.
        //
        // This is the same single instance the administration page and the shell already share, so
        // there is one client, one timer and one state machine. The view still calls StartMonitoring
        // when it appears; that call is idempotent and now finds the loop already running.
        remoteWindowsService?.StartMonitoring();
        var installationDetector = isLocalManagement ? new WindowsNutInstallationDetector() : null;
        var diagnostics = new DiagnosticsPageViewModel(
            settings,
            ApplicationRuntimeInfo.CreateCurrent(),
            polling,
            devices,
            installationDetector,
            runtimeProfile,
            settings.Language,
            isLocalManagement ? new WindowsNutVersionResolver() : null);
        // The administration page also receives the agent client: the same one the service monitor
        // uses, for one read-only hardware operation. There is no second transport here and no
        // fallback, and a local profile gets no client at all.
        var administration = new AdministrationPageViewModel(
            installationDetector,
            isLocalManagement ? new NutConfigurationFilePipeline() : null,
            isLocalManagement ? new WindowsLocalNutAdministration() : null,
            isLocalManagement ? new WindowsNutDriverDiagnostics() : null,
            runtimeProfile,
            remoteManagement,
            settings.Language,
            isLocalManagement ? new WindowsNutDriverCatalogSource() : null,
            remoteWindowsService,
            remoteWindowsServiceControl,
            isLocalManagement ? null : agentClient);
        INutManagedFileDetector managedFileDetector = isLocalManagement
            ? new LocalNutManagedFileDetector(installationDetector ?? new WindowsNutInstallationDetector())
            : new RemoteNutManagedFileDetector(() => remoteManagement?.DirectoryValidation);
        var settingsPage = new SettingsPageViewModel(
            settings,
            store,
            profileBootstrap.Profiles,
            profileStore,
            profileMutator,
            credentialStore,
            new ManagedNutConnectionTester(new NutTcpClient()),
            runtimeProfile.Profile.Id,
            managedFileDetector,
            agentCredentials);
        window.Closed += async (_, _) =>
        {
            if (remoteWindowsService is not null)
            {
                await remoteWindowsService.DisposeAsync();
            }

            if (remoteManagement is not null)
            {
                await remoteManagement.DisposeAsync();
            }

            agentSessionCredentials.Dispose();
            diagnostics.Dispose();
            devices.Dispose();
            polling.Dispose();
        };
        if (loadError is not null) settingsPage.SetLoadError(loadError);
        if (profileBootstrap.Warning is not null) settingsPage.SetProfileLoadError(profileBootstrap.Warning, profileBootstrap.IsProfileDocumentLoadFailure);
        var viewModel = new MainWindowViewModel(
            settings.Theme,
            overview,
            devices,
            settingsPage,
            diagnostics,
            administration,
            settings.Language,
            settings.SidebarPreference,
            mockMode: false,
            $"{endpoint.Host}:{endpoint.Port}",
            runtimeProfile.Profile.Name,
            runtimeProfile.Profile.Management.Mode,
            runtimeProfile.Profile.AccessMode,
            runtimeProfile.Profile.Monitoring.PreferredUpsName,
            runtimeProfile.Profile,
            remoteWindowsService);
        viewModel.SetTransparencyPreference(settings.BackgroundTransparency);
        administration.SemanticReviewChanged += viewModel.SetSemanticReview;
        viewModel.ThemeChanged += async preference =>
        {
            ApplyTheme(preference);
            settingsPage.ApplyTheme(preference);
            try { await settingsPage.PersistThemeAsync(preference); } catch (OperationCanceledException) { }
        };
        settingsPage.ThemeChanged += viewModel.SetTheme;
        settingsPage.SidebarPreferenceChanged += preference => viewModel.SidebarPreference = preference;
        viewModel.SidebarPreferenceChanged += settingsPage.ApplySidebarPreference;
        settingsPage.BackgroundTransparencyChanged += viewModel.SetTransparencyPreference;
        settingsPage.ProfilePersisted += async profile =>
        {
            // The process keeps the monitoring endpoint, the configuration transport and their
            // credentials with which it started. Two things are narrow enough to take effect at once.
            if (profile.Id != runtimeProfile.Profile.Id) return;

            // Managed-file scope is presentation and write authorization for the same runtime profile.
            administration.UpdateManagedConfigurationFiles(profile.Management.ManagedFiles);
            viewModel.UpdateManagedConfigurationFiles(profile.Management.ManagedFiles);

            // The access mode, which used to need a restart. Every surface reporting it derives from a
            // profile copy taken at startup, so the interface went on claiming Manage after the profile
            // had been saved as read-only — a stale claim about authorization, which is the worst kind
            // to leave standing.
            //
            // Widening to Manage still grants nothing by itself: writing remains gated on the safe-write
            // probe, and the administration page reports the capability as unverified until it has run.
            // The session goes first: it owns the write decision, and narrowing to read-only has to
            // revoke before any surface has a chance to render the old answer.
            remoteManagement?.ApplyAccessMode(profile.AccessMode);
            administration.ApplyAccessMode(profile.AccessMode);
            viewModel.ApplyAccessMode(profile.AccessMode);
            diagnostics.ApplyAccessMode(profile.AccessMode);

            // The agent's own settings, which used to need a restart for no good reason. Changing a
            // profile from named pipe to HTTPS and then finding the administration screen still using
            // the old transport is indistinguishable from the setting not having been saved.
            //
            // Rebuilt only when something about the agent actually changed. Saving an unrelated field
            // must not tear down a working agent connection, and comparing the settings is what keeps
            // this from becoming a hot reload of the whole profile.
            if (remoteWindowsService is null) return;

            var updated = profile.Management.Agent;
            if (updated == agentSettings) return;

            agentSettings = updated;

            // A fresh client for the new settings, through the same factory used at startup, so the
            // credential rules are the ones already reviewed: the agent's own stored secret, never the
            // SMB or SSH one, and session credentials keeping their precedence.
            var rebuilt = await NutAgentClientFactory.CreateAsync(
                updated, profile.Id, credentialStore, CancellationToken.None, agentSessionCredentials);

            remoteWindowsServiceControl?.Rebind(rebuilt);
            await remoteWindowsService.RebindAsync(rebuilt, updated.Transport);
            administration.RebindAgentClient(rebuilt);
        };
        viewModel.EffectiveThemeChanged += settingsPage.ApplyTransparencyAvailability;
        settingsPage.ApplyTransparencyAvailability(viewModel.IsEffectiveDark);
        ApplyTheme(settings.Theme);
        window.DataContext = viewModel;
        await devices.InitializeAsync();
        await diagnostics.RefreshLocalInstallationAsync();
        await administration.InitializeAsync();
        // Both, not just the configuration transport's: the agent keeps its secret under its own
        // entry, and refreshing only one left a stored agent credential reported as missing.
        await settingsPage.RefreshCredentialStatusesAsync();
        if (remoteManagement is not null)
        {
            await remoteManagement.RefreshStoredCredentialStatusAsync();
            await remoteManagement.TryConnectAndValidateConfiguredSmbAsync();
        }
        if (remoteWindowsService is not null)
        {
            // Runtime service state is never persisted as configuration. A single read on startup
            // restores the actual state instead; the existing visible-page monitor then keeps it
            // current without introducing another timer or another control path.
            await remoteWindowsService.RefreshAsync();
        }
    }

    private void ApplyTheme(ThemePreference preference)
    {
        RequestedThemeVariant = preference switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
