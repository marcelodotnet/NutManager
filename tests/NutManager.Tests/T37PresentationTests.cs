using NutManager.App.ViewModels;
using Xunit;

namespace NutManager.Tests;

/// <summary>Stable structural guards for the first T37 layout and navigation polish pass.</summary>
public sealed class T37PresentationTests
{
    [Fact]
    public void EverySidebarDestinationUsesTheSharedClippingSafeNavigationIcon()
    {
        var window = Read("src", "NutManager.App", "MainWindow.axaml");
        var icon = Read("src", "NutManager.App", "Presentation", "Controls", "NutNavigationIcon.axaml");
        var viewModel = new MainWindowViewModel();

        Assert.Equal(5, viewModel.NavigationItems.Count);
        Assert.Equal(2, window.Split("<controls:NutNavigationIcon Kind=\"{Binding Page}\" />", StringSplitOptions.None).Length - 1);
        foreach (var flag in new[] { "IsOverview", "IsDevices", "IsAdministration", "IsDiagnostics", "IsSettings" })
        {
            Assert.Contains($"IsVisible=\"{{Binding {flag}, ElementName=Root}}\"", icon, StringComparison.Ordinal);
        }

        var motion = Read("src", "NutManager.App", "Presentation", "Controls", "NutIconMotion.cs");
        var styles = Read("src", "NutManager.App", "Presentation", "Themes", "NutShellStyles.axaml");
        Assert.DoesNotContain("visual.Offset =", motion, StringComparison.Ordinal);
        Assert.DoesNotContain("translateY(-1px)", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedServerListAndMonitoringShareTheWideRowWithNameBesideHost()
    {
        var view = Read("src", "NutManager.App", "Views", "SettingsPageView.axaml");
        var behavior = Read("src", "NutManager.App", "Views", "SettingsPageView.axaml.cs");

        Assert.Contains("ColumnDefinitions=\"330,16,*\"", view, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProfileListPanel\"", view, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProfileEditorHeader\"", view, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProfileIdentityPanel\" Grid.Row=\"2\" Grid.Column=\"2\"", view, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProfileMonitoringPanel\"", view, StringComparison.Ordinal);
        var monitoring = view[view.IndexOf("x:Name=\"ProfileMonitoringPanel\"", StringComparison.Ordinal)..];
        Assert.True(monitoring.IndexOf("NameLabel", StringComparison.Ordinal) < monitoring.IndexOf("MonitoringHostLabel", StringComparison.Ordinal));
        Assert.Contains("Position(ProfileEditorHeader, compact ? 0 : 2, compact ? 2 : 0)", behavior, StringComparison.Ordinal);
        Assert.Contains("Position(ProfileListPanel, 0, compact ? 0 : 2)", behavior, StringComparison.Ordinal);
        Assert.Contains("Position(ProfileIdentityPanel, compact ? 0 : 2, compact ? 4 : 2)", behavior, StringComparison.Ordinal);
        Assert.Contains("Position(ProfileEditorPanel, 0, compact ? 6 : 4)", behavior, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteConfigurationContextSharesTheWideNavigationRowAndStacksWhenNarrow()
    {
        var view = Read("src", "NutManager.App", "Views", "NutConfigurationAdministrationView.axaml");
        var behavior = Read("src", "NutManager.App", "Views", "NutConfigurationAdministrationView.axaml.cs");

        var filesRegion = view[view.IndexOf("x:Name=\"ConfigurationFilesRegion\"", StringComparison.Ordinal)..];
        filesRegion = filesRegion[..filesRegion.IndexOf('>')];
        Assert.Contains("Grid.Column=\"0\"", filesRegion, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RemoteConfigurationCard\"", view, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", view[view.IndexOf("x:Name=\"RemoteConfigurationCard\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsRemoteManagementProfile}\"", view, StringComparison.Ordinal);
        Assert.Contains("var wide = Bounds.Width >= 980", behavior, StringComparison.Ordinal);
        Assert.Contains("var sideBySide = wide && filesVisible", behavior, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(RemoteConfigurationCard, sideBySide ? 2 : 0)", behavior, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(RemoteConfigurationCard, filesVisible && !sideBySide ? 2 : 0)", behavior, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-card\"", view[view.IndexOf("x:Name=\"RemoteConfigurationCard\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Stretch\"", view[view.IndexOf("x:Name=\"RemoteConfigurationCard\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteConfigurationCard.MaxWidth", behavior, StringComparison.Ordinal);

        // Only the position changed; the existing visibility predicate remains the sole predicate.
        Assert.Equal(1, view.Split("IsVisible=\"{Binding IsRemoteManagementProfile}\"", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void AdministrationMenusUseEqualRoundedProductSurfaces()
    {
        var view = Read("src", "NutManager.App", "Views", "AdministrationPageView.axaml");
        var styles = Read("src", "NutManager.App", "Presentation", "Themes", "NutShellStyles.axaml");

        Assert.Contains("<Border Classes=\"nut-card\" Padding=\"8\">", view, StringComparison.Ordinal);
        Assert.Contains("<UniformGrid Columns=\"4\" Rows=\"1\" />", view, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.nut-file-strip-frame\"", styles, StringComparison.Ordinal);
        Assert.Contains("Property=\"CornerRadius\" Value=\"20\"", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingAProfilePreservesTheSettingsScrollOffset()
    {
        var view = Read("src", "NutManager.App", "Views", "SettingsPageView.axaml");
        var behavior = Read("src", "NutManager.App", "Views", "SettingsPageView.axaml.cs");

        Assert.Contains("x:Name=\"SettingsScrollViewer\"", view, StringComparison.Ordinal);
        Assert.Contains("Click=\"SaveAllButton_OnClick\"", view, StringComparison.Ordinal);
        Assert.Contains("var offset = SettingsScrollViewer.Offset", behavior, StringComparison.Ordinal);
        Assert.Contains("SettingsScrollViewer.Offset = offset", behavior, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Background", behavior, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralSettingsAndFinalActionsShareTheManagedServersCard()
    {
        var view = Read("src", "NutManager.App", "Views", "SettingsPageView.axaml");
        var card = view.IndexOf("x:Name=\"ManagedServersCard\"", StringComparison.Ordinal);
        var management = view.IndexOf("x:Name=\"ManagedProfilesLayout\"", StringComparison.Ordinal);
        var general = view.IndexOf("x:Name=\"GeneralSettingsSection\"", StringComparison.Ordinal);
        var save = view.IndexOf("x:Name=\"SettingsCommitBar\"", StringComparison.Ordinal);
        var discard = view.IndexOf("Command=\"{Binding DiscardAllCommand}\"", StringComparison.Ordinal);

        Assert.True(card >= 0 && management > card && general > management && save > general && discard > save);
        Assert.Equal(1, view.Split("Click=\"SaveAllButton_OnClick\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("x:Name=\"SettingsCommitBar\" ColumnDefinitions=\"Auto,Auto,*\"", view, StringComparison.Ordinal);
        Assert.Contains("<StackPanel Grid.Column=\"2\" VerticalAlignment=\"Center\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"SaveProfileButton_OnClick\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding SaveCommand}\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDurationsAndNutPortUseCompactNumericControls()
    {
        var view = Read("src", "NutManager.App", "Views", "SettingsPageView.axaml");
        var behavior = Read("src", "NutManager.App", "Views", "SettingsPageView.axaml.cs");
        var upsEditor = Read("src", "NutManager.App", "Views", "UpsConfigurationEditorView.axaml");
        var styles = Read("src", "NutManager.App", "Presentation", "Themes", "NutControlStyles.axaml");

        Assert.Contains("ProfileDraft.MonitoringPortValue, Mode=TwoWay", view, StringComparison.Ordinal);
        Assert.Contains("ConnectionTimeoutSecondsValue, Mode=TwoWay", view, StringComparison.Ordinal);
        Assert.Contains("PollingIntervalSecondsValue, Mode=TwoWay", view, StringComparison.Ordinal);
        Assert.Equal(3, view.Split("<NumericUpDown", StringSplitOptions.None).Length - 1);
        var general = view[view.IndexOf("x:Name=\"GeneralSettingsSection\"", StringComparison.Ordinal)..];
        Assert.Equal(2, general.Split("Width=\"220\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("Maximum=\"65535\"", view, StringComparison.Ordinal);
        Assert.Contains("ParsingNumberStyle=\"Integer\"", view, StringComparison.Ordinal);
        Assert.Equal(4, upsEditor.Split("<NumericUpDown", StringSplitOptions.None).Length - 1);
        Assert.Equal(4, upsEditor.Split("ParsingNumberStyle=\"Integer\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("x:Name=\"GeneralPreferencesLayout\" ColumnDefinitions=\"220,220\" ColumnSpacing=\"18\"", view, StringComparison.Ordinal);
        Assert.Contains("new ColumnDefinitions(\"220,220\")", behavior, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding ProfileDraft.MonitoringPort", view, StringComparison.Ordinal);
        var numericStyles = styles[styles.IndexOf("<Style Selector=\"NumericUpDown\">", StringComparison.Ordinal)..];
        Assert.Contains("Style Selector=\"NumericUpDown\"", styles, StringComparison.Ordinal);
        Assert.Contains("ButtonSpinner#PART_Spinner", numericStyles, StringComparison.Ordinal);
        Assert.Contains("Property=\"CornerRadius\" Value=\"8\"", numericStyles, StringComparison.Ordinal);
        Assert.Contains("Property=\"ShowButtonSpinner\" Value=\"True\"", numericStyles, StringComparison.Ordinal);
        // Not clipping: ClipToBounds clips to the rectangular bounds rather than to the rounded
        // geometry, so clipping the control and the spinner left the square corners exactly as they
        // were. And not a style either — the stock spinner gives its RepeatButtons a control theme of
        // their own, which no outside selector can outrank; targeting them through
        // NumericUpDown /template/ ButtonSpinner /template/ RepeatButton changed nothing, not even
        // their background. The template is replaced instead, which is how the scroll bar is handled.
        Assert.DoesNotContain("Property=\"ClipToBounds\"", numericStyles, StringComparison.Ordinal);

        // Both PART names have to survive the replacement: the ButtonSpinner binds its spin events to
        // them, so losing one would leave a control that looks right and no longer counts.
        Assert.Contains("Name=\"PART_IncreaseButton\"", numericStyles, StringComparison.Ordinal);
        Assert.Contains("Name=\"PART_DecreaseButton\"", numericStyles, StringComparison.Ordinal);

        // The decrease button is the one touching the right edge, so it carries the radius. 7 rather
        // than 8 because the container's 1px border sits outside it, and an inner curve has to be the
        // outer curve minus that thickness or the two are not concentric.
        Assert.Contains("CornerRadius=\"0,7,7,0\"", numericStyles, StringComparison.Ordinal);
        Assert.Contains("NutSpinnerButton", numericStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedServerListHasSubtleRoundedClipping()
    {
        var view = Read("src", "NutManager.App", "Views", "SettingsPageView.axaml");
        var listPanel = view.IndexOf("x:Name=\"ProfileListPanel\"", StringComparison.Ordinal);
        var roundedList = view.IndexOf("<Border CornerRadius=\"12\" ClipToBounds=\"True\"", StringComparison.Ordinal);
        var list = view.IndexOf("<ListBox ItemsSource=\"{Binding ManagedProfileCards}\"", StringComparison.Ordinal);

        Assert.True(listPanel >= 0 && roundedList > listPanel && list > roundedList);
    }

    [Fact]
    public void ProfileQuickMenuUsesReadableCardsAndWrappingMetadata()
    {
        var shell = Read("src", "NutManager.App", "MainWindow.axaml");
        var styles = Read("src", "NutManager.App", "Presentation", "Themes", "NutShellStyles.axaml");
        var menu = shell[shell.IndexOf("x:Name=\"ProfileQuickMenuButton\"", StringComparison.Ordinal)..];

        Assert.Contains("Classes=\"nut-profile-menu-item\"", menu, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", menu, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-pill healthy\"", menu, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding Endpoint}\"", menu, StringComparison.Ordinal);
        // The profile rows live in the popup, which now follows the button rather than being nested
        // inside it — the selector's own avatar sits between the two, so the slice has to start at
        // the popup or it picks up an avatar that was never in the list.
        var popup = menu.IndexOf("x:Name=\"ProfileQuickMenuPopup\"", StringComparison.Ordinal);
        Assert.True(popup >= 0, "the profile list is presented by a popup on the window's overlay layer");
        var profileItems = menu[popup..menu.IndexOf("Classes=\"nut-profile-manage\"", StringComparison.Ordinal)];
        Assert.DoesNotContain("Classes=\"nut-profile-avatar\"", profileItems, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.nut-profile-menu-item\"", styles, StringComparison.Ordinal);
        Assert.Contains("Property=\"CornerRadius\" Value=\"10\"", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void SidebarProfileSelectorUsesAnimatedAccentGlowOnHover()
    {
        var styles = Read("src", "NutManager.App", "Presentation", "Themes", "NutShellStyles.axaml");
        var shell = Read("src", "NutManager.App", "MainWindow.axaml");
        var hover = styles.IndexOf(
            "Style Selector=\"Button.nut-profile-card:pointerover Border.nut-profile-card-surface\"",
            StringComparison.Ordinal);

        Assert.True(hover >= 0);
        Assert.Contains("BoxShadowsTransition Property=\"BoxShadow\"", styles, StringComparison.Ordinal);
        Assert.Contains("Property=\"CornerRadius\" Value=\"12\"", styles, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-profile-card-surface\"", shell, StringComparison.Ordinal);
        Assert.Contains("0 0 10 0 #665FA8FF, 0 0 24 3 #335FA8FF", styles[hover..], StringComparison.Ordinal);
    }

    [Fact]
    public void PersistedManagedFileScopeIsForwardedToTheRunningAdministrationContext()
    {
        var app = Read("src", "NutManager.App", "App.axaml.cs");

        Assert.Contains("settingsPage.ProfilePersisted += profile =>", app, StringComparison.Ordinal);
        Assert.Contains("profile.Id == runtimeProfile.Profile.Id", app, StringComparison.Ordinal);
        Assert.Contains(
            "administration.UpdateManagedConfigurationFiles(profile.Management.ManagedFiles)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "viewModel.UpdateManagedConfigurationFiles(profile.Management.ManagedFiles)",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveConfigurationUsesTwoResponsiveColumnsAndAStaticAgentIndicator()
    {
        var view = Read("src", "NutManager.App", "Views", "OverviewPageView.axaml");
        var behavior = Read("src", "NutManager.App", "Views", "OverviewPageView.axaml.cs");
        var styles = Read("src", "NutManager.App", "Presentation", "Themes", "NutControlStyles.axaml");

        Assert.Contains("x:Name=\"ActiveConfigurationLayout\"", view, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ActiveProfileRows}\"", view, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ActiveConnectivityRows}\"", view, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-agent-status-dot\"", view, StringComparison.Ordinal);
        Assert.Contains("Static by design: Agent reachability must not pulse, glow or animate", view, StringComparison.Ordinal);
        Assert.Contains("fitsIllustration ? \"*,*,Auto\" : fitsTwoColumns ? \"*,*\" : \"*\"", behavior, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(ActiveConnectivityRows, fitsTwoColumns ? 0 : 1)", behavior, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(ActiveConfigurationIllustration, fitsIllustration ? 2 : 0)", behavior, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.SetColumn(ActiveConfigurationIllustration, 2)", behavior, StringComparison.Ordinal);

        var indicatorStart = styles.IndexOf("Style Selector=\"Ellipse.nut-agent-status-dot\"", StringComparison.Ordinal);
        var indicatorEnd = styles.IndexOf("<!-- ==================== Surfaces", indicatorStart, StringComparison.Ordinal);
        Assert.True(indicatorStart >= 0 && indicatorEnd > indicatorStart);
        var indicatorStyles = styles[indicatorStart..indicatorEnd];
        Assert.Contains("NutHealthyBrush", indicatorStyles, StringComparison.Ordinal);
        Assert.Contains("NutCriticalBrush", indicatorStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("Transition", indicatorStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("Animation", indicatorStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void SmbDirectoryHasOneEditableSourceAndAdministrationOnlyValidatesIt()
    {
        var settings = Read("src", "NutManager.App", "Views", "SettingsPageView.axaml");
        var remote = Read("src", "NutManager.App", "Views", "RemoteAccessAdministrationView.axaml");

        Assert.Contains("ProfileDraft.SmbSharePath, Mode=TwoWay", settings, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"{Binding RemoteManagement.IsSmbDirectoryFixed}\"", remote, StringComparison.Ordinal);
        Assert.Contains("Administration.Remote.SmbDirectory.Fixed", remote, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding RemoteManagement.IsSshSftp}\"", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteValidateDirectoryButton_OnClick", remote, StringComparison.Ordinal);

        var probe = remote.IndexOf("RemoteProbeWriteButton_OnClick", StringComparison.Ordinal);
        var directory = remote.IndexOf("Administration.Remote.Directory]", StringComparison.Ordinal);
        Assert.True(probe >= 0 && directory >= 0 && probe < directory);
        Assert.Contains("IsWriteCapabilityUnverified", remote, StringComparison.Ordinal);
        Assert.Contains("IsWriteCapabilitySupported", remote, StringComparison.Ordinal);
        Assert.Contains("IsWriteCapabilityRejected", remote, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-danger\"", remote, StringComparison.Ordinal);
        Assert.Contains("Administration.Remote.SafeWrite.Verify", remote, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-success-outline nut-status-locked\"", remote, StringComparison.Ordinal);
        Assert.Contains("Administration.Remote.SafeWrite.Verified", remote, StringComparison.Ordinal);
        Assert.Equal(2, remote.Split("Width=\"176\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, remote.Split("FontWeight=\"Bold\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, remote.Split("HorizontalContentAlignment=\"Center\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("IsEnabled=\"False\"", remote, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding !RemoteManagement.IsWriteCapabilitySupported}\"", remote, StringComparison.Ordinal);

        var directoryCard = remote.IndexOf("<!-- SFTP keeps its directory browser", StringComparison.Ordinal);
        Assert.True(directoryCard >= 0);
        Assert.Contains("IsVisible=\"{Binding RemoteManagement.ShowsDirectoryBrowser}\"",
            remote[directoryCard..], StringComparison.Ordinal);
    }

    [Fact]
    public void StartupRestoresSavedSmbContextAndReadsActualRemoteServiceStateOnce()
    {
        var app = Read("src", "NutManager.App", "App.axaml.cs");

        Assert.Contains("TryConnectAndValidateConfiguredSmbAsync", app, StringComparison.Ordinal);
        Assert.Contains("await remoteWindowsService.RefreshAsync()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("new PeriodicTimer", app, StringComparison.Ordinal);
    }

    [Fact]
    public void BasicAndAdvancedConfigurationOptionsUseProductCards()
    {
        var ups = Read("src", "NutManager.App", "Views", "UpsConfigurationEditorView.axaml");
        var general = Read("src", "NutManager.App", "Views", "NutGeneralConfigurationEditorView.axaml");
        var server = Read("src", "NutManager.App", "Views", "UpsdConfigurationEditorView.axaml");
        var monitoring = Read("src", "NutManager.App", "Views", "UpsmonConfigurationEditorView.axaml");

        Assert.Contains("Classes=\"nut-card\" IsVisible=\"{Binding IsBasicSelected}\"", ups, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-card\" IsVisible=\"{Binding ShowAdvanced}\"", ups, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-card\" IsVisible=\"{Binding HasGlobalFields}\"", ups, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-card\" IsVisible=\"{Binding !ShowAdvanced}\"", general, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-card\" IsVisible=\"{Binding ShowAdvanced}\"", general, StringComparison.Ordinal);
        Assert.Contains("<Border Classes=\"nut-card\">", server, StringComparison.Ordinal);
        Assert.Contains("<Border Classes=\"nut-card\">", monitoring, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding BasicFields}\"", monitoring, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding AdvancedFields}\"", monitoring, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadedRemoteFileShowsAuthorizationWarningAndCapabilityRefreshKeepsItsSnapshot()
    {
        var view = Read("src", "NutManager.App", "Views", "NutConfigurationAdministrationView.axaml");
        var viewModel = Read("src", "NutManager.App", "ViewModels", "AdministrationPageViewModel.cs");

        Assert.Contains("RequiresRemoteWriteAuthorization", view, StringComparison.Ordinal);
        Assert.Contains("Administration.Configuration.WriteAuthorizationRequired", view, StringComparison.Ordinal);
        Assert.Contains("preservesLoadedFile", viewModel, StringComparison.Ordinal);
        Assert.Contains("BuildEditorsAsync(snapshot!, CancellationToken.None)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadSelectedFileAsync(CancellationToken.None", viewModel[
            viewModel.IndexOf("private async void OnRemoteConfigurationContextChanged", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsAreOneResponsiveDocumentRatherThanThreeTabs()
    {
        var view = Read("src", "NutManager.App", "Views", "DiagnosticsPageView.axaml");
        var behavior = Read("src", "NutManager.App", "Views", "DiagnosticsPageView.axaml.cs");

        Assert.DoesNotContain("ShowOverviewTabCommand", view, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowConnectivityTabCommand", view, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowEnvironmentTabCommand", view, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DiagnosticsSummaryGrid\"", view, StringComparison.Ordinal);
        Assert.Contains("width >= 1120 ? 4 : width >= 650 ? 2 : 1", behavior, StringComparison.Ordinal);
        Assert.Contains("Diagnostics.Group.Overview", view, StringComparison.Ordinal);
        Assert.Contains("Diagnostics.Group.Connection", view, StringComparison.Ordinal);
        Assert.Contains("Diagnostics.Group.Environment", view, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsLeavesRoomBetweenVersionAndRuntime()
    {
        var view = Read("src", "NutManager.App", "Views", "DiagnosticsPageView.axaml");

        var version = view.IndexOf("ApplicationVersion", StringComparison.Ordinal);
        var runtime = view.IndexOf("Diagnostics.Runtime", version, StringComparison.Ordinal);
        Assert.True(version >= 0 && runtime > version);
        Assert.Contains("Width=\"300\" Margin=\"0,0,36,10\"", view[(version - 220)..runtime], StringComparison.Ordinal);
    }

    [Fact]
    public void PrimaryStatusGlowRespondsWithoutADeadHoverInterval()
    {
        var styles = Read("src", "NutManager.App", "Presentation", "Themes", "NutControlStyles.axaml");
        var pills = styles[styles.IndexOf("<Style Selector=\"Border.nut-pill\">", StringComparison.Ordinal)..];

        Assert.Contains("0 0 0 0 #00000000, 0 0 0 0 #00000000", pills, StringComparison.Ordinal);
        Assert.Contains("BoxShadowsTransition Property=\"BoxShadow\" Duration=\"0:0:0.08\"", pills, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionShellNoLongerOffersMockModeAndCardCopyUsesTheReadableToken()
    {
        var app = Read("src", "NutManager.App", "App.axaml.cs");
        var settings = Read("src", "NutManager.App", "Views", "SettingsPageView.axaml");
        var shell = Read("src", "NutManager.App", "MainWindow.axaml");
        var typography = Read("src", "NutManager.App", "Presentation", "Themes", "NutTypography.axaml");

        Assert.DoesNotContain("new MockNutClient", app, StringComparison.Ordinal);
        Assert.DoesNotContain("MockModeLabel", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SimulationText", shell, StringComparison.Ordinal);
        Assert.Contains("Border.nut-card TextBlock.nut-metadata", typography, StringComparison.Ordinal);
        Assert.Contains("NutCardSmallTextBrush", typography, StringComparison.Ordinal);
    }

    [Fact]
    public void LatestPresentationPolishKeepsStatusAndScrollSemanticsExplicit()
    {
        var overview = Read("src", "NutManager.App", "Views", "OverviewPageView.axaml");
        var diagnostics = Read("src", "NutManager.App", "Views", "DiagnosticsPageView.axaml");
        var administration = Read("src", "NutManager.App", "Views", "NutConfigurationAdministrationView.axaml");
        var shell = Read("src", "NutManager.App", "MainWindow.axaml");

        Assert.Contains("Classes.warning=\"{Binding IsConnectionPending}\"", overview, StringComparison.Ordinal);
        Assert.Contains("Classes.critical=\"{Binding IsConnectionCritical}\"", overview, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding !IsPrimaryStatusUnknown}\"", overview, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsUnknown}\"", overview, StringComparison.Ordinal);
        // The right gutter T37 established is still 18. The vertical values arrived with the
        // underlap: the inset that keeps content clear of the title bar and the footer lives on the
        // scroll content, not on the scroll viewer, because padding there consumes viewport and cost
        // the page a third of its scroll range.
        Assert.Contains("Spacing=\"16\" Margin=\"0,92,18,58\"", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("Strings[Administration.Configuration.NoFiles]", administration, StringComparison.Ordinal);
        Assert.Contains("Title=\"NUT Manager\"", shell, StringComparison.Ordinal);
    }

    private static string Read(params string[] segments) => Repository.Read(Path.Combine(segments));
}
