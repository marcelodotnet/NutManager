using NutManager.App.ViewModels;
using NutManager.Core.Agent;
using NutManager.Core.Configuration;
using NutManager.Core.Models;
using Xunit;

namespace NutManager.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void StartsOnOverviewPage()
    {
        var viewModel = new MainWindowViewModel();

        Assert.Equal(AppPage.Overview, viewModel.SelectedPage);
        Assert.IsType<OverviewPageViewModel>(viewModel.CurrentPage);
        Assert.True(viewModel.NavigationItems.Single(item => item.Page == AppPage.Overview).IsSelected);
    }

    [Fact]
    public void NavigateCommandChangesTheSelectedPage()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.NavigateCommand.Execute(AppPage.Diagnostics);

        Assert.Equal(AppPage.Diagnostics, viewModel.SelectedPage);
        Assert.IsType<DiagnosticsPageViewModel>(viewModel.CurrentPage);
        Assert.True(viewModel.NavigationItems.Single(item => item.Page == AppPage.Diagnostics).IsSelected);
        Assert.False(viewModel.NavigationItems.Single(item => item.Page == AppPage.Overview).IsSelected);
    }

    [Fact]
    public void AdministrationIsIncludedInNavigationAndOpensItsPageViewModel()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.NavigateCommand.Execute(AppPage.Administration);

        Assert.Equal(AppPage.Administration, viewModel.SelectedPage);
        Assert.IsType<AdministrationPageViewModel>(viewModel.CurrentPage);
        Assert.True(viewModel.NavigationItems.Single(item => item.Page == AppPage.Administration).IsSelected);
        Assert.Equal(
            [AppPage.Overview, AppPage.Devices, AppPage.Administration, AppPage.Diagnostics, AppPage.Settings],
            viewModel.NavigationItems.Select(item => item.Page));
    }

    [Fact]
    public void NavigationTogglePersistsCollapsedPreferenceOutsideCompactLayout()
    {
        var viewModel = CreateShell(sidebarPreference: SidebarPreference.Expanded);

        viewModel.ToggleNavigationCommand.Execute(null);

        Assert.Equal(SidebarPreference.Collapsed, viewModel.SidebarPreference);
        Assert.Equal(SidebarDisplayState.Collapsed, viewModel.SidebarDisplay);
        Assert.Equal(72, viewModel.SidebarWidth);
    }

    [Fact]
    public void ExpandedSidebarKeepsItsSemanticWidth()
    {
        var viewModel = CreateShell(sidebarPreference: SidebarPreference.Expanded);

        Assert.Equal(SidebarDisplayState.Expanded, viewModel.SidebarDisplay);
        Assert.Equal(220, viewModel.SidebarWidth);
    }

    [Fact]
    public void CompactOverlayDoesNotReplaceTheSavedSidebarPreference()
    {
        var viewModel = CreateShell(sidebarPreference: SidebarPreference.Expanded);
        viewModel.UpdateLayoutWidth(859);

        viewModel.ToggleNavigationCommand.Execute(null);

        Assert.Equal(SidebarPreference.Expanded, viewModel.SidebarPreference);
        Assert.True(viewModel.IsOverlayOpen);
        Assert.Equal(SidebarDisplayState.Overlay, viewModel.SidebarDisplay);
    }

    [Fact]
    public void HeaderThemeToggleMakesSystemPreferenceExplicitUsingEffectiveTheme()
    {
        var viewModel = new MainWindowViewModel(ThemePreference.System);

        viewModel.ToggleThemeCommand.Execute(true);

        Assert.Equal(ThemePreference.Light, viewModel.SelectedTheme);
    }

    [Fact]
    public void SystemThemeAdvertisesTheActionForTheEffectiveTheme()
    {
        var viewModel = new MainWindowViewModel(ThemePreference.System);

        viewModel.UpdateEffectiveTheme(true);
        Assert.True(viewModel.ShowLightThemeAction);
        Assert.False(viewModel.ShowDarkThemeAction);

        viewModel.UpdateEffectiveTheme(false);
        Assert.False(viewModel.ShowLightThemeAction);
        Assert.True(viewModel.ShowDarkThemeAction);
    }

    [Fact]
    public void HeaderThemeToggleMakesLightAndDarkPreferencesExplicit()
    {
        var light = new MainWindowViewModel(ThemePreference.Light);
        light.ToggleThemeCommand.Execute(false);
        Assert.Equal(ThemePreference.Dark, light.SelectedTheme);

        var dark = new MainWindowViewModel(ThemePreference.Dark);
        dark.ToggleThemeCommand.Execute(true);
        Assert.Equal(ThemePreference.Light, dark.SelectedTheme);
    }

    [Fact]
    public void ClosingCompactOverlayPreservesTheSidebarPreference()
    {
        var viewModel = CreateShell(SidebarPreference.Collapsed);
        viewModel.UpdateLayoutWidth(700);
        viewModel.ToggleNavigationCommand.Execute(null);

        viewModel.CloseNavigationOverlay();

        Assert.False(viewModel.IsOverlayOpen);
        Assert.Equal(SidebarPreference.Collapsed, viewModel.SidebarPreference);
    }

    [Fact]
    public void CompactOverlayUsesCloseSemanticsAndDisablesBackgroundInteraction()
    {
        var viewModel = CreateShell(SidebarPreference.Expanded);
        viewModel.UpdateLayoutWidth(700);

        viewModel.ToggleNavigationCommand.Execute(null);

        Assert.Equal("Recolher navegação", viewModel.NavigationToggleName);
        Assert.False(viewModel.IsBackgroundInteractionEnabled);

        viewModel.CloseNavigationOverlay();

        Assert.Equal("Expandir navegação", viewModel.NavigationToggleName);
        Assert.True(viewModel.IsBackgroundInteractionEnabled);
    }

    [Fact]
    public void NavigatingFromCompactOverlayReenablesTheShell()
    {
        var viewModel = CreateShell(SidebarPreference.Expanded);
        viewModel.UpdateLayoutWidth(700);
        viewModel.ToggleNavigationCommand.Execute(null);

        viewModel.NavigateCommand.Execute(AppPage.Diagnostics);

        Assert.False(viewModel.IsOverlayOpen);
        Assert.True(viewModel.IsBackgroundInteractionEnabled);
        Assert.Equal("Expandir navegação", viewModel.NavigationToggleName);
        Assert.Equal(AppPage.Diagnostics, viewModel.SelectedPage);
    }

    [Fact]
    public void MediumLayoutDoesNotExposeAVisuallyIneffectivePreferenceToggle()
    {
        var viewModel = CreateShell(SidebarPreference.Expanded);
        viewModel.UpdateLayoutWidth(1000);

        viewModel.ToggleNavigationCommand.Execute(null);

        Assert.False(viewModel.IsNavigationToggleVisible);
        Assert.Equal(SidebarPreference.Expanded, viewModel.SidebarPreference);
        Assert.Equal(SidebarDisplayState.Collapsed, viewModel.SidebarDisplay);
    }

    [Fact]
    public void ActiveProfileSummaryUsesLocalizedPresentationInsteadOfRawEnums()
    {
        var viewModel = new MainWindowViewModel(
            ThemePreference.Dark,
            new OverviewPageViewModel(),
            new DevicesPageViewModel(),
            language: UiLanguagePreference.EnUs,
            activeProfileName: "Remote UPS",
            managementMode: NutManagementMode.Remote,
            accessMode: ManagedNutServerAccessMode.ReadOnly);

        Assert.Equal("Remote UPS", viewModel.ActiveProfileName);
        Assert.Equal("Remote · Read only", viewModel.ActiveProfileModeText);
    }

    [Fact]
    public void ActiveConfigurationUsesCurrentProfileTransportsAndDynamicManagedFileCount()
    {
        var overview = new OverviewPageViewModel();
        var managedFiles = ManagedNutConfigurationFiles.Create(
            [NutConfigurationFileKind.UpsConf, NutConfigurationFileKind.UpsdConf, NutConfigurationFileKind.UpsmonConf]);
        var profile = CreateRemoteDashboardProfile(managedFiles);
        var viewModel = new MainWindowViewModel(
            ThemePreference.Dark,
            overview,
            new DevicesPageViewModel(),
            activeProfile: profile);

        Assert.Equal(
            ["GANDALF", "Remoto", "Gerenciar", "NOBREAK"],
            overview.ActiveProfileRows.Select(row => row.Value));
        Assert.Equal("SMB", ValueFor(overview.ActiveConnectivityRows, "Configuração via"));
        Assert.Equal("HTTPS", ValueFor(overview.ActiveConnectivityRows, "Controle via"));
        Assert.Equal("3 gerenciados", ValueFor(overview.ActiveConnectivityRows, "Arquivos NUT"));

        viewModel.UpdateManagedConfigurationFiles(
            ManagedNutConfigurationFiles.Create([NutConfigurationFileKind.UpsConf]));
        Assert.Equal("1 gerenciado", ValueFor(overview.ActiveConnectivityRows, "Arquivos NUT"));

        viewModel.UpdateManagedConfigurationFiles(ManagedNutConfigurationFiles.Create([]));
        Assert.Equal("0 gerenciados", ValueFor(overview.ActiveConnectivityRows, "Arquivos NUT"));
    }

    [Fact]
    public void ActiveConfigurationMirrorsExistingAgentObservationWithStaticSemanticState()
    {
        var overview = new OverviewPageViewModel();
        var profile = CreateRemoteDashboardProfile(ManagedNutConfigurationFiles.All);
        var agent = new RemoteWindowsServiceViewModel(
            profile.Monitoring.Host,
            new NoOperationAgentClient(),
            transport: profile.Management.Agent.Transport);
        _ = new MainWindowViewModel(
            ThemePreference.Dark,
            overview,
            new DevicesPageViewModel(),
            activeProfile: profile,
            remoteWindowsService: agent);

        var unavailable = overview.ActiveConnectivityRows.Single(row => row.Label == "Agente");
        Assert.Equal("Desconectado", unavailable.Value);
        Assert.True(unavailable.IsCritical);
        Assert.False(unavailable.IsHealthy);

        agent.Observation = new NutAgentObservation(
            profile.Monitoring.Host,
            NutAgentClientStatus.Success,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.Parse("2026-08-18T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        var connected = overview.ActiveConnectivityRows.Single(row => row.Label == "Agente");
        Assert.Equal("Conectado", connected.Value);
        Assert.True(connected.IsHealthy);
        Assert.False(connected.IsCritical);
    }

    [Fact]
    public void ShellMirrorsExistingConnectionStateWithoutStartingAnotherOperation()
    {
        var overview = new OverviewPageViewModel();
        var viewModel = new MainWindowViewModel(
            ThemePreference.System,
            overview,
            new DevicesPageViewModel(),
            activeEndpoint: "127.0.0.1:3493");

        overview.ConnectionState = ConnectionState.Reconnecting;
        overview.DataFreshness = DataFreshness.Stale;

        Assert.Equal(ConnectionPresentationState.Pending, viewModel.ConnectionPresentation);
        Assert.True(viewModel.IsConnectionPending);
    }

    [Fact]
    public void HeaderUsesTheActuallyMonitoredUpsWhenASnapshotArrives()
    {
        var overview = new OverviewPageViewModel();
        var viewModel = new MainWindowViewModel(
            ThemePreference.Dark,
            overview,
            new DevicesPageViewModel(),
            activeEndpoint: "nut.example:3493",
            preferredUpsName: "CONFIGURED");

        Assert.Contains("CONFIGURED@nut.example:3493", viewModel.ConnectionSummaryText, StringComparison.Ordinal);

        overview.Snapshot = new UpsSnapshot(
            new UpsIdentity("LIVE"),
            [],
            new Dictionary<string, UpsVariable>(),
            DateTimeOffset.UtcNow,
            DataSource.Live);

        Assert.Contains("LIVE@nut.example:3493", viewModel.ConnectionSummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("CONFIGURED@", viewModel.ConnectionSummaryText, StringComparison.Ordinal);
        Assert.Equal(viewModel.ConnectionSummaryText, viewModel.ConnectionTooltip);
    }

    [Fact]
    public void ConnectedStateWithoutAnEndpointOrSnapshotIsUnavailable()
    {
        var overview = new OverviewPageViewModel
        {
            ConnectionState = ConnectionState.Connected,
            DataFreshness = DataFreshness.Fresh
        };
        var viewModel = new MainWindowViewModel(ThemePreference.Dark, overview);

        Assert.Equal(ConnectionPresentationState.Unavailable, viewModel.ConnectionPresentation);
    }

    [Fact]
    public void ProfileQuickMenuProjectsEveryExistingCardAndDoesNotActivateOnSelection()
    {
        var active = CreateProfile("Local", NutManagementMode.Local, ManagedNutServerAccessMode.Manage);
        var other = CreateProfile("Remote", NutManagementMode.Remote, ManagedNutServerAccessMode.ReadOnly);
        var profiles = new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, active.Id, [active, other]);
        var settings = new SettingsPageViewModel(new ApplicationSettings(), null, profiles, null, runtimeProfileId: active.Id);
        var viewModel = new MainWindowViewModel(
            ThemePreference.System,
            new OverviewPageViewModel(),
            new DevicesPageViewModel(),
            settings);

        viewModel.OpenManagedProfileCommand.Execute(settings.ManagedProfileCards.Single(card => card.Profile.Id == other.Id));

        Assert.Equal(2, viewModel.ManagedProfileCards.Count);
        Assert.Single(viewModel.ManagedProfileCards, card => card.IsActive);
        Assert.Equal(other.Id, settings.SelectedManagedProfile?.Id);
        Assert.Equal(active.Id, profiles.ActiveProfileId);
        Assert.Equal(AppPage.Settings, viewModel.SelectedPage);
    }

    [Fact]
    public void ProfileQuickMenuSelectionPreservesDirtyDraftDecisionFlow()
    {
        var active = CreateProfile("Local", NutManagementMode.Local, ManagedNutServerAccessMode.Manage);
        var other = CreateProfile("Remote", NutManagementMode.Remote, ManagedNutServerAccessMode.ReadOnly);
        var profiles = new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, active.Id, [active, other]);
        var settings = new SettingsPageViewModel(new ApplicationSettings(), null, profiles, null, runtimeProfileId: active.Id);
        var viewModel = new MainWindowViewModel(
            ThemePreference.System,
            new OverviewPageViewModel(),
            new DevicesPageViewModel(),
            settings);
        settings.ProfileDraft.Name = "Unsaved";

        viewModel.OpenManagedProfileCommand.Execute(settings.ManagedProfileCards.Single(card => card.Profile.Id == other.Id));

        Assert.Equal(active.Id, settings.SelectedManagedProfile?.Id);
        Assert.True(settings.IsDirtyDraftDecisionVisible);
        Assert.True(settings.IsProfileDraftDirty);
        Assert.Equal(AppPage.Settings, viewModel.SelectedPage);
    }

    [Fact]
    public void CompactLayoutHidesOnlyFooterAuthorship()
    {
        var viewModel = CreateShell(SidebarPreference.Expanded);

        Assert.True(viewModel.IsFooterAuthorshipVisible);
        viewModel.UpdateLayoutWidth(700);
        Assert.False(viewModel.IsFooterAuthorshipVisible);
        Assert.NotEmpty(viewModel.AdministrationConfirmationText);
        Assert.NotEmpty(viewModel.ConnectionStatusText);
    }

    private static ManagedNutServerProfile CreateProfile(
        string name,
        NutManagementMode managementMode,
        ManagedNutServerAccessMode accessMode) => new(
        Guid.NewGuid(),
        name,
        new NutMonitoringProfile("127.0.0.1"),
        managementMode == NutManagementMode.Remote
            ? new NutManagementProfile(managementMode, "management.example", "/etc/nut")
            : new NutManagementProfile(managementMode),
        accessMode);

    private static ManagedNutServerProfile CreateRemoteDashboardProfile(ManagedNutConfigurationFiles managedFiles) => new(
        Guid.NewGuid(),
        "GANDALF",
        new NutMonitoringProfile("gandalf.sbra.local", preferredUpsName: "NOBREAK"),
        new NutManagementProfile(
            NutManagementMode.Remote,
            configurationTransport: RemoteConfigurationTransportKind.Smb,
            smbSharePath: @"\\GANDALF\etc",
            managedFiles: managedFiles,
            agent: new NutAgentProfileSettings(
                NutAgentTransportKind.Https,
                "https://gandalf.sbra.local:5199/")),
        ManagedNutServerAccessMode.Manage);

    private static string ValueFor(IEnumerable<OverviewInfoRowViewModel> rows, string label) =>
        rows.Single(row => row.Label == label).Value;

    private sealed class NoOperationAgentClient : INutManagerAgentClient
    {
        public Task<NutAgentClientResult<NutAgentHandshake>> HandshakeAsync(string host, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The dashboard must not start agent I/O.");

        public Task<NutAgentClientResult<NutAgentServiceStatus>> GetStatusAsync(string host, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The dashboard must not start agent I/O.");

        public Task<NutAgentClientResult<NutAgentHardwareSnapshot>> GetHardwareSnapshotAsync(string host, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The dashboard must not start agent I/O.");

        public Task<NutAgentClientResult<NutAgentOperationResult>> StartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The dashboard must not mutate the agent.");

        public Task<NutAgentClientResult<NutAgentOperationResult>> StopAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The dashboard must not mutate the agent.");

        public Task<NutAgentClientResult<NutAgentOperationResult>> RestartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The dashboard must not mutate the agent.");
    }

    private static MainWindowViewModel CreateShell(SidebarPreference sidebarPreference) => new(
        ThemePreference.System,
        new OverviewPageViewModel(),
        new DevicesPageViewModel(),
        sidebarPreference: sidebarPreference);

    [Fact]
    public void LightThemeDrawsTheOpaqueBackdropWithoutDiscardingTheTransparencyChoice()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SetTransparencyPreference(true);
        viewModel.UpdateEffectiveTheme(true);

        Assert.True(viewModel.IsBackgroundTransparent);

        // Acrylic only reads as glass under the dark palette. Over the near-white light one the same
        // backdrop washes the page out instead of revealing anything, so the opaque panel is drawn.
        viewModel.UpdateEffectiveTheme(false);
        Assert.False(viewModel.IsBackgroundTransparent);

        // The choice was suppressed for the duration, not forgotten: coming back restores it, so a
        // trip through light theme does not silently reset a preference the user set once.
        viewModel.UpdateEffectiveTheme(true);
        Assert.True(viewModel.IsBackgroundTransparent);
    }

    [Fact]
    public void TransparencyTurnedOffStaysOffAcrossThemeChanges()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SetTransparencyPreference(false);

        Assert.False(viewModel.IsBackgroundTransparent);

        viewModel.UpdateEffectiveTheme(false);
        Assert.False(viewModel.IsBackgroundTransparent);

        // Returning to dark must not switch transparency back on by itself.
        viewModel.UpdateEffectiveTheme(true);
        Assert.False(viewModel.IsBackgroundTransparent);
    }

    [Fact]
    public void TheTransparencySwitchIsOperableOnlyUnderTheDarkPalette()
    {
        var settings = new SettingsPageViewModel();

        // Disabled rather than silently doing nothing, which is what the hint beside it explains.
        settings.ApplyTransparencyAvailability(false);
        Assert.False(settings.IsTransparencyAvailable);

        settings.ApplyTransparencyAvailability(true);
        Assert.True(settings.IsTransparencyAvailable);
    }

    [Fact]
    public void TheBlockedSwitchReadsOffWhileTheStoredChoiceStaysOn()
    {
        var settings = new SettingsPageViewModel { IsBackgroundTransparent = true };

        // Under the light palette the window draws the opaque backdrop, so a switch still reading
        // "on" would claim a transparency that is not there.
        settings.ApplyTransparencyAvailability(false);
        Assert.False(settings.IsTransparencyEffective);
        Assert.True(settings.IsBackgroundTransparent);

        // And the choice is intact when the palette can honour it again.
        settings.ApplyTransparencyAvailability(true);
        Assert.True(settings.IsTransparencyEffective);
    }

    [Fact]
    public void TheBlockedSwitchCannotOverwriteTheStoredChoice()
    {
        var settings = new SettingsPageViewModel { IsBackgroundTransparent = true };
        settings.ApplyTransparencyAvailability(false);

        // Nothing the user can reach writes here while the control is disabled, but the setter
        // refuses anyway: a stray binding write must not erase a preference the user cannot see.
        settings.IsTransparencyEffective = true;
        Assert.True(settings.IsBackgroundTransparent);

        settings.IsTransparencyEffective = false;
        Assert.True(settings.IsBackgroundTransparent);
    }
}
