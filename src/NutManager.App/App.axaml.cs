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
        settingsPage.ProfilePersisted += profile =>
        {
            // The process keeps the endpoint, transport and credentials with which it started.
            // Managed-file scope is presentation/write authorization for that same runtime profile,
            // however, and can safely take effect immediately after its profile was persisted.
            if (profile.Id == runtimeProfile.Profile.Id)
            {
                administration.UpdateManagedConfigurationFiles(profile.Management.ManagedFiles);
                viewModel.UpdateManagedConfigurationFiles(profile.Management.ManagedFiles);
            }
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
