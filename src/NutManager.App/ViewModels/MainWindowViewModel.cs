using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using NutManager.App.Localization;
using NutManager.App.Services;
using NutManager.Core.Agent;
using NutManager.Core.Models;
using NutManager.Infrastructure.Platform.Windows;

namespace NutManager.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<AppPage, PageViewModel> _pages;
    private readonly OverviewPageViewModel _overviewPage;
    private readonly SettingsPageViewModel _settingsPage;
    private readonly string? _activeEndpoint;
    private string? _activeProfileName;
    private readonly NutManagementMode? _managementMode;
    private ManagedNutServerAccessMode? _accessMode;
    private readonly string? _preferredUpsName;
    private ManagedNutServerProfile? _activeProfile;
    private readonly RemoteWindowsServiceViewModel? _remoteWindowsService;
    private ManagedNutConfigurationFiles _managedConfigurationFiles = ManagedNutConfigurationFiles.Create([]);
    private bool _isOverlayOpen;
    private SemanticConfigurationReviewViewModel? _semanticReview;

    public MainWindowViewModel(ThemePreference themePreference = ThemePreference.System)
        : this(themePreference, new OverviewPageViewModel(), new DevicesPageViewModel(), new SettingsPageViewModel())
    {
    }

    public MainWindowViewModel(ThemePreference themePreference, OverviewPageViewModel overviewPage)
        : this(themePreference, overviewPage, new DevicesPageViewModel(), new SettingsPageViewModel())
    {
    }

    public MainWindowViewModel(
        ThemePreference themePreference,
        OverviewPageViewModel overviewPage,
        DevicesPageViewModel devicesPage,
        SettingsPageViewModel? settingsPage = null,
        DiagnosticsPageViewModel? diagnosticsPage = null,
        AdministrationPageViewModel? administrationPage = null,
        UiLanguagePreference language = UiLanguagePreference.PtBr,
        SidebarPreference sidebarPreference = SidebarPreference.Expanded,
        bool mockMode = false,
        string? activeEndpoint = null,
        string? activeProfileName = null,
        NutManagementMode? managementMode = null,
        ManagedNutServerAccessMode? accessMode = null,
        string? preferredUpsName = null,
        ManagedNutServerProfile? activeProfile = null,
        RemoteWindowsServiceViewModel? remoteWindowsService = null,
        AboutPageViewModel? aboutPage = null)
    {
        ArgumentNullException.ThrowIfNull(overviewPage);
        ArgumentNullException.ThrowIfNull(devicesPage);

        _overviewPage = overviewPage;
        _settingsPage = settingsPage ?? new SettingsPageViewModel();
        _activeEndpoint = string.IsNullOrWhiteSpace(activeEndpoint) ? null : activeEndpoint;
        _activeProfileName = string.IsNullOrWhiteSpace(activeProfileName) ? null : activeProfileName;
        _managementMode = managementMode;
        _accessMode = accessMode;
        _preferredUpsName = string.IsNullOrWhiteSpace(preferredUpsName) ? null : preferredUpsName;
        _activeProfile = activeProfile;
        _remoteWindowsService = remoteWindowsService;
        _managedConfigurationFiles = activeProfile?.Management.ManagedFiles ?? ManagedNutConfigurationFiles.Create([]);
        _language = language;
        _sidebarPreference = sidebarPreference;
        Localizer = new NutManagerLocalizer(language);
        _pages = new Dictionary<AppPage, PageViewModel>
        {
            [AppPage.Overview] = overviewPage,
            [AppPage.Devices] = devicesPage,
            [AppPage.Administration] = administrationPage ?? new AdministrationPageViewModel(),
            [AppPage.Diagnostics] = diagnosticsPage ?? new DiagnosticsPageViewModel(),
            [AppPage.Settings] = _settingsPage,
            [AppPage.About] = aboutPage ?? new AboutPageViewModel(
                ApplicationRuntimeInfo.CreateCurrent(),
                new WindowsExternalResourceLauncher(),
                language)
        };

        NavigationItems = new List<NavigationItemViewModel>
        {
            CreateNavigationItem(AppPage.Overview, "Nav.Overview"),
            CreateNavigationItem(AppPage.Devices, "Nav.Devices"),
            CreateNavigationItem(AppPage.Administration, "Nav.Administration"),
            CreateNavigationItem(AppPage.Diagnostics, "Nav.Diagnostics"),
            CreateNavigationItem(AppPage.Settings, "Nav.Settings"),
            CreateNavigationItem(AppPage.About, "Nav.About")
        };
        ThemeOptions =
        [
            new ThemeOption(ThemePreference.System, Localizer.Get("Theme.System")),
            new ThemeOption(ThemePreference.Light, Localizer.Get("Theme.Light")),
            new ThemeOption(ThemePreference.Dark, Localizer.Get("Theme.Dark"))
        ];
        _selectedThemeOption = ThemeOptions.Single(option => option.Preference == themePreference);
        _selectedPage = AppPage.Overview;
        _currentPage = _pages[AppPage.Overview];
        _overviewPage.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(OverviewPageViewModel.ConnectionState) or nameof(OverviewPageViewModel.DataFreshness) or nameof(OverviewPageViewModel.Snapshot))
            {
                OnPropertyChanged(nameof(ConnectionPresentation));
                OnPropertyChanged(nameof(ConnectionStatusText));
                OnPropertyChanged(nameof(FooterServerStatusText));
                OnPropertyChanged(nameof(FooterServerAccessibleText));
                OnPropertyChanged(nameof(ConnectionTooltip));
                OnPropertyChanged(nameof(ConnectionSummaryText));
                OnPropertyChanged(nameof(ActiveUpsName));
                OnPropertyChanged(nameof(ConnectionDetailText));
                OnPropertyChanged(nameof(IsConnectionHealthy));
                OnPropertyChanged(nameof(IsConnectionPending));
                OnPropertyChanged(nameof(IsConnectionCritical));
                OnPropertyChanged(nameof(IsConnectionUnavailable));
            }
        };
        if (_remoteWindowsService is not null)
        {
            _remoteWindowsService.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RemoteWindowsServiceViewModel.Observation))
                {
                    PublishDashboardContext();
                }
            };
        }
        UpdateNavigationSelection();
        PublishDashboardContext();
    }

    /// <summary>
    /// Carries a saved rename into the shell's own copy of the active profile.
    ///
    /// The name was captured once at startup and held in a readonly field, so renaming the server the
    /// session was already using left the sidebar, the footer and the Overview all announcing a name
    /// the operator had just replaced. The file on disk was correct; only the running window was
    /// wrong, which is the version the operator can see.
    ///
    /// Identity and nothing else. The new profile's name is taken; its monitoring endpoint, transport,
    /// management block and access mode are deliberately *not* — the copy is rebuilt from the fields
    /// this shell already holds. Monitoring host, port, preferred UPS, configuration transport and
    /// credentials still require a restart, and a rename must not become the doorway through which
    /// they change underneath a live session.
    ///
    /// Only for this session's own profile: renaming some other saved server leaves the running one
    /// alone, which is why the identifier is compared before anything moves.
    /// </summary>
    public void ApplyActiveProfileIdentity(ManagedNutServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (_activeProfile is not { } active || active.Id != profile.Id) return;
        if (string.Equals(active.Name, profile.Name, StringComparison.Ordinal)) return;

        _activeProfile = new ManagedNutServerProfile(
            active.Id, profile.Name, active.Monitoring, active.Management, active.AccessMode);
        _activeProfileName = string.IsNullOrWhiteSpace(profile.Name) ? null : profile.Name;

        // Everything that spells the server's name out loud. The footer keeps two readings - the
        // short one and the accessible one - and updating only the visible half would leave a screen
        // reader announcing the old name.
        OnPropertyChanged(nameof(ActiveProfileName));
        OnPropertyChanged(nameof(FooterServerStatusText));
        OnPropertyChanged(nameof(FooterServerAccessibleText));

        PublishDashboardContext();
    }

    /// <summary>
    /// Carries a saved access mode into the shell's own copy of the active profile.
    ///
    /// The Overview reads access from here, and holding the value from startup meant the card kept
    /// saying Manage after the profile had been saved as read-only — a claim about authorization,
    /// which is the worst kind to leave stale.
    /// </summary>
    public void ApplyAccessMode(ManagedNutServerAccessMode accessMode)
    {
        if (_activeProfile is not { } profile || profile.AccessMode == accessMode) return;

        _activeProfile = new ManagedNutServerProfile(
            profile.Id, profile.Name, profile.Monitoring, profile.Management, accessMode);

        // The sidebar card reads a separate field rather than the profile, so both have to move or the
        // card goes on saying Manage next to a header that says read-only.
        _accessMode = accessMode;
        OnPropertyChanged(nameof(ActiveProfileModeText));

        PublishDashboardContext();
    }

    /// <summary>
    /// Hands the Overview dashboard the management context the shell already owns, plus shortcuts
    /// that only navigate to existing surfaces. No new state, capability or command is created.
    /// </summary>
    private void PublishDashboardContext()
    {
        var activeManagementMode = _activeProfile?.Management.Mode ?? _managementMode;
        var activeAccessMode = _activeProfile?.AccessMode ?? _accessMode;
        var activePreferredUps = _activeProfile?.Monitoring.PreferredUpsName ?? _preferredUpsName;
        var profileRows = new List<OverviewInfoRowViewModel>
        {
            new(Localizer.Get("Overview.Profile"), _activeProfile?.Name ?? ActiveProfileName)
        };
        if (activeManagementMode is { } mode)
            profileRows.Add(new(Localizer.Get("Overview.Management"),
                Localizer.Get(mode == NutManagementMode.Local ? "Management.Local" : "Management.Remote")));
        if (activeAccessMode is { } access)
            profileRows.Add(new(Localizer.Get("Overview.Access"),
                Localizer.Get(access == ManagedNutServerAccessMode.Manage ? "Access.Manage" : "Access.ReadOnly")));
        profileRows.Add(new(Localizer.Get("Overview.PreferredUps"),
            activePreferredUps ?? Localizer.Get("Status.Unavailable"),
            activePreferredUps is not null));

        var configurationTransport = _activeProfile?.Management.Mode switch
        {
            NutManagementMode.Local => Localizer.Get("Management.Local"),
            NutManagementMode.Remote when _activeProfile.Management.ConfigurationTransport == RemoteConfigurationTransportKind.Smb => Localizer.Get("Transport.Smb"),
            NutManagementMode.Remote => Localizer.Get("Transport.Sftp"),
            _ => Localizer.Get("Status.Unavailable")
        };
        var agentTransport = _activeProfile?.Management.Mode switch
        {
            NutManagementMode.Remote when _activeProfile.Management.Agent.Transport == NutAgentTransportKind.Https => Localizer.Get("Settings.Agent.Transport.Https"),
            NutManagementMode.Remote => Localizer.Get("RemoteService.Transport.NamedPipe"),
            _ => Localizer.Get("Status.Unavailable")
        };
        var isAgentConnected = _remoteWindowsService?.IsAgentReachable == true;
        var connectivityRows = new List<OverviewInfoRowViewModel>
        {
            new(Localizer.Get("Overview.ConfigurationVia"), configurationTransport,
                configurationTransport != Localizer.Get("Status.Unavailable")),
            new(Localizer.Get("Overview.ControlVia"), agentTransport,
                agentTransport != Localizer.Get("Status.Unavailable")),
            new(Localizer.Get("Overview.ManagedFiles"), ManagedFilesText),
            new(Localizer.Get("Overview.Agent"),
                Localizer.Get(isAgentConnected ? "RemoteService.Agent.Connected" : "Status.Disconnected"),
                false,
                isAgentConnected ? OverviewInfoRowStatus.Healthy : OverviewInfoRowStatus.Critical)
        };
        var administrationPage = _pages[AppPage.Administration] as AdministrationPageViewModel;
        var shortcuts = new List<OverviewShortcutViewModel>
        {
            CreateShortcut(AdministrationSection.NutConfiguration, OverviewShortcutGlyph.Configuration,
                "Administration.Section.Configuration", administrationPage),
            CreateShortcut(AdministrationSection.WindowsService, OverviewShortcutGlyph.Service,
                "Administration.Section.WindowsService", administrationPage),
            CreateShortcut(AdministrationSection.DevicesAndDrivers, OverviewShortcutGlyph.Devices,
                "Administration.Section.DevicesDrivers", administrationPage),
            new(Localizer.Get("Nav.Diagnostics"), Localizer.Get("Diagnostics.Description"),
                OverviewShortcutGlyph.Diagnostics, new RelayCommand(() => Navigate(AppPage.Diagnostics)))
        };

        _overviewPage.SetDashboardContext(profileRows, connectivityRows, shortcuts);
    }

    private string ManagedFilesText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        Localizer.Get(_managedConfigurationFiles.Count == 1 ? "Overview.ManagedFiles.One" : "Overview.ManagedFiles.Many"),
        _managedConfigurationFiles.Count);

    /// <summary>Applies the confirmed managed-file selection to the active runtime profile summary.</summary>
    public void UpdateManagedConfigurationFiles(ManagedNutConfigurationFiles managedFiles)
    {
        ArgumentNullException.ThrowIfNull(managedFiles);
        _managedConfigurationFiles = managedFiles;
        PublishDashboardContext();
    }

    private OverviewShortcutViewModel CreateShortcut(
        AdministrationSection section,
        OverviewShortcutGlyph glyph,
        string resourcePrefix,
        AdministrationPageViewModel? administrationPage) =>
        new(Localizer.Get(resourcePrefix),
            Localizer.Get($"{resourcePrefix}.Description"),
            glyph,
            new RelayCommand(() =>
            {
                if (administrationPage?.AdministrationSections.FirstOrDefault(item => item.Section == section) is { } target)
                    administrationPage.SelectedAdministrationSection = target;
                Navigate(AppPage.Administration);
            }));

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }
    public IReadOnlyList<ThemeOption> ThemeOptions { get; }
    public IReadOnlyList<ManagedProfileCardViewModel> ManagedProfileCards => _settingsPage.ManagedProfileCards;
    public NutManagerLocalizer Localizer { get; private set; }

    [ObservableProperty] private AppPage _selectedPage;
    [ObservableProperty] private PageViewModel _currentPage;
    [ObservableProperty] private ThemeOption? _selectedThemeOption;
    [ObservableProperty] private UiLanguagePreference _language;
    [ObservableProperty] private SidebarPreference _sidebarPreference;

    /// <summary>
    /// Whether the window paints its acrylic backdrop or a solid one. The window binds both the
    /// acrylic pane and the opaque panel behind the shell to this, so switching it swaps which of
    /// the two is drawn rather than dimming the effect towards invisibility.
    /// </summary>
    [ObservableProperty] private bool _isBackgroundTransparent = true;
    [ObservableProperty] private ShellLayoutState _shellLayout = ShellLayoutState.Wide;
    // Dark is the product's default, so starting here keeps the window from drawing one opaque
    // frame before the shell reports which variant actually resolved.
    [ObservableProperty] private bool _isEffectiveDark = true;

    /// <summary>
    /// What the user chose, kept apart from what the window draws. Acrylic only reads as glass under
    /// the dark palette; over the near-white light one the same backdrop washes the page out instead
    /// of revealing anything. So light theme forces the opaque backdrop while this preference waits,
    /// and going back to dark restores the choice rather than resetting it.
    /// </summary>
    private bool _transparencyPreference = true;

    public event Action<bool>? EffectiveThemeChanged;

    public void SetTransparencyPreference(bool value)
    {
        _transparencyPreference = value;
        IsBackgroundTransparent = value && IsEffectiveDark;
    }

    public ThemePreference SelectedTheme => SelectedThemeOption?.Preference ?? ThemePreference.System;
    public SidebarDisplayState SidebarDisplay => ShellPresentationMapper.SidebarFor(ShellLayout, SidebarPreference);
    public ReviewDrawerDisplayState ReviewDrawerDisplay => ShellPresentationMapper.ReviewFor(ShellLayout, _semanticReview?.HasChanges == true, true);
    public bool IsSidebarExpanded => SidebarDisplay == SidebarDisplayState.Expanded;
    public bool IsSidebarCollapsed => SidebarDisplay == SidebarDisplayState.Collapsed;
    public bool IsSidebarOverlay => SidebarDisplay == SidebarDisplayState.Overlay;
    public bool IsWideLayout => ShellLayout == ShellLayoutState.Wide;
    public bool IsCompactLayout => ShellLayout == ShellLayoutState.Compact;
    public bool IsFooterAuthorshipVisible => !IsCompactLayout;
    public bool IsOverlayOpen => IsSidebarOverlay && _isOverlayOpen;
    public double NavigationOverlayOpacity => IsOverlayOpen ? 1d : 0d;
    public Thickness NavigationOverlayMargin => IsOverlayOpen ? new Thickness(0) : new Thickness(-24, 0, 24, 0);
    public bool IsBackgroundInteractionEnabled => !IsOverlayOpen && !IsReviewDrawerOverlay;
    public bool IsNavigationToggleVisible => ShellLayout != ShellLayoutState.Medium;
    public double SidebarWidth => IsSidebarExpanded ? 220 : IsSidebarCollapsed ? 72 : 0;
    /// <summary>
    /// The content area's horizontal breathing room only.
    ///
    /// The vertical inset deliberately lives inside the page's own scroll viewer instead of here.
    /// This padding sits inside the border that reaches under the title bar and the footer, so a
    /// vertical value here would push the scroll viewport straight back down and cancel that reach —
    /// the content would never travel under the bars, which is the whole point of the underlap.
    /// </summary>
    public Thickness ContentPadding => ShellLayout switch
    {
        ShellLayoutState.Wide => new Thickness(28, 0, 28, 0),
        ShellLayoutState.Medium => new Thickness(20, 0, 20, 0),
        _ => new Thickness(14, 0, 14, 0)
    };
    public bool IsMockMode => false;
    public string? ActiveUpsName => _overviewPage.Snapshot?.Identity.Name ?? _preferredUpsName;
    public string ConnectionDetailText => _activeEndpoint is null
        ? Localizer.Get("Shell.NoActiveProfile")
        : ActiveUpsName is null ? _activeEndpoint : $"{ActiveUpsName}@{_activeEndpoint}";
    public string ActiveProfileName => _activeProfileName ?? Localizer.Get("Shell.NoActiveProfile");
    public string ActiveProfileModeText => _managementMode switch
    {
        NutManagementMode.Local => Localizer.Get("Management.Local"),
        NutManagementMode.Remote => Localizer.Get("Management.Remote"),
        _ => Localizer.Get("Status.Unavailable")
    } + " · " + (_accessMode switch
    {
        ManagedNutServerAccessMode.ReadOnly => Localizer.Get("Access.ReadOnly"),
        ManagedNutServerAccessMode.Manage => Localizer.Get("Access.Manage"),
        _ => Localizer.Get("Status.Unavailable")
    });
    public string ApplicationVersionText => $"v{typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";
    public string AdministrationConfirmationText => Localizer.Get("Shell.AdministrationConfirmation");
    public string FooterAuthorshipText => Localizer.Get("Shell.Authorship");
    public string OpenProfilesText => Localizer.Get("Shell.OpenProfiles");
    public string SavedProfilesText => Localizer.Get("Shell.SavedProfiles");
    public string ManageProfilesText => Localizer.Get("Shell.ManageProfiles");
    public ConnectionPresentationState ConnectionPresentation => ShellPresentationMapper.ConnectionFor(
        _overviewPage.ConnectionState,
        _overviewPage.DataFreshness,
        _activeEndpoint is not null || _overviewPage.Snapshot is not null);
    public string ConnectionStatusText => ConnectionPresentation switch
    {
        ConnectionPresentationState.Healthy => Localizer.Get("Status.Connected"),
        ConnectionPresentationState.Pending => _overviewPage.ConnectionState == ConnectionState.Reconnecting ? Localizer.Get("Status.Reconnecting") : Localizer.Get("Status.Connecting"),
        ConnectionPresentationState.Warning => Localizer.Get("Status.Stale"),
        ConnectionPresentationState.Critical => _overviewPage.ConnectionState == ConnectionState.ConnectionFailed ? Localizer.Get("Status.ConnectionFailed") : Localizer.Get("Status.Disconnected"),
        _ => Localizer.Get("Status.Unavailable")
    };
    public string ConnectionTooltip => ConnectionSummaryText;
    public string ConnectionSummaryText => $"{ConnectionStatusText} · {ConnectionDetailText}";

    /// <summary>
    /// The footer's reading of the active server: "GANDALF · Online".
    ///
    /// This is the **NUT TCP** connection and nothing else. The agent has its own state and its own
    /// surfaces, and folding the two together here would let a stopped agent make a perfectly healthy
    /// monitoring session look offline — or the reverse, which is worse.
    ///
    /// Three readings rather than two. Online and Offline are the ends, and the amber state in between
    /// says Reconnecting — because "offline" while the client is actively retrying is wrong in the way
    /// that matters: it tells an operator to go and investigate something that is already fixing
    /// itself. A transient state named as a failure costs somebody a trip to the server room.
    ///
    /// Failed and unavailable both read as Offline. From the footer's distance the useful question is
    /// whether readings are arriving, and the distinction between "tried and failed" and "never tried"
    /// is not thrown away: the dot keeps its four colours and <see cref="ConnectionStatusText"/> still
    /// names the exact state for the accessible label and the tooltip.
    ///
    /// The profile's own name is used rather than its host, because that is what the operator called
    /// this server. A profile with no name falls back to whatever ActiveProfileName resolves to.
    /// </summary>
    public string FooterServerStatusText
    {
        get
        {
            var state =
                IsConnectionHealthy ? Localizer.Get("Shell.ServerOnline") :
                IsConnectionPending ? Localizer.Get("Status.Reconnecting") :
                Localizer.Get("Shell.ServerOffline");

            return $"{ActiveProfileName} · {state}";
        }
    }

    /// <summary>Names the precise state for assistive technology, not just Online or Offline.</summary>
    public string FooterServerAccessibleText => $"{ActiveProfileName} · {ConnectionStatusText}";
    public string ApplicationName => Localizer.Get("App.Name");
    public bool IsConnectionHealthy => ConnectionPresentation == ConnectionPresentationState.Healthy;
    public bool IsConnectionPending => ConnectionPresentation is ConnectionPresentationState.Pending or ConnectionPresentationState.Warning;
    public bool IsConnectionCritical => ConnectionPresentation == ConnectionPresentationState.Critical;
    public bool IsConnectionUnavailable => ConnectionPresentation == ConnectionPresentationState.Unavailable;
    public bool ShowLightThemeAction => SelectedTheme == ThemePreference.Dark || (SelectedTheme == ThemePreference.System && IsEffectiveDark);
    public bool ShowDarkThemeAction => !ShowLightThemeAction;
    public string NavigationToggleName => IsSidebarExpanded || IsOverlayOpen
        ? Localizer.Get("Shell.CollapseNavigation")
        : Localizer.Get("Shell.ExpandNavigation");
    public string SimulationText => Localizer.Get("Shell.SimulationActive");
    public string ReviewDrawerTitle => Localizer.Get("Shell.ReviewChanges");
    public string ReviewDrawerCloseText => Localizer.Get("Shell.CloseReview");
    public string? ReviewDrawerPendingText => _semanticReview?.PendingText;
    public string? ReviewDrawerPendingCount => _semanticReview?.ChangeCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public object? ReviewDrawerContent => _semanticReview;
    public bool IsReviewDrawerInline => ReviewDrawerDisplay == ReviewDrawerDisplayState.Expanded;
    public bool IsReviewDrawerOverlay => ReviewDrawerDisplay == ReviewDrawerDisplayState.Overlay;
    public bool IsReviewDrawerVisible => ReviewDrawerDisplay != ReviewDrawerDisplayState.Hidden;

    public event Action<ThemePreference>? ThemeChanged;
    public event Action<SidebarPreference>? SidebarPreferenceChanged;

    public void SetTheme(ThemePreference preference)
    {
        var option = ThemeOptions.Single(option => option.Preference == preference);
        if (!Equals(SelectedThemeOption, option)) SelectedThemeOption = option;
    }

    public void UpdateLayoutWidth(double width) => ShellLayout = ShellPresentationMapper.LayoutFor(width);

    public void UpdateEffectiveTheme(bool isDark) => IsEffectiveDark = isDark;

    public void SetSemanticReview(SemanticConfigurationReviewViewModel? review)
    {
        _semanticReview = review;
        OnPropertyChanged(nameof(ReviewDrawerDisplay));
        OnPropertyChanged(nameof(IsReviewDrawerVisible));
        OnPropertyChanged(nameof(ReviewDrawerPendingText));
        OnPropertyChanged(nameof(ReviewDrawerPendingCount));
        OnPropertyChanged(nameof(ReviewDrawerContent));
        OnPropertyChanged(nameof(IsReviewDrawerInline));
        OnPropertyChanged(nameof(IsReviewDrawerOverlay));
        OnPropertyChanged(nameof(IsBackgroundInteractionEnabled));
    }

    [RelayCommand]
    private void CloseReviewDrawer() => SetSemanticReview(null);

    [RelayCommand]
    private void Navigate(AppPage page)
    {
        // Told before it is replaced, so a page can drop the state that belonged to the visit. Also
        // fires when navigating to the page already shown, which is harmless: there is nothing to
        // discard that the user has not just seen.
        CurrentPage?.OnDeactivated();

        SelectedPage = page;
        CurrentPage = _pages[page];
        UpdateNavigationSelection();
        if (IsSidebarOverlay)
        {
            CloseNavigationOverlay();
        }
    }

    [RelayCommand]
    private void OpenManagedProfile(ManagedProfileCardViewModel? profile)
    {
        if (profile is not null)
        {
            // Selection remains owned by Settings. Its setter preserves a dirty draft by opening
            // the existing decision flow instead of silently replacing it.
            _settingsPage.SelectedProfileCard = profile;
        }

        Navigate(AppPage.Settings);
    }

    [RelayCommand]
    private void ToggleNavigation()
    {
        if (ShellLayout == ShellLayoutState.Medium) return;

        if (IsSidebarOverlay)
        {
            _isOverlayOpen = !_isOverlayOpen;
            OnPropertyChanged(nameof(IsOverlayOpen));
            OnPropertyChanged(nameof(NavigationOverlayOpacity));
            OnPropertyChanged(nameof(NavigationOverlayMargin));
            OnPropertyChanged(nameof(IsBackgroundInteractionEnabled));
            OnPropertyChanged(nameof(NavigationToggleName));
            return;
        }

        SidebarPreference = SidebarPreference == SidebarPreference.Expanded ? SidebarPreference.Collapsed : SidebarPreference.Expanded;
    }

    [RelayCommand]
    private void ToggleTheme(bool effectiveDark) => SetTheme(SelectedTheme switch
    {
        ThemePreference.Light => ThemePreference.Dark,
        ThemePreference.Dark => ThemePreference.Light,
        _ => effectiveDark ? ThemePreference.Light : ThemePreference.Dark
    });

    partial void OnSelectedThemeOptionChanged(ThemeOption? value)
    {
        if (value is not null)
        {
            OnPropertyChanged(nameof(SelectedTheme));
            OnPropertyChanged(nameof(ShowLightThemeAction));
            OnPropertyChanged(nameof(ShowDarkThemeAction));
            ThemeChanged?.Invoke(value.Preference);
        }
    }

    partial void OnIsEffectiveDarkChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLightThemeAction));
        OnPropertyChanged(nameof(ShowDarkThemeAction));
        IsBackgroundTransparent = _transparencyPreference && value;
        EffectiveThemeChanged?.Invoke(value);
    }

    partial void OnSidebarPreferenceChanged(SidebarPreference value)
    {
        NotifyShellProperties();
        SidebarPreferenceChanged?.Invoke(value);
    }

    partial void OnShellLayoutChanged(ShellLayoutState value)
    {
        _isOverlayOpen = false;
        NotifyShellProperties();
    }

    private NavigationItemViewModel CreateNavigationItem(AppPage page, string resourceKey) =>
        new(page, Localizer.Get(resourceKey), new RelayCommand(() => Navigate(page)));

    private void UpdateNavigationSelection()
    {
        foreach (var item in NavigationItems) item.IsSelected = item.Page == SelectedPage;
    }

    private void NotifyShellProperties()
    {
        OnPropertyChanged(nameof(SidebarDisplay));
        OnPropertyChanged(nameof(ReviewDrawerDisplay));
        OnPropertyChanged(nameof(IsSidebarExpanded));
        OnPropertyChanged(nameof(IsSidebarCollapsed));
        OnPropertyChanged(nameof(IsSidebarOverlay));
        OnPropertyChanged(nameof(IsWideLayout));
        OnPropertyChanged(nameof(IsCompactLayout));
        OnPropertyChanged(nameof(IsFooterAuthorshipVisible));
        OnPropertyChanged(nameof(IsOverlayOpen));
        OnPropertyChanged(nameof(NavigationOverlayOpacity));
        OnPropertyChanged(nameof(NavigationOverlayMargin));
        OnPropertyChanged(nameof(IsBackgroundInteractionEnabled));
        OnPropertyChanged(nameof(IsNavigationToggleVisible));
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(ContentPadding));
        OnPropertyChanged(nameof(NavigationToggleName));
        OnPropertyChanged(nameof(IsReviewDrawerVisible));
        OnPropertyChanged(nameof(IsReviewDrawerInline));
        OnPropertyChanged(nameof(IsReviewDrawerOverlay));
    }

    public void CloseNavigationOverlay()
    {
        if (!_isOverlayOpen) return;
        _isOverlayOpen = false;
        OnPropertyChanged(nameof(IsOverlayOpen));
        OnPropertyChanged(nameof(NavigationOverlayOpacity));
        OnPropertyChanged(nameof(NavigationOverlayMargin));
        OnPropertyChanged(nameof(IsBackgroundInteractionEnabled));
        OnPropertyChanged(nameof(NavigationToggleName));
    }
}
