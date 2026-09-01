using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NutManager.Agent.Config.Localization;
using NutManager.Core.Agent;
using NutManager.Core.Models;

namespace NutManager.Agent.Config.ViewModels;

/// <summary>
/// The whole utility, as one screen.
///
/// It holds no Windows API of its own: every machine fact arrives through one of the six interfaces
/// below, which is what lets a domain controller, an expired certificate, a foreign firewall rule and
/// a running service all be exercised in tests on a machine that has none of them.
///
/// Three product rules are enforced here rather than trusted to the view:
///
///   - At least one transport stays enabled. The last one cannot be unchecked, and the checkbox that
///     would do it is disabled rather than the Apply being refused afterwards.
///   - Saving configuration never starts or restarts the service. A running agent is offered a
///     restart; a stopped agent stays stopped.
///   - Nothing is removed from the machine unless it is provably NutManager's, and the certificate is
///     never removed at all.
/// </summary>
public sealed partial class AgentConfigViewModel : ObservableObject
{
    private const int DefaultHttpsPort = 5199;

    private readonly IAgentConfigurationStore _store;
    private readonly IAgentOperatorsGroupAdministration _groups;
    private readonly IAgentServiceAdministration _service;
    private readonly IAgentHttpsResourceAdministration _resources;
    private readonly IAgentCertificateCatalog _certificates;
    private readonly IAgentRuntimeInventory _inventory;

    /// <summary>
    /// Absent in the tests that have nothing to say about the listener, which then reports that it has
    /// not been checked rather than reporting a state nobody observed.
    /// </summary>
    private readonly IAgentHttpsListenerProbe? _listenerProbe;

    /// <summary>Absent in tests and on any host that has no browser to hand a link to.</summary>
    private readonly IAgentProjectPageLauncher? _projectPage;
    private readonly IAgentCertificateImporter? _certificateImporter;
    private readonly IAgentConfigUiPreferences _preferences;
    private readonly TimeProvider _time;

    /// <summary>The last saved document. Cancel returns to this; dirty is measured against it.</summary>
    private AgentTransportConfigurationDocument _confirmed = new();

    private AgentOperatorsGroupState _groupState =
        AgentOperatorsGroupState.Missing("NutManager Operators", AgentMachineRole.Unknown);

    private AgentServiceSnapshot _serviceState = AgentServiceSnapshot.NotInstalled();
    private AgentRuntimeInventorySnapshot _machine = new(null, null, false, false, null);
    private AgentHttpsResourceSnapshot _resourceState = AgentHttpsResourceSnapshot.None;

    /// <summary>
    /// Guards the transport setters against the revert they perform on themselves. Without it, pushing
    /// a refused value back would raise a second change notification and recurse.
    /// </summary>
    private bool _suppressTransportGuard;

    /// <summary>The endpoint whose resources were last described, so cleanup targets what exists.</summary>
    private AgentHttpsBinding? _appliedBinding;

    /// <summary>
    /// Whether the resource snapshot came from actually asking Windows.
    ///
    /// It does not, while the draft is incomplete: there is no endpoint to query for. Without this
    /// flag the status strip reported every resource as Absent in that case, which reads as "we
    /// looked and there is nothing there" - and that is how a screen came to say the SSL binding was
    /// not configured while Apply, which does query, found another application already bound to the
    /// port. Never-queried and queried-and-absent are different facts and now say different things.
    /// </summary>
    private bool _resourceStateWasQueried;

    public AgentConfigViewModel(
        IAgentConfigurationStore store,
        IAgentOperatorsGroupAdministration groups,
        IAgentServiceAdministration service,
        IAgentHttpsResourceAdministration resources,
        IAgentCertificateCatalog certificates,
        IAgentRuntimeInventory inventory,
        UiLanguagePreference? language = null,
        TimeProvider? timeProvider = null,
        IAgentCertificateImporter? certificateImporter = null,
        IAgentConfigUiPreferences? preferences = null,
        IAgentProjectPageLauncher? projectPage = null,
        IAgentHttpsListenerProbe? listenerProbe = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(inventory);

        _store = store;
        _groups = groups;
        _service = service;
        _resources = resources;
        _certificates = certificates;
        _inventory = inventory;
        _certificateImporter = certificateImporter;
        _projectPage = projectPage;
        _listenerProbe = listenerProbe;
        _preferences = preferences ?? AgentConfigUiPreferences.None;
        _time = timeProvider ?? TimeProvider.System;

        // An explicit argument wins, then a saved preference, then the culture Windows is running in.
        // The saved value is read once here rather than watched: this window is open for minutes, and
        // a preference that changed underneath it would be somebody else's session, not this one.
        _selectedLanguage = language ?? _preferences.ReadLanguage() ?? AgentConfigStrings.DetectLanguage();

        // No saved theme means nobody has chosen one, so the window follows Windows - the same answer
        // the desktop application gives, where ThemePreference.System maps to ThemeVariant.Default.
        // A third stored state here would be a preference nobody set masquerading as one they did.
        _selectedTheme = _preferences.ReadTheme() ?? ThemePreference.System;
        _strings = new AgentConfigStrings(_selectedLanguage);

        LanguageOptions =
        [
            new AgentLanguageOption(UiLanguagePreference.PtBr, _strings["Language.Portuguese"]),
            new AgentLanguageOption(UiLanguagePreference.EnUs, _strings["Language.English"]),
        ];

        // The field, not the property: assigning through the setter here would be the control
        // reporting a selection nobody made, which is the write this guard exists to prevent.
        _selectedLanguageOption = LanguageOptions.First(option => option.Value == _selectedLanguage);
    }

    // ---------------------------------------------------------------- language

    /// <summary>
    /// The strings the whole window binds through.
    ///
    /// Replaced wholesale when the language changes rather than mutated, so every
    /// <c>{Binding Strings[Key]}</c> in the view re-evaluates from one notification. The alternative —
    /// an indexer that raises its own change events — would have every one of those bindings depend on
    /// a notification shape that is easy to get subtly wrong and hard to see when it is.
    /// </summary>
    [ObservableProperty]
    private AgentConfigStrings _strings;

    [ObservableProperty]
    private UiLanguagePreference _selectedLanguage;

    /// <summary>
    /// The two languages this product ships, named in themselves.
    ///
    /// Built once. Both cultures give these entries the same text - a language is called what it
    /// calls itself - so there is nothing here for a language change to rewrite.
    /// </summary>
    public IReadOnlyList<AgentLanguageOption> LanguageOptions { get; }

    /// <summary>
    /// The entry the selector shows.
    ///
    /// Two-way, and guarded. A list control assigns its selection as it materialises, and that
    /// assignment once reached the preference store and overwrote a saved choice with whatever
    /// happened to be first. The guard is what makes the control safe to bind: the initial value is
    /// put in place without going through the setter, and an echo of the current value is not a
    /// change.
    /// </summary>
    [ObservableProperty]
    private AgentLanguageOption? _selectedLanguageOption;

    /// <summary>Set while the selector is being brought in line with the preference.</summary>
    private bool _syncingLanguage;

    partial void OnSelectedLanguageOptionChanged(AgentLanguageOption? value)
    {
        if (_syncingLanguage || value is null) return;

        SelectedLanguage = value.Value;
    }

    /// <summary>
    /// The selector's two states, as booleans rather than as a selected object.
    ///
    /// This was a ComboBox bound to a list of option objects, and it overwrote the language at
    /// start-up: the control resolved its selection against an items source that was not ready yet,
    /// fell back to the first entry and wrote that back through the two-way binding - which made the
    /// saved preference useless and, worse, silently re-saved the wrong one over it.
    ///
    /// A pair of booleans has no such window. Nothing checks itself, and the setters ignore the
    /// <c>false</c> the group writes to whichever option is being unchecked, so the only thing that
    /// can change the language is somebody choosing one.
    /// </summary>
    public bool IsPortugueseSelected
    {
        get => SelectedLanguage == UiLanguagePreference.PtBr;
        set
        {
            if (value) SelectedLanguage = UiLanguagePreference.PtBr;
        }
    }

    public bool IsEnglishSelected
    {
        get => SelectedLanguage == UiLanguagePreference.EnUs;
        set
        {
            if (value) SelectedLanguage = UiLanguagePreference.EnUs;
        }
    }

    partial void OnSelectedLanguageChanged(UiLanguagePreference value)
    {
        Strings = new AgentConfigStrings(value);

        // The selector follows the preference when the change came from somewhere else, and echoing
        // it back is not a second change.
        _syncingLanguage = true;
        try
        {
            SelectedLanguageOption = LanguageOptions.FirstOrDefault(option => option.Value == value);
        }
        finally
        {
            _syncingLanguage = false;
        }

        _preferences.WriteLanguage(value);

        // The terms are a document, not a string table: switching language means reading the other
        // file. Cleared rather than re-read here, so a session that never opens the page never pays
        // for it - and one that is looking at it gets the other language on the next open.
        TermsBlocks.Clear();
        if (ShowTerms) LoadTerms();

        RelocalizeSurface();
    }

    // ---------------------------------------------------------------- transient copy feedback

    /// <summary>
    /// Cancels the toast currently counting down, so a second copy restarts the wait instead of being
    /// hidden early by the first one's timer.
    /// </summary>
    private CancellationTokenSource? _toastLifetime;

    /// <summary>Long enough to read three words, short enough not to sit there.</summary>
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(1.8);

    /// <summary>
    /// A short-lived confirmation, shown over the window and never in its layout.
    ///
    /// Deliberately its own state rather than a reuse of the Apply banner: copying a value to the
    /// clipboard is not a configuration result, and a screen that reported both through one surface
    /// would let "Copied!" overwrite the reason an Apply was refused.
    /// </summary>
    [ObservableProperty]
    private bool _isToastVisible;

    /// <summary>
    /// Keeps the anchored popup alive while its content fades out. Popup membership and visual
    /// visibility are separate because closing a Popup removes it before a transition can render.
    /// </summary>
    [ObservableProperty]
    private bool _isToastPopupOpen;

    [ObservableProperty]
    private string? _toastMessage;

    [ObservableProperty]
    private AgentToastKind _toastKind;

    /// <summary>
    /// Reports what the clipboard actually did.
    ///
    /// Success is never assumed: the view passes the result of the write, so a clipboard the platform
    /// refused says so instead of claiming a copy that did not happen.
    /// </summary>
    public void ReportEndpointCopy(bool succeeded) => ShowToast(
        succeeded ? AgentToastKind.Success : AgentToastKind.Error,
        Strings[succeeded ? "Toast.EndpointCopied" : "Toast.EndpointCopyFailed"]);

    /// <summary>
    /// One toast at a time, and the newest click owns the clock.
    ///
    /// Three quick copies produce one toast that stays for a full period after the last of them,
    /// rather than three stacked boxes or a box that vanishes while the pointer is still on the
    /// button. Cancelling the previous token is what makes the earlier timer harmless: it wakes,
    /// sees it was cancelled, and hides nothing.
    /// </summary>
    private void ShowToast(AgentToastKind kind, string message)
    {
        var previous = _toastLifetime;
        _toastLifetime = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();

        ToastKind = kind;
        ToastMessage = message;
        IsToastPopupOpen = true;
        IsToastVisible = true;

        HideToastAsync(_toastLifetime.Token);
    }

    /// <summary>
    /// The countdown. Deliberately not awaited by the caller - a copy is finished the moment the
    /// clipboard has the value, and nothing else should wait on a display timer.
    /// </summary>
    private async void HideToastAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ToastDuration, _time, cancellationToken).ConfigureAwait(true);
            IsToastVisible = false;
            await Task.Delay(TimeSpan.FromMilliseconds(180), _time, cancellationToken).ConfigureAwait(true);
            IsToastPopupOpen = false;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later copy, or the window closed. Either way the toast this timer was
            // responsible for is no longer the one on screen, so it hides nothing.
        }
    }

    /// <summary>
    /// Called when the window closes. Without it a pending delay would resume against a view model
    /// whose window is gone, and the cancellation source would never be disposed.
    /// </summary>
    public void CancelTransientFeedback()
    {
        _toastLifetime?.Cancel();
        IsToastVisible = false;
        IsToastPopupOpen = false;
        _toastLifetime?.Dispose();
        _toastLifetime = null;
    }

    // ---------------------------------------------------------------- theme

    /// <summary>
    /// Raised when the operator picks a theme, so the view can hand it to Avalonia.
    ///
    /// An event rather than this class touching Application.Current: the view model is where the two
    /// product rules live - which glyph is offered, and that a theme change is not a configuration
    /// change - and neither of those needs a reference to a UI framework to be true or to be tested.
    /// </summary>
    public event Action<ThemePreference>? ThemeChanged;

    [ObservableProperty]
    private ThemePreference _selectedTheme;

    /// <summary>
    /// Which theme is actually on screen, which is not always the one that was chosen: System
    /// resolves to whatever Windows is doing, and the window is told the answer by its own
    /// ActualThemeVariantChanged. The glyph depends on this rather than on the preference, because an
    /// operator reads what they are looking at, not what is stored.
    /// </summary>
    [ObservableProperty]
    private bool _isEffectiveDark = true;

    /// <summary>
    /// Whether the button offers to go light. The formula is the desktop application's own, kept
    /// identical on purpose: two NutManager windows must not disagree about which way the toggle
    /// points.
    /// </summary>
    public bool ShowLightThemeAction =>
        SelectedTheme == ThemePreference.Dark ||
        (SelectedTheme == ThemePreference.System && IsEffectiveDark);

    public bool ShowDarkThemeAction => !ShowLightThemeAction;

    /// <summary>
    /// The two halves of the segmented control, each naming the theme it selects.
    ///
    /// A pair of explicit choices rather than one toggle: the control shows both destinations at
    /// once, and its filled half says which one you are in. Choosing writes Light or Dark outright,
    /// which is the honest consequence of showing two positions - once somebody has chosen, this
    /// window stops following Windows.
    ///
    /// Nothing here touches the Agent: no dirty flag, no Apply, no store, no service, no HTTP.sys.
    /// </summary>
    [RelayCommand]
    private void SelectLightTheme() => SelectedTheme = ThemePreference.Light;

    [RelayCommand]
    private void SelectDarkTheme() => SelectedTheme = ThemePreference.Dark;

    /// <summary>
    /// Which theme is selected, resolving System against what is actually on screen.
    ///
    /// Reported rather than set: the two commands above are how it changes. Kept because the settings
    /// tests read it, and because it states in one place what "currently dark" means.
    /// </summary>
    public bool IsDarkThemeSelected => SelectedTheme switch
    {
        ThemePreference.Dark => true,
        ThemePreference.Light => false,
        _ => IsEffectiveDark,
    };

    /// <summary>
    /// The sentence beside the control, and the accessible name of each half.
    ///
    /// It names the action rather than the state: in dark mode it reads "Enable light mode". A label
    /// that announced the theme you are already in would leave somebody using a screen reader with no
    /// idea what the control does.
    /// </summary>
    public string ThemeActionText => ShowLightThemeAction
        ? Strings["Theme.EnableLight"]
        : Strings["Theme.EnableDark"];

    partial void OnSelectedThemeChanged(ThemePreference value)
    {
        _preferences.WriteTheme(value);
        NotifyThemeChanged();
        ThemeChanged?.Invoke(value);
    }

    partial void OnIsEffectiveDarkChanged(bool value) => NotifyThemeChanged();

    private void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(ShowLightThemeAction));
        OnPropertyChanged(nameof(ShowDarkThemeAction));
        OnPropertyChanged(nameof(ThemeActionText));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
    }

    /// <summary>
    /// Flips to the opposite of what is on screen.
    ///
    /// The System case resolves against the effective theme rather than toggling to System, so the
    /// first press does what it looks like it will do instead of appearing to do nothing on a machine
    /// whose Windows theme already matches. Same rule as the desktop application.
    ///
    /// Nothing here touches the Agent: no dirty flag, no Apply, no store, no service, no HTTP.sys.
    /// </summary>
    [RelayCommand]
    private void ToggleTheme() => SelectedTheme = SelectedTheme switch
    {
        ThemePreference.Light => ThemePreference.Dark,
        ThemePreference.Dark => ThemePreference.Light,
        _ => IsEffectiveDark ? ThemePreference.Light : ThemePreference.Dark,
    };

    /// <summary>Told by the window when Avalonia resolves the variant, including on start-up.</summary>
    public void UpdateEffectiveTheme(bool isDark) => IsEffectiveDark = isDark;

    /// <summary>
    /// Re-renders everything that captured a string when it was built.
    ///
    /// Computed properties only need a notification; the three collections hold text that was composed
    /// at construction, so they are rebuilt — from state already in memory, without re-reading the
    /// machine. Switching language is a view concern and must not go near the store, the service or
    /// the certificate catalog.
    /// </summary>
    private void RelocalizeSurface()
    {
        // Transient results — "Configuration saved.", a membership outcome, a failed import — were
        // written in the previous language and cannot be re-derived. Clearing them is honest;
        // leaving them would put two languages on screen at once.
        ClearApplyResult();
        OperatorsMessage = null;
        ServiceMessage = null;
        CertificateImportMessage = null;
        CertificateImportDetail = null;

        var selected = SelectedCertificate?.Thumbprint;
        var known = Certificates.Select(option => option.Certificate).ToArray();
        Certificates.Clear();
        foreach (var certificate in known) Certificates.Add(new AgentCertificateOption(certificate, Strings));
        if (!string.IsNullOrWhiteSpace(selected))
        {
            SelectedCertificate = Certificates.FirstOrDefault(option =>
                string.Equals(option.Thumbprint, selected, StringComparison.OrdinalIgnoreCase));
        }

        ApplyServiceText();
        RefreshHttpsValidation();
        RebuildResourceStatus();
        RebuildDiagnostics();

        // Every computed property on this object re-reads, rather than a hand-written list of the
        // ones somebody remembered. The list version shipped three strings in the previous language -
        // the two transport pills and the last-transport notice - because they were written after the
        // list was, and that is a mistake this cannot make again.
        OnPropertyChanged(string.Empty);
    }

    /// <summary>The product version shown in the fixed operational footer.</summary>
    public string ApplicationVersion
    {
        get
        {
            var version = typeof(AgentConfigViewModel).Assembly.GetName().Version;
            return version is null
                ? "v—"
                : $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    // ---------------------------------------------------------------- which surface is showing

    /// <summary>
    /// Which of the three surfaces the window is showing.
    ///
    /// One window, three views, and still no navigation rail: configuration is the window's purpose,
    /// diagnostics is a read-only report of the same machine, and settings holds the preferences and
    /// the one destructive action that do not belong beside the fields they act on. A sidebar for
    /// three destinations is the desktop shell this utility is deliberately not.
    ///
    /// One value rather than a boolean per surface, so "both showing" and "none showing" are states
    /// the type cannot hold.
    /// </summary>
    [ObservableProperty]
    private AgentConfigSurface _surface = AgentConfigSurface.Configuration;

    partial void OnSurfaceChanged(AgentConfigSurface value)
    {
        OnPropertyChanged(nameof(ShowConfiguration));
        OnPropertyChanged(nameof(ShowDiagnostics));
        OnPropertyChanged(nameof(ShowSettings));
        OnPropertyChanged(nameof(ShowTerms));
        OnPropertyChanged(nameof(ShowActionBar));
        OnPropertyChanged(nameof(ShowHomeAction));
        OnPropertyChanged(nameof(ShowSettingsAction));
        OnPropertyChanged(nameof(HeaderActionText));
        OnPropertyChanged(nameof(ViewToggleText));

        if (value == AgentConfigSurface.Settings && IsAgentTab) ReloadService();

        // A result belongs to the action that produced it and to the panel it happened on. Leaving
        // settings ends both, so the message does not greet whoever comes back later as though they
        // had just done something.
        if (value != AgentConfigSurface.Settings)
        {
            ClearStartupResult();
            ClearInstallResult();
        }

        // Back on the surface that shows the row. The monitor kept running while the operator was
        // elsewhere, so this is not a catch-up - it is the answer being fresh at the moment it becomes
        // visible again, rather than up to a second old.
        if (value == AgentConfigSurface.Configuration) RequestListenerRefresh();
    }

    public bool ShowConfiguration => Surface == AgentConfigSurface.Configuration;

    public bool ShowDiagnostics => Surface == AgentConfigSurface.Diagnostics;

    public bool ShowSettings => Surface == AgentConfigSurface.Settings;

    public bool ShowTerms => Surface == AgentConfigSurface.Terms;

    /// <summary>
    /// Whether Apply and Cancel belong on screen.
    ///
    /// They are the draft controls of the configuration surface: they save the transport document and
    /// nothing else. Settings changes the theme, the language and a service start type, and every one
    /// of those takes effect when it is made; the terms are a document. Showing Apply beside either
    /// would promise that something on the page is waiting to be saved, and nothing is.
    ///
    /// Diagnostics keeps them, because it is a read-only view of the same configuration the draft
    /// belongs to and losing the buttons on the way there was never part of this.
    /// </summary>
    public bool ShowActionBar => !ShowSettings && !ShowTerms;

    /// <summary>
    /// The toggle's label names where it goes, not where you are. Localized text belongs on the view
    /// model rather than inside a converter, which would need a language of its own to reach.
    /// </summary>
    public string ViewToggleText => ShowDiagnostics ? Strings["Header.Configuration"] : Strings["Header.Diagnostics"];

    [RelayCommand]
    private void ToggleDiagnostics() =>
        Surface = ShowDiagnostics ? AgentConfigSurface.Configuration : AgentConfigSurface.Diagnostics;

    /// <summary>
    /// Whether the header button offers to go home rather than to open settings.
    ///
    /// The glyph is the action, not the location. From the configuration surface - and from
    /// diagnostics, which keeps its own labelled control for getting back - the button opens
    /// settings, so it is a gear. From settings and from the terms it returns to the configuration
    /// surface, so it is a house. A gear that did not open settings would be a lie about what
    /// pressing it does.
    /// </summary>
    public bool ShowHomeAction => ShowSettings || ShowTerms;

    public bool ShowSettingsAction => !ShowHomeAction;

    /// <summary>The tooltip and the accessible name, which are deliberately the same string.</summary>
    public string HeaderActionText => ShowHomeAction ? Strings["Header.Home"] : Strings["Settings.Title"];

    /// <summary>
    /// Opens settings, or goes home.
    ///
    /// Going home is navigation and nothing else. It does not cancel, does not discard the draft and
    /// does not re-read anything: an operator who edited a field, looked at a preference and came
    /// back finds their edit exactly where they left it, with Apply still offering to save it.
    /// </summary>
    [RelayCommand]
    private void HeaderAction() =>
        Surface = ShowHomeAction ? AgentConfigSurface.Configuration : AgentConfigSurface.Settings;

    /// <summary>
    /// The canonical terms, as blocks the page can style.
    ///
    /// Filled on first use rather than at construction: most sessions never open this page, and
    /// parsing three hundred lines of a document nobody asked for is work done on the way to the
    /// window appearing.
    /// </summary>
    public ObservableCollection<AgentTermsBlock> TermsBlocks { get; } = [];

    // ---------------------------------------------------------------- which settings panel

    /// <summary>
    /// The open panel of the settings surface.
    ///
    /// View-model state rather than a TabControl selection, because the strip is built from buttons
    /// and dividers. It also means the panel survives a trip home and back, which is what somebody
    /// expects of a place they were just looking at.
    /// </summary>
    [ObservableProperty]
    private AgentSettingsTab _settingsTab = AgentSettingsTab.General;

    partial void OnSettingsTabChanged(AgentSettingsTab value)
    {
        // Each result is local to its own panel: the start type line belongs to General and the
        // registration line to Agent. Switching panels leaves the action behind with them.
        if (value != AgentSettingsTab.General) ClearStartupResult();
        if (value != AgentSettingsTab.Agent) ClearInstallResult();

        OnPropertyChanged(nameof(IsGeneralTab));
        OnPropertyChanged(nameof(IsAppearanceTab));
        OnPropertyChanged(nameof(IsAgentTab));
        OnPropertyChanged(nameof(IsAboutTab));

        if (value == AgentSettingsTab.Agent) ReloadService();
    }

    public bool IsGeneralTab => SettingsTab == AgentSettingsTab.General;

    public bool IsAppearanceTab => SettingsTab == AgentSettingsTab.Appearance;

    public bool IsAgentTab => SettingsTab == AgentSettingsTab.Agent;

    public bool IsAboutTab => SettingsTab == AgentSettingsTab.About;

    [RelayCommand]
    private void SelectGeneralTab() => SettingsTab = AgentSettingsTab.General;

    [RelayCommand]
    private void SelectAppearanceTab() => SettingsTab = AgentSettingsTab.Appearance;

    [RelayCommand]
    private void SelectAgentTab() => SettingsTab = AgentSettingsTab.Agent;

    [RelayCommand]
    private void SelectAboutTab() => SettingsTab = AgentSettingsTab.About;

    /// <summary>Opens the terms from About.</summary>
    [RelayCommand]
    private void OpenTerms()
    {
        LoadTerms();
        Surface = AgentConfigSurface.Terms;
    }

    private void LoadTerms()
    {
        if (TermsBlocks.Count > 0) return;

        foreach (var block in AgentTermsDocument.Read(SelectedLanguage)) TermsBlocks.Add(block);
    }

    /// <summary>
    /// Back to settings, which comes back showing About.
    ///
    /// The tab strip holds its own selection, so returning here lands on the tab the reader left from
    /// rather than resetting to the first one - which is the whole difference between going back and
    /// starting over.
    /// </summary>
    [RelayCommand]
    private void CloseTerms() => Surface = AgentConfigSurface.Settings;

    // ---------------------------------------------------------------- transports

    [ObservableProperty]
    private bool _namedPipeEnabled = true;

    [ObservableProperty]
    private bool _httpsEnabled;

    /// <summary>
    /// Whether the named pipe checkbox may be operated at all.
    ///
    /// False in the one case that matters: the pipe is the last transport left. The view binds a
    /// checkbox's IsEnabled to this, so the invalid combination cannot be expressed — which is what
    /// "block the action in the UI rather than allowing an invalid Apply" means in practice. Turning
    /// the other transport on re-enables it immediately.
    /// </summary>
    public bool CanToggleNamedPipe => !NamedPipeEnabled || HttpsEnabled;

    public bool CanToggleHttps => !HttpsEnabled || NamedPipeEnabled;

    /// <summary>Shown beside the checkboxes while one of them is the last one standing.</summary>
    public bool ShowsLastTransportNotice => !CanToggleNamedPipe || !CanToggleHttps;

    public string LastTransportNotice => Strings["Transport.LastOne"];

    /// <summary>The badge beside each checkbox, in words. The pill's colour repeats this; it never replaces it.</summary>
    public string NamedPipeStatusText => NamedPipeEnabled ? Strings["Transport.Active"] : Strings["Transport.Inactive"];

    public string HttpsStatusText => HttpsEnabled ? Strings["Transport.Active"] : Strings["Transport.Inactive"];

    partial void OnNamedPipeEnabledChanged(bool value) =>
        OnTransportChanged(value, HttpsEnabled, () => NamedPipeEnabled = true);

    partial void OnHttpsEnabledChanged(bool value)
    {
        OnTransportChanged(value, NamedPipeEnabled, () => HttpsEnabled = true);

        RefreshHttpsValidation();

        // The listener row is derived from the transport, so turning HTTPS on has to redraw it. This
        // recomposes from the resource state already read; it does not go back to HTTP.sys, which
        // would put a blocking Windows query on the UI thread every time a checkbox is clicked.
        RebuildResourceStatus();
        RebuildDiagnostics();

        // Turning the transport off means there is nothing left to probe, and turning it on means
        // there may be. Either way the standing observation no longer describes the current state.
        RequestListenerRefresh();
    }

    /// <summary>
    /// Defence in depth behind the disabled checkbox.
    ///
    /// The view cannot produce this state, but a test, a keyboard binding or a later refactor could.
    /// Rather than let an invalid selection exist and be caught at Apply, it is refused at the moment
    /// it is set and the previous value is put back.
    /// </summary>
    private void OnTransportChanged(bool value, bool other, Action revert)
    {
        if (!_suppressTransportGuard && !value && !other)
        {
            _suppressTransportGuard = true;
            try
            {
                revert();
            }
            finally
            {
                _suppressTransportGuard = false;
            }
        }

        OnPropertyChanged(nameof(CanToggleNamedPipe));
        OnPropertyChanged(nameof(CanToggleHttps));
        OnPropertyChanged(nameof(ShowsLastTransportNotice));
        OnPropertyChanged(nameof(NamedPipeStatusText));
        OnPropertyChanged(nameof(HttpsStatusText));
        OnPropertyChanged(nameof(ActiveTransportsText));
        OnPropertyChanged(nameof(HttpsPortText));
        OnPropertyChanged(nameof(CanResetHttps));
        OnPropertyChanged(nameof(HttpsResetBlockedReason));
        OnPropertyChanged(nameof(HttpsResetToolTip));
        ResetHttpsCommand.NotifyCanExecuteChanged();
        RefreshDirty();
    }

    // ---------------------------------------------------------------- HTTPS

    [ObservableProperty]
    private string _httpsHost = string.Empty;

    [ObservableProperty]
    private int _httpsPort = DefaultHttpsPort;

    [ObservableProperty]
    private AgentCertificateOption? _selectedCertificate;

    public ObservableCollection<AgentCertificateOption> Certificates { get; } = [];

    public bool HasSelectedCertificate => SelectedCertificate is not null;

    /// <summary>The endpoint exactly as it will be written and bound, built once by the shared rules.</summary>
    [ObservableProperty]
    private string _httpsEndpoint = string.Empty;

    /// <summary>Why the current HTTPS settings are not usable, or the confirmation that they are.</summary>
    [ObservableProperty]
    private string? _httpsValidationMessage;

    [ObservableProperty]
    private bool _httpsIsValid;

    [ObservableProperty]
    private string? _certificateImportMessage;

    [ObservableProperty]
    private string? _certificateImportDetail;

    [ObservableProperty]
    private string _certificateImportStateClass = "healthy";

    public bool CanImportCertificate => _certificateImporter is not null && !IsBusy;

    public string? HttpsHostValidationMessage => HttpsEnabled ? DescribeHostProblem() : null;

    public string? HttpsPortValidationMessage => HttpsEnabled && !IsPortValid()
        ? Strings["Https.Invalid.Port"]
        : null;

    public bool HttpsHostHasError => HttpsHostValidationMessage is not null;

    public bool HttpsPortHasError => HttpsPortValidationMessage is not null;

    /// <summary>
    /// Endpoint errors belong on the fields themselves. The compact line below the certificate area
    /// is reserved for certificate/import feedback, so an empty host never consumes a permanent row.
    /// </summary>
    /// <summary>
    /// Whether the line under the thumbprint has anything to add.
    ///
    /// With no certificate chosen it does not. The surface above already says "no certificate
    /// selected", and repeating it here as a warning said the same thing twice, in a row that then
    /// had to find room inside a card sized for one line. A validation message is for a certificate
    /// that was chosen and turned out to be unusable - that is the case worth the space.
    ///
    /// A working certificate keeps the row too, in green. Silence would be ambiguous at the exact
    /// moment an operator wants confirmation: a chosen certificate with nothing said about it reads
    /// as one that has not been checked yet, and the whole point of this line is to say that the
    /// certificate and the host agree before anybody presses Apply.
    ///
    /// An import result is the exception to the "certificate selected" condition: it reports an
    /// attempt rather than a state, and it is worth showing whether or not one ended up selected.
    /// </summary>
    public bool ShowCertificateFeedback =>
        HttpsEnabled &&
        !string.IsNullOrWhiteSpace(CertificateFeedbackMessage) &&
        (CertificateImportMessage is not null ||
            (HasSelectedCertificate && !HttpsHostHasError && !HttpsPortHasError));

    /// <summary>
    /// The thumbprint block collapses when there is nothing to show.
    ///
    /// A label over an empty value is a row of dead space in a card that has none to spare, and it
    /// invites the reading that a thumbprint exists but could not be read.
    /// </summary>
    public bool ShowThumbprint => HasSelectedCertificate;

    public string? CertificateFeedbackMessage => CertificateImportMessage ?? HttpsValidationMessage;

    /// <summary>
    /// The technical detail behind a failed import, shown only on hover.
    ///
    /// It is an exception type and an HRESULT the adapter composed - never a platform message, which
    /// can name a path, a key container or a store, and never anything derived from a password.
    /// </summary>
    public string? CertificateFeedbackDetail => CertificateImportDetail ?? HttpsValidationDetail;

    /// <summary>
    /// The specifics behind a validation message, for the tooltip.
    ///
    /// The card now says only that the certificate does not match the host. Which host was asked for,
    /// and which names the certificate actually carries, is what an administrator needs next - so it
    /// moves here rather than going away. The details panel and Diagnostics still carry it too.
    /// </summary>
    [ObservableProperty]
    private string? _httpsValidationDetail;

    partial void OnHttpsValidationDetailChanged(string? value) =>
        OnPropertyChanged(nameof(CertificateFeedbackDetail));

    public string CertificateFeedbackStateClass => CertificateImportMessage is not null
        ? CertificateImportStateClass
        : HttpsIsValid ? "healthy" : "warning";

    public string CertificateFeedbackIconKey => CertificateFeedbackStateClass switch
    {
        "healthy" => "AgentIconStateReady",
        "critical" => "AgentIconStateError",
        _ => "AgentIconStateAttention",
    };

    public string? CertificateThumbprint => SelectedCertificate?.Thumbprint;

    /// <summary>
    /// Whether the chosen certificate is expanded.
    ///
    /// Inline rather than a Windows certificate dialog: X509Certificate2UI never came to .NET, and the
    /// four facts that decide whether HTTPS will work — the subject, the alternative names, the private
    /// key and the server-authentication usage — are exactly the four this panel shows. Sending an
    /// operator to certlm.msc to read them would be sending them away from the screen that refused the
    /// certificate.
    /// </summary>
    [ObservableProperty]
    private bool _showCertificateDetails;

    partial void OnShowCertificateDetailsChanged(bool value) =>
        OnPropertyChanged(nameof(CertificateDetailsButtonText));

    public string CertificateDetailsButtonText =>
        ShowCertificateDetails ? Strings["Https.Certificate.Hide"] : Strings["Https.Certificate.View"];

    [RelayCommand]
    private void ToggleCertificateDetails() => ShowCertificateDetails = !ShowCertificateDetails;

    public string? CertificateSubject => SelectedCertificate?.Certificate.Subject;

    public string? CertificateIssuer => SelectedCertificate?.Certificate.Issuer;

    public string? CertificateValidity => SelectedCertificate is { } option
        ? $"{option.Certificate.NotBefore:yyyy-MM-dd} — {option.Certificate.NotAfter:yyyy-MM-dd}"
        : null;

    public string? CertificateNotBefore => SelectedCertificate is { } option
        ? option.Certificate.NotBefore.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
        : null;

    public string? CertificateNotAfter => SelectedCertificate is { } option
        ? option.Certificate.NotAfter.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
        : null;

    /// <summary>
    /// The subject alternative names, which are where a modern certificate actually carries the host it
    /// speaks for. Shown because "does not name this host" is the rejection an operator most often
    /// disagrees with, and this is the evidence.
    /// </summary>
    public string? CertificateSubjectAlternativeNames => SelectedCertificate is { } option
        ? option.Certificate.SubjectAlternativeNames.Count > 0
            ? string.Join(", ", option.Certificate.SubjectAlternativeNames)
            : Strings["Https.Certificate.NoSans"]
        : null;

    public string? CertificatePrivateKeyText => SelectedCertificate is { } option
        ? option.Certificate.HasPrivateKey ? Strings["Https.Certificate.Yes"] : Strings["Https.Certificate.No"]
        : null;

    public string? CertificateServerAuthenticationText => SelectedCertificate is { } option
        ? option.Certificate.SupportsServerAuthentication ? Strings["Https.Certificate.Yes"] : Strings["Https.Certificate.No"]
        : null;

    public string? CertificateHostMatchText => SelectedCertificate is { } option
        ? AgentCertificateRules.MatchesHost(option.Certificate, HttpsHost.Trim())
            ? Strings["Https.Certificate.Match"]
            : Strings["Https.Certificate.Mismatch"]
        : null;

    partial void OnHttpsHostChanged(string value)
    {
        ClearImportFeedback();
        OnPropertyChanged(nameof(CertificateHostMatchText));
        RefreshHttpsValidation();
        RefreshDirty();
    }

    partial void OnHttpsPortChanged(int value)
    {
        ClearImportFeedback();
        OnPropertyChanged(nameof(HttpsPortText));
        RefreshHttpsValidation();
        RefreshDirty();
    }

    partial void OnSelectedCertificateChanged(AgentCertificateOption? value)
    {
        ClearImportFeedback();
        OnPropertyChanged(nameof(HasSelectedCertificate));
        OnPropertyChanged(nameof(ShowThumbprint));
        OnPropertyChanged(nameof(CertificateThumbprint));
        OnPropertyChanged(nameof(CertificateSubject));
        OnPropertyChanged(nameof(CertificateIssuer));
        OnPropertyChanged(nameof(CertificateValidity));
        OnPropertyChanged(nameof(CertificateNotBefore));
        OnPropertyChanged(nameof(CertificateNotAfter));
        OnPropertyChanged(nameof(CertificateSubjectAlternativeNames));
        OnPropertyChanged(nameof(CertificatePrivateKeyText));
        OnPropertyChanged(nameof(CertificateServerAuthenticationText));
        OnPropertyChanged(nameof(CertificateHostMatchText));
        RefreshHttpsValidation();
        RefreshDirty();
    }

    /// <summary>
    /// Imports one file through the Windows adapter, refreshes the machine catalog, and selects the
    /// imported certificate. No password is copied into a property: it exists only as this call's
    /// argument and the adapter's PKCS#12 loader input.
    /// </summary>
    public async Task<AgentCertificateImportResult> ImportCertificateAsync(
        string path,
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (_certificateImporter is null)
        {
            // Its own outcome, not the store failure. Nothing was attempted against LocalMachine\My
            // here, and saying otherwise would send somebody to check a store that was never opened.
            var unavailable = AgentCertificateImportResult.From(AgentCertificateImportOutcome.ImporterUnavailable);
            SetImportFeedback(unavailable);
            return unavailable;
        }

        IsBusy = true;
        CertificateImportMessage = null;
        CertificateImportDetail = null;
        NotifyCertificateFeedbackChanged();
        RefreshCommandStates();

        try
        {
            var result = await Task.Run(
                () => _certificateImporter.Import(path, password), cancellationToken).ConfigureAwait(true);

            if (result.Outcome is AgentCertificateImportOutcome.Imported && result.Certificate is { } imported)
            {
                ReloadCertificates(imported.Thumbprint, imported);
                SetImportedCertificateFeedback(imported);
            }
            else
            {
                SetImportFeedback(result);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The adapter classifies every failure it anticipates. One it did not anticipate stops
            // here rather than escaping an async void click handler and taking the window with it —
            // named by type and code, which identifies the fault without quoting a platform message
            // that could carry a path or a container name.
            var unexpected = AgentCertificateImportResult.From(
                AgentCertificateImportOutcome.Failed,
                $"{exception.GetType().Name} (0x{exception.HResult:X8})");
            SetImportFeedback(unexpected);
            return unexpected;
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private void SetImportedCertificateFeedback(AgentCertificateSummary certificate)
    {
        var verdict = AgentCertificateRules.Evaluate(certificate, HttpsHost.Trim(), _time.GetUtcNow());
        CertificateImportStateClass = verdict.IsUsable ? "healthy" : "warning";
        CertificateImportDetail = null;
        CertificateImportMessage = verdict.IsUsable
            ? Strings["Https.Import.Success"]
            : Strings.Format("Https.Import.SuccessWithIssue", DescribeCertificateProblems(
                new AgentCertificateOption(certificate, Strings)));
        NotifyCertificateFeedbackChanged();
    }

    private void SetImportFeedback(AgentCertificateImportResult result)
    {
        if (result.Outcome is AgentCertificateImportOutcome.PasswordRequired)
        {
            CertificateImportMessage = null;
            CertificateImportDetail = null;
            NotifyCertificateFeedbackChanged();
            return;
        }

        CertificateImportStateClass = "critical";

        // Each outcome names its own cause. The generic store message is the last resort, not the
        // default: "could not be imported" tells an administrator nothing they did not already see.
        var reason = result.Outcome switch
        {
            AgentCertificateImportOutcome.PasswordIncorrect => Strings["Https.Import.PasswordIncorrect"],
            AgentCertificateImportOutcome.UnsupportedFile => Strings["Https.Import.Unsupported"],
            AgentCertificateImportOutcome.InvalidFile => Strings["Https.Import.InvalidFile"],
            AgentCertificateImportOutcome.AccessDenied => Strings["Https.Import.AccessDenied"],
            AgentCertificateImportOutcome.ImporterUnavailable => Strings["Https.Import.Unavailable"],
            _ => Strings["Https.Import.Failed"],
        };

        // The technical detail is a type name and an HRESULT the adapter built; it never carries a
        // password, a key or a path. Appending it keeps the support log on screen instead of in a file
        // the operator would have to go and find.
        CertificateImportMessage = reason;

        // The type and HRESULT are for a support conversation, not for the card. Inline they pushed a
        // two-line message to three and told the operator nothing they could act on; on the tooltip
        // they are one hover away for whoever actually needs them.
        CertificateImportDetail = string.IsNullOrWhiteSpace(result.Failure) ? null : result.Failure;
        NotifyCertificateFeedbackChanged();
    }

    private void ClearImportFeedback()
    {
        if (CertificateImportMessage is null) return;
        CertificateImportMessage = null;
        CertificateImportDetail = null;
        NotifyCertificateFeedbackChanged();
    }

    private void NotifyCertificateFeedbackChanged()
    {
        OnPropertyChanged(nameof(CertificateFeedbackMessage));
        OnPropertyChanged(nameof(CertificateFeedbackDetail));
        OnPropertyChanged(nameof(CertificateFeedbackStateClass));
        OnPropertyChanged(nameof(CertificateFeedbackIconKey));
        OnPropertyChanged(nameof(ShowCertificateFeedback));
    }

    /// <summary>
    /// Re-evaluates the endpoint and the certificate together.
    ///
    /// Both, because the certificate has to speak for the host: changing the host can invalidate a
    /// certificate that was fine a moment ago, and saying so here is the difference between a mistake
    /// caught on this screen and a handshake failure investigated as a network problem next week.
    /// </summary>
    private void RefreshHttpsValidation()
    {
        OnPropertyChanged(nameof(HttpsHostValidationMessage));
        OnPropertyChanged(nameof(HttpsPortValidationMessage));
        OnPropertyChanged(nameof(HttpsHostHasError));
        OnPropertyChanged(nameof(HttpsPortHasError));

        if (!HttpsEnabled)
        {
            HttpsEndpoint = AgentHttpsPrefixRules.TryBuildPrefix(HttpsHost, HttpsPort, out var disabledPrefix, out _)
                ? disabledPrefix!
                : string.Empty;
            HttpsValidationMessage = null;
            HttpsIsValid = false;
            NotifyCertificateFeedbackChanged();
            RefreshApplyState();
            return;
        }

        if (!AgentHttpsPrefixRules.TryBuildPrefix(HttpsHost, HttpsPort, out var prefix, out _))
        {
            HttpsEndpoint = string.Empty;
            HttpsValidationMessage = DescribeEndpointProblem();
            HttpsIsValid = false;
            NotifyCertificateFeedbackChanged();
            RefreshApplyState();
            return;
        }

        HttpsEndpoint = prefix!;

        if (SelectedCertificate is not { } option)
        {
            HttpsValidationMessage = Strings["Https.Certificate.None"];
            HttpsIsValid = false;
            NotifyCertificateFeedbackChanged();
            RefreshApplyState();
            return;
        }

        var verdict = AgentCertificateRules.Evaluate(option.Certificate, HttpsHost.Trim(), _time.GetUtcNow());

        HttpsValidationMessage = verdict.IsUsable
            ? Strings["Https.Certificate.Valid"]
            : DescribeCertificateProblems(option);
        HttpsIsValid = verdict.IsUsable;
        NotifyCertificateFeedbackChanged();
        RefreshApplyState();

        // The status strip reports on an endpoint, so it has to be re-read when the endpoint appears
        // or changes. It was only read when the window opened and after an Apply, which is how the
        // strip came to say the SSL binding was absent on a machine where Apply then found another
        // application already bound to the port: the strip was describing a draft that had since
        // become something else.
        //
        // Guarded on the binding actually changing, so this is one query per distinct endpoint and
        // not one per keystroke - an incomplete host does not reach here at all, because it does not
        // pass validation.
        if (!HttpsIsValid || CertificateThumbprint is not { } thumbprint) return;

        var candidate = new AgentHttpsBinding(HttpsHost.Trim(), HttpsPort, thumbprint);
        if (!_resourceStateWasQueried || candidate != _appliedBinding) RefreshResourceState();
    }

    private string? DescribeHostProblem()
    {
        var host = HttpsHost?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(host)) return Strings["Https.Invalid.Host"];

        if (host.Contains('*', StringComparison.Ordinal) || host.StartsWith('+'))
        {
            return Strings["Https.Invalid.Wildcard"];
        }

        return AgentHttpsPrefixRules.TryBuildPrefix(host, DefaultHttpsPort, out _, out _)
            ? null
            : Strings["Https.Invalid.HostFormat"];
    }

    private bool IsPortValid() =>
        HttpsPort is >= AgentHttpsPrefixRules.MinimumPort and <= AgentHttpsPrefixRules.MaximumPort;

    /// <summary>
    /// Why the endpoint is not usable, in the operator's language.
    ///
    /// The rules in Core carry English text, which is right for a log and wrong for this window - an
    /// operator on a pt-BR server was being shown "An explicit host or FQDN is required." The verdict
    /// still comes from Core; only the sentence is chosen here.
    /// </summary>
    private string DescribeEndpointProblem()
    {
        return DescribeHostProblem() ??
               (!IsPortValid() ? Strings["Https.Invalid.Port"] : Strings["Https.Invalid.HostFormat"]);
    }

    /// <summary>
    /// The same for the certificate.
    ///
    /// Each clause re-asks a question Core already answers publicly — the private key, the validity
    /// window, the server-authentication usage, and <see cref="AgentCertificateRules.MatchesHost"/> —
    /// so the phrasing is localized here without the rule being decided here. Whether the certificate
    /// is usable at all remains Core's answer, and this only runs once it has said no.
    /// </summary>
    private string DescribeCertificateProblems(AgentCertificateOption option)
    {
        // Cleared up front: a detail left over from a previous host would describe a mismatch that has
        // since been fixed, on a tooltip nobody would think to distrust.
        HttpsValidationDetail = null;

        var certificate = option.Certificate;
        var now = _time.GetUtcNow();

        // One reason, not a list. Three stacked warnings cost three lines of a 600px window and still
        // leave the operator deciding which to act on first, so the order below is that decision made
        // once: a missing private key cannot be fixed from this screen at all, an expired certificate
        // comes next, and the name mismatch last because it is the one a different host would resolve.
        if (!certificate.HasPrivateKey) return Strings["Https.Cert.NoPrivateKey"];

        if (now < certificate.NotBefore)
        {
            return Strings.Format("Https.Cert.NotYetValid", certificate.NotBefore.ToString("dd/MM/yyyy"));
        }

        if (now > certificate.NotAfter)
        {
            return Strings.Format("Https.Cert.Expired", certificate.NotAfter.ToString("dd/MM/yyyy"));
        }

        if (!certificate.SupportsServerAuthentication) return Strings["Https.Cert.NoServerAuth"];

        var host = HttpsHost?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(host) && !AgentCertificateRules.MatchesHost(certificate, host))
        {
            // One short sentence on the card. Naming the host and then listing the subject and every
            // alternative name made the longest message on the screen out of the most common problem,
            // and it was the message that kept overflowing its card. The same facts are on the
            // tooltip, in the details panel and in Diagnostics.
            HttpsValidationDetail = Strings.Format(
                "Https.Cert.HostMismatch.Detail",
                host,
                certificate.SubjectAlternativeNames.Count > 0
                    ? string.Join(", ", certificate.SubjectAlternativeNames)
                    : certificate.Subject);

            return Strings["Https.Cert.HostMismatch"];
        }

        // Core judged the certificate unusable and every reason above was ruled out. Saying so beats
        // returning an empty string, which would read as approval.
        return Strings["Https.Cert.Unusable"];
    }

    // ---------------------------------------------------------------- operators group

    public string OperatorsGroupName => _groupState.GroupName;

    [ObservableProperty]
    private bool _operatorsGroupExists;

    [ObservableProperty]
    private string _newMemberAccount = string.Empty;

    [ObservableProperty]
    private string? _operatorsMessage;

    public ObservableCollection<string> Members { get; } = [];

    public bool HasMembers => Members.Count > 0;

    /// <summary>Whether creating the group here would write to a directory rather than to this machine.</summary>
    public bool GroupCreationAffectsDirectory => _groupState.CreationAffectsDirectory;

    [RelayCommand]
    private void CreateGroup()
    {
        if (_groupState.Exists) return;

        // A domain controller has no independent SAM, so this would create a directory object visible
        // on every server in the domain. That is a different act from adding a local group to one
        // machine, and it is confirmed before anything happens rather than explained afterwards.
        if (_groupState.CreationAffectsDirectory)
        {
            PendingConfirmation = AgentConfigConfirmation.CreateGroupInDirectory;
            return;
        }

        CreateGroupCore();
    }

    private void CreateGroupCore()
    {
        var result = _groups.Create();

        if (!result.Created && result.Sid is null)
        {
            OperatorsMessage = result.Failure;
            return;
        }

        OperatorsMessage = Strings["Operators.Created"];
        ReloadGroup();
    }

    [RelayCommand]
    private void AddMember()
    {
        var account = NewMemberAccount?.Trim();
        if (string.IsNullOrWhiteSpace(account)) return;

        var result = _groups.AddMember(account);

        OperatorsMessage = result.Outcome switch
        {
            AgentMembershipOutcome.Added => Strings.Format("Operators.Added", result.AccountName),
            // Wanting an account in a group it is already in is the desired state, reached earlier.
            // Reporting that as a failure would be technically defensible and practically useless.
            AgentMembershipOutcome.AlreadyMember => Strings.Format("Operators.AlreadyMember", result.AccountName),
            _ => result.Detail,
        };

        if (result.Succeeded)
        {
            NewMemberAccount = string.Empty;
            ReloadMembers();
        }
    }

    private void ReloadGroup()
    {
        _groupState = _groups.Describe();
        OperatorsGroupExists = _groupState.Exists;

        OnPropertyChanged(nameof(OperatorsGroupName));
        OnPropertyChanged(nameof(GroupCreationAffectsDirectory));

        ReloadMembers();
        RebuildDiagnostics();
    }

    private void ReloadMembers()
    {
        Members.Clear();

        if (_groupState.Exists)
        {
            foreach (var member in _groups.ListMembers()) Members.Add(member);
        }

        OnPropertyChanged(nameof(HasMembers));
    }

    // ---------------------------------------------------------------- service

    public AgentServiceState ServiceState => _serviceState.State;

    [ObservableProperty]
    private string _serviceStateText = string.Empty;

    [ObservableProperty]
    private string? _serviceStartModeText;

    [ObservableProperty]
    private string? _serviceMessage;

    /// <summary>
    /// Whether saved configuration is waiting for a restart to reach the listener.
    ///
    /// This was only ever a sentence appended to the apply message, which put a full explanation in
    /// the narrow strip between the service actions and the Apply button and squeezed both. As a
    /// state it can be a short marker with the explanation on its tooltip - and it is set from the
    /// same condition as before: configuration was written while the service was running.
    ///
    /// It never causes a restart. It is a fact on screen, and the restart stays an explicit action.
    /// </summary>
    [ObservableProperty]
    private bool _restartRequired;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// The footer's one line about the service: which service, and what it is doing. Two facts, in the
    /// order an operator reads them, and never merged into a single word.
    /// </summary>
    public string ServiceFooterText =>
        $"{Strings["Service.Title"]} {Strings["Service.Name"]} ({ServiceStateText})";

    public bool ServiceIsRunning => _serviceState.IsRunning;

    public bool ServiceIsInstalled => _serviceState.IsInstalled;

    public bool CanStartService => ServiceIsInstalled && !ServiceIsRunning && !IsBusy;

    public bool CanStopService => ServiceIsRunning && !IsBusy;

    public bool CanRestartService => ServiceIsRunning && !IsBusy;

    /// <summary>
    /// The first service-control position is stable: it starts a stopped service and stops a running
    /// one. A missing service keeps the start control visible but disabled so the action row does not
    /// jump while the service state changes.
    /// </summary>
    public bool ShowStartServiceAction => !ServiceIsRunning;

    public bool ShowStopServiceAction => ServiceIsRunning;

    [RelayCommand]
    private Task StartServiceAsync(CancellationToken cancellationToken) =>
        RunServiceOperationAsync(_service.StartAsync, cancellationToken);

    [RelayCommand]
    private Task StopServiceAsync(CancellationToken cancellationToken) =>
        RunServiceOperationAsync(_service.StopAsync, cancellationToken);

    [RelayCommand]
    private Task RestartServiceAsync(CancellationToken cancellationToken) =>
        RunServiceOperationAsync(_service.RestartAsync, cancellationToken);

    private async Task RunServiceOperationAsync(
        Func<CancellationToken, Task<AgentServiceOutcome>> operation, CancellationToken cancellationToken)
    {
        IsBusy = true;
        RefreshCommandStates();

        try
        {
            var outcome = await operation(cancellationToken).ConfigureAwait(true);
            ServiceMessage = outcome.Succeeded ? null : outcome.Failure;

            // The listener has been rebuilt from what was saved, so the marker has nothing left to
            // warn about. Only a successful operation clears it.
            if (outcome.Succeeded) RestartRequired = false;

            ReloadService();
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private void ReloadService()
    {
        _serviceState = _service.Describe();
        ApplyServiceText();

        OnPropertyChanged(nameof(ServiceState));
        OnPropertyChanged(nameof(ServiceIsRunning));
        OnPropertyChanged(nameof(ServiceIsInstalled));
        OnPropertyChanged(nameof(ShowStartServiceAction));
        OnPropertyChanged(nameof(ShowStopServiceAction));
        OnPropertyChanged(nameof(CanInstallService));
        OnPropertyChanged(nameof(ServiceInstallDescription));
        InstallServiceCommand.NotifyCanExecuteChanged();
        SyncStartupFromService();
        RefreshCommandStates();

        // The listener row reads the service state, so re-reading the service has to redraw the strip
        // and not only the diagnostics list. Without this the card said "running" beside a listener
        // row still describing the service as stopped, which is the disagreement an operator sees
        // first and trusts least.
        RebuildResourceStatus();
        RebuildDiagnostics();

        // Starting, stopping or restarting changes what the endpoint will say. Asked now rather than
        // at the next tick, and never assumed: a service that has just been started is running long
        // before its prefix is open, so the row stays unavailable until a probe actually succeeds.
        RequestListenerRefresh();
    }

    /// <summary>
    /// Renders the service state into words, from state that has already been read.
    ///
    /// Separate from <see cref="ReloadService"/> because changing the interface language must not
    /// query the service control manager. Re-reading the machine to re-word a label would turn a view
    /// preference into an administrative call.
    /// </summary>
    private void ApplyServiceText()
    {
        ServiceStateText = _serviceState.State switch
        {
            AgentServiceState.NotInstalled => Strings["Service.State.NotInstalled"],
            AgentServiceState.Stopped => Strings["Service.State.Stopped"],
            AgentServiceState.Running => Strings["Service.State.Running"],
            AgentServiceState.StartPending => Strings["Service.State.StartPending"],
            AgentServiceState.StopPending => Strings["Service.State.StopPending"],
            AgentServiceState.Paused => Strings["Service.State.Paused"],
            _ => Strings["Service.State.Unknown"],
        };

        ServiceStartModeText = string.IsNullOrWhiteSpace(_serviceState.StartMode)
            ? null
            : Strings.Format("Service.StartMode", _serviceState.StartMode);

        OnPropertyChanged(nameof(ServiceFooterText));
    }

    // ---------------------------------------------------------------- confirmations

    [ObservableProperty]
    private AgentConfigConfirmation _pendingConfirmation = AgentConfigConfirmation.None;

    public bool IsConfirming => PendingConfirmation is not AgentConfigConfirmation.None;

    /// <summary>The three cleanup checkboxes, ticked by default and each independently refusable.</summary>
    [ObservableProperty]
    private bool _cleanupFirewallRule = true;

    [ObservableProperty]
    private bool _cleanupSslBinding = true;

    [ObservableProperty]
    private bool _cleanupUrlReservation = true;

    partial void OnPendingConfirmationChanged(AgentConfigConfirmation value)
    {
        OnPropertyChanged(nameof(IsConfirming));
        OnPropertyChanged(nameof(ConfirmationTitle));
        OnPropertyChanged(nameof(ConfirmationMessage));
        OnPropertyChanged(nameof(ConfirmButtonText));
        OnPropertyChanged(nameof(IsDisablingHttps));
    }

    /// <summary>Whether the open confirmation is the HTTPS teardown, which is the only one with choices.</summary>
    public bool IsDisablingHttps => PendingConfirmation is AgentConfigConfirmation.DisableHttps;


    /// <summary>
    /// The affirmative button's label. Each confirmation names what it will actually do rather than
    /// saying "OK" — the difference between "Criar no domínio" and "OK" is the whole point of asking.
    /// </summary>
    public string ConfirmButtonText => PendingConfirmation switch
    {
        AgentConfigConfirmation.ManualStartup => Strings["Settings.Startup.Manual.Confirm"],
        AgentConfigConfirmation.CreateGroupInDirectory => Strings["Operators.DirectoryConfirm"],
        AgentConfigConfirmation.DisableHttps => Strings["Cleanup.RemoveAndDisable"],
        AgentConfigConfirmation.ResetHttps => Strings["Https.Reset.Confirm"],
        AgentConfigConfirmation.RestartService => Strings["Service.Restart"],
        _ => Strings["Action.Confirm"],
    };

    public string? ConfirmationTitle => PendingConfirmation switch
    {
        AgentConfigConfirmation.ManualStartup => Strings["Settings.Startup.Manual.Title"],
        AgentConfigConfirmation.CreateGroupInDirectory => Strings["Operators.DirectoryTitle"],
        AgentConfigConfirmation.DisableHttps => Strings["Cleanup.Title"],
        AgentConfigConfirmation.ResetHttps => Strings["Https.Reset.Title"],
        AgentConfigConfirmation.RestartService => Strings["Service.RestartTitle"],
        _ => null,
    };

    public string? ConfirmationMessage => PendingConfirmation switch
    {
        AgentConfigConfirmation.ManualStartup => Strings["Settings.Startup.Manual.Question"],
        AgentConfigConfirmation.CreateGroupInDirectory => Strings["Operators.DirectoryWarning"],
        AgentConfigConfirmation.DisableHttps => Strings["Cleanup.Message"],
        AgentConfigConfirmation.ResetHttps => Strings["Https.Reset.Message"],
        AgentConfigConfirmation.RestartService => Strings["Service.RestartQuestion"],
        _ => null,
    };

    [RelayCommand]
    private void CancelConfirmation() => PendingConfirmation = AgentConfigConfirmation.None;

    // ---------------------------------------------------------------- reset HTTPS

    /// <summary>
    /// Whether the reset is refusable before it is offered, and why.
    ///
    /// Null means it can run. Named rather than boolean because the one thing that blocks it - HTTPS
    /// being the only transport left - is a rule the operator can satisfy themselves, and a disabled
    /// button with no reason given is a rule nobody can satisfy.
    /// </summary>
    public string? HttpsResetBlockedReason => NamedPipeEnabled || !HttpsEnabled
        ? null
        : Strings["Https.Reset.LastTransport"];

    public bool CanResetHttps => HttpsResetBlockedReason is null && !IsBusy;

    public string HttpsResetToolTip => HttpsResetBlockedReason ?? Strings["Https.Reset.Tooltip"];

    /// <summary>
    /// Opens the confirmation. It never changes anything by itself.
    ///
    /// Reset is not the HTTPS checkbox with a different label. The checkbox turns a transport off and
    /// leaves the machine as it is; this removes the SSL binding, the URL reservation and the firewall
    /// rule that this product created, forgets the endpoint and the certificate this product chose,
    /// and puts the port back to its default. The certificate itself is never touched.
    /// </summary>
    [RelayCommand]
    private void ResetHttps()
    {
        if (!CanResetHttps) return;
        PendingConfirmation = AgentConfigConfirmation.ResetHttps;
    }

    /// <summary>
    /// Removes what is provably this product's, then forgets the configuration that named it.
    ///
    /// Resources first and the file second, the same order as Apply and for the same reason: if the
    /// removal fails, the configuration still describes the machine as it actually is. A resource left
    /// behind because it belongs to somebody else is not a failure - it is the ownership rule working,
    /// and it is reported rather than retried.
    /// </summary>
    private async Task ResetHttpsCoreAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ClearApplyResult();
        RefreshCommandStates();

        try
        {
            var notes = new List<string>();

            if (_appliedBinding is { } binding)
            {
                var removed = await Task.Run(
                    () => _resources.Remove(binding, AgentHttpsCleanupRequest.Everything),
                    cancellationToken).ConfigureAwait(true);

                if (!removed.Succeeded)
                {
                    // Some resources may already be gone. Saying so, and naming what was removed
                    // before the failure, beats writing a configuration that claims HTTPS was reset on
                    // a machine that still has a live binding on it.
                    var failure = new List<string>();
                    if (!string.IsNullOrWhiteSpace(removed.Failure)) failure.Add(removed.Failure);
                    if (removed.Applied.Count > 0)
                    {
                        failure.Add(Strings.Format(
                            "Https.Reset.PartiallyRemoved", string.Join(", ", removed.Applied)));
                    }

                    SetApplyResult(
                        AgentApplyResultKind.Error,
                        Strings["Https.Reset.Failed"],
                        failure.Count > 0 ? string.Join(" ", failure) : null);
                    RefreshResourceState();
                    return;
                }

                // Resources the machine kept because they are not ours. Reported, never retried.
                notes.AddRange(removed.Skipped);
            }

            _suppressTransportGuard = true;
            try
            {
                HttpsEnabled = false;
            }
            finally
            {
                _suppressTransportGuard = false;
            }

            HttpsHost = string.Empty;
            HttpsPort = DefaultHttpsPort;
            SelectedCertificate = null;
            _appliedBinding = null;

            var document = BuildDocument();
            var write = _store.Write(document);

            if (!write.Succeeded)
            {
                // The resources are gone and the file still names them. The screen keeps the reset
                // state rather than claiming an endpoint whose binding no longer exists, and the
                // operator is told that the file is the part that failed.
                SetApplyResult(
                    AgentApplyResultKind.Error, Strings["Apply.Result.ConfigurationFailed"], write.Failure);
                RefreshDirty();
                RefreshResourceState();
                return;
            }

            _confirmed = document;
            OnPropertyChanged(nameof(ActiveTransportsText));
            OnPropertyChanged(nameof(HttpsPortText));
            RefreshDirty();
            RefreshResourceState();
            RefreshHttpsValidation();

            notes.Insert(0, Strings["Https.Reset.Done"]);

            // Reset never starts or restarts the agent on its own; a running one is offered the
            // restart in exactly the way Apply offers it.
            if (_serviceState.IsRunning)
            {
                RestartRequired = true;
                SetApplyResult(AgentApplyResultKind.Success, notes[0], BuildDetail(notes));
                PendingConfirmation = AgentConfigConfirmation.RestartService;
                return;
            }

            SetApplyResult(AgentApplyResultKind.Success, notes[0], BuildDetail(notes));
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    /// <summary>
    /// The affirmative answer to whichever confirmation is open.
    ///
    /// For the HTTPS one this is "disable and remove"; <see cref="DisableWithoutRemovingAsync"/> is the
    /// other button, and it turns the transport off while leaving every system resource alone.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmAsync(CancellationToken cancellationToken)
    {
        var pending = PendingConfirmation;
        PendingConfirmation = AgentConfigConfirmation.None;

        switch (pending)
        {
            case AgentConfigConfirmation.ManualStartup:
                // The one place the start type moves to Manual. Reaching it means somebody read what
                // it does and said yes.
                await ApplyStartupPreferenceAsync(automatic: false).ConfigureAwait(true);
                break;

            case AgentConfigConfirmation.CreateGroupInDirectory:
                CreateGroupCore();
                return;

            case AgentConfigConfirmation.DisableHttps:
                await ApplyCoreAsync(
                    new AgentHttpsCleanupRequest(CleanupFirewallRule, CleanupSslBinding, CleanupUrlReservation),
                    cancellationToken).ConfigureAwait(true);
                return;

            case AgentConfigConfirmation.ResetHttps:
                await ResetHttpsCoreAsync(cancellationToken).ConfigureAwait(true);
                return;

            case AgentConfigConfirmation.RestartService:
                await RunServiceOperationAsync(_service.RestartAsync, cancellationToken).ConfigureAwait(true);
                return;

            default:
                return;
        }
    }

    /// <summary>Turn HTTPS off and leave the firewall rule, the binding and the reservation in place.</summary>
    [RelayCommand]
    private async Task DisableWithoutRemovingAsync(CancellationToken cancellationToken)
    {
        PendingConfirmation = AgentConfigConfirmation.None;
        await ApplyCoreAsync(AgentHttpsCleanupRequest.Nothing, cancellationToken).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- apply and cancel

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string? _applyMessage;

    [ObservableProperty]
    private bool _applyFailed;

    /// <summary>
    /// What the adapter actually said, kept off the banner and put on its tooltip.
    ///
    /// The infrastructure describes a refusal precisely and in English - "An SSL certificate is
    /// already bound to port 5199 by another application..." - and that sentence appeared verbatim in
    /// the Portuguese window, in the strip beside the buttons, where it ran underneath them. The
    /// short line above it is localised and says the same thing; this keeps the original for whoever
    /// needs the detail.
    /// </summary>
    [ObservableProperty]
    private string? _applyResultDetail;

    /// <summary>How the banner should read: nothing, a success, a caution, or a refusal.</summary>
    [ObservableProperty]
    private AgentApplyResultKind _applyResultKind;

    public bool HasApplyResult => ApplyResultKind is not AgentApplyResultKind.None;

    partial void OnApplyResultKindChanged(AgentApplyResultKind value) =>
        OnPropertyChanged(nameof(HasApplyResult));

    /// <summary>
    /// Turns a failed resource operation into one localised line.
    ///
    /// The mapping reads the ownership this window already queried rather than matching on the
    /// adapter's English: a foreign SSL binding and a foreign reservation are the two refusals an
    /// operator actually meets, and both are already classified. Anything else gets the honest
    /// generic line - inventing a cause from an unrecognised message would be worse than admitting
    /// the operation failed and handing over the detail.
    /// </summary>
    private void SetHttpsFailure(string? detail)
    {
        ApplyResultDetail = detail;

        var message = _resourceState.SslBinding.Ownership is AgentResourceOwnership.ForeignOwner
            ? Strings.Format("Apply.Result.SslBindingConflict", HttpsPort)
            : _resourceState.UrlReservation.Ownership is AgentResourceOwnership.ForeignOwner
                ? Strings["Apply.Result.UrlReservationConflict"]
                : Strings["Apply.Result.HttpsFailed"];

        ApplyFailed = true;
        ApplyResultKind = AgentApplyResultKind.Error;
        ApplyMessage = message;
    }

    private void SetApplyResult(AgentApplyResultKind kind, string message, string? detail = null)
    {
        ApplyFailed = kind is AgentApplyResultKind.Error;
        ApplyResultKind = kind;
        ApplyMessage = message;
        ApplyResultDetail = detail;
    }

    private void ClearApplyResult()
    {
        ApplyFailed = false;
        ApplyResultKind = AgentApplyResultKind.None;
        ApplyMessage = null;
        ApplyResultDetail = null;
    }

    public bool CanApply => IsDirty && !IsBusy && (!HttpsEnabled || HttpsIsValid);

    /// <summary>
    /// Why Apply is refused, or null when it is not.
    ///
    /// The button being disabled is correct - half-configured HTTPS must not reach agent.json - but a
    /// disabled control with no explanation leaves an administrator hunting for the field that is
    /// wrong. The order below is the order the conditions are actually evaluated in, so the reason
    /// shown is the one that would still be blocking after the operator fixes it.
    ///
    /// Nothing is validated here. Every branch reads a state that already exists.
    /// </summary>
    public string? ApplyDisabledReason
    {
        get
        {
            if (CanApply) return null;

            if (IsBusy) return Strings["Apply.Disabled.Busy"];

            // Not dirty comes before the HTTPS checks: a valid saved configuration is not something
            // to complain about, and "no pending changes" is the honest answer.
            if (!IsDirty) return Strings["Apply.Disabled.NoChanges"];

            if (!NamedPipeEnabled && !HttpsEnabled) return Strings["Apply.Disabled.NoTransport"];

            // With HTTPS off, a missing certificate is not a problem: it is not needed.
            if (!HttpsEnabled) return null;

            if (HttpsHostHasError) return Strings["Apply.Disabled.InvalidHost"];
            if (HttpsPortHasError) return Strings["Apply.Disabled.InvalidPort"];
            if (!HasSelectedCertificate) return Strings["Apply.Disabled.NoCertificate"];

            // A certificate was chosen and refused. The reason is the validation message that already
            // explains why, rather than a second sentence that would have to agree with it.
            return HttpsValidationMessage ?? Strings["Apply.Disabled.NoCertificate"];
        }
    }

    public bool HasApplyDisabledReason => ApplyDisabledReason is not null;

    // ---------------------------------------------------------------- choosing an installed certificate

    /// <summary>
    /// The certificates offered by the selection panel, ordered for this host.
    ///
    /// Built when the panel opens rather than kept continuously in step, because the ordering depends
    /// on the host in the draft and that changes while somebody is typing.
    /// </summary>
    public ObservableCollection<AgentCertificateCandidate> CertificateCandidates { get; } = [];

    [ObservableProperty]
    private bool _isSelectingCertificate;

    /// <summary>Highlighted in the list, and not yet the draft. Confirming is what moves it.</summary>
    [ObservableProperty]
    private AgentCertificateCandidate? _pendingCertificate;

    partial void OnPendingCertificateChanged(AgentCertificateCandidate? value) =>
        OnPropertyChanged(nameof(CanConfirmCertificateSelection));

    public bool CanConfirmCertificateSelection => PendingCertificate is not null;

    public bool HasCertificateCandidates => CertificateCandidates.Count > 0;

    /// <summary>
    /// Opens the panel over the machine store, reading and nothing else.
    ///
    /// This is the answer for a certificate that is already installed: an operator should not have to
    /// find the PFX again and import a second copy of something Windows already holds. Enumerating is
    /// all that happens - no import, no export, no key access, no change of any kind to the store.
    /// </summary>
    [RelayCommand]
    private void OpenCertificateSelection()
    {
        var host = HttpsHost.Trim();
        var now = _time.GetUtcNow();

        CertificateCandidates.Clear();

        // Ordered so the ones that would work come first, and never filtered: an operator diagnosing
        // a refused endpoint needs to see the certificate that is failing, not have it hidden for
        // failing. Ties fall back to the latest expiry and then to the name, so the order is stable.
        var candidates = _certificates.List()
            .Select(certificate => new AgentCertificateCandidate(certificate, host, now, Strings))
            .OrderByDescending(candidate => candidate.IsUsable)
            .ThenByDescending(candidate => candidate.MatchesHost)
            .ThenByDescending(candidate => candidate.HasPrivateKey)
            .ThenByDescending(candidate => candidate.SupportsServerAuthentication)
            .ThenByDescending(candidate => candidate.IsCurrentlyValid)
            .ThenByDescending(candidate => candidate.Certificate.NotAfter)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates) CertificateCandidates.Add(candidate);

        // Whatever is already in the draft starts highlighted. Nothing else is preselected: several
        // certificates here can share a common name and an issuer, and choosing one of those on the
        // operator's behalf is how the wrong one ends up configured.
        PendingCertificate = CertificateThumbprint is { } thumbprint
            ? CertificateCandidates.FirstOrDefault(candidate => string.Equals(
                candidate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
            : null;

        OnPropertyChanged(nameof(HasCertificateCandidates));
        IsSelectingCertificate = true;
    }

    /// <summary>
    /// Puts the highlighted certificate into the draft. Nothing is saved and nothing on the machine is
    /// touched: the file, the binding, the firewall rule and the service all wait for Apply.
    /// </summary>
    [RelayCommand]
    private void ConfirmCertificateSelection()
    {
        if (PendingCertificate is not { } candidate) return;

        IsSelectingCertificate = false;
        ReloadCertificates(candidate.Thumbprint, candidate.Certificate);
    }

    /// <summary>Closes the panel and leaves the draft exactly as it was.</summary>
    [RelayCommand]
    private void CancelCertificateSelection() => IsSelectingCertificate = false;

    [RelayCommand]
    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (!CanApply) return;

        // Turning HTTPS off while its system resources are still on the machine is the one Apply that
        // asks a question first: the operator decides whether the binding, the reservation and the rule
        // go with it. Everything else applies straight through.
        if (_confirmed.HttpsEnabled && !HttpsEnabled && HasRemovableHttpsResources())
        {
            PendingConfirmation = AgentConfigConfirmation.DisableHttps;
            return;
        }

        await ApplyCoreAsync(AgentHttpsCleanupRequest.Nothing, cancellationToken).ConfigureAwait(true);
    }

    private bool HasRemovableHttpsResources() =>
        _resourceState.SslBinding.MayRemove ||
        _resourceState.UrlReservation.MayRemove ||
        _resourceState.FirewallRule.MayRemove;

    /// <summary>
    /// The actual save.
    ///
    /// System resources are changed before the file, deliberately. If a binding cannot be written the
    /// configuration is left exactly as it was, so the agent keeps running the way it was running; the
    /// reverse order would leave a service configured for a listener that cannot open. The cost is that
    /// a successful resource change followed by a file failure leaves resources in place unused, which
    /// is harmless and is reported.
    /// </summary>
    private async Task ApplyCoreAsync(AgentHttpsCleanupRequest cleanup, CancellationToken cancellationToken)
    {
        IsBusy = true;
        ClearApplyResult();
        RefreshCommandStates();

        try
        {
            var notes = new List<string>();

            if (HttpsEnabled)
            {
                if (!HttpsIsValid)
                {
                    SetApplyResult(
                        AgentApplyResultKind.Error,
                        HttpsValidationMessage ?? Strings["Apply.Result.HttpsFailed"]);
                    return;
                }

                var binding = new AgentHttpsBinding(HttpsHost.Trim(), HttpsPort, CertificateThumbprint!);
                var applied = await Task.Run(() => _resources.Apply(binding), cancellationToken).ConfigureAwait(true);

                if (!applied.Succeeded)
                {
                    SetHttpsFailure(applied.Failure);
                    return;
                }

                notes.AddRange(applied.Skipped);
                _appliedBinding = binding;
            }
            else if (cleanup.RemovesAnything && _appliedBinding is { } previous)
            {
                var removed = await Task.Run(() => _resources.Remove(previous, cleanup), cancellationToken).ConfigureAwait(true);
                notes.AddRange(removed.Skipped);
            }

            var document = BuildDocument();
            var write = _store.Write(document);

            if (!write.Succeeded)
            {
                SetApplyResult(
                    AgentApplyResultKind.Error, Strings["Apply.Result.ConfigurationFailed"], write.Failure);
                return;
            }

            _confirmed = document;
            OnPropertyChanged(nameof(ActiveTransportsText));
            OnPropertyChanged(nameof(HttpsPortText));
            RefreshDirty();
            RefreshResourceState();

            notes.Insert(0, Strings["Message.Saved"]);

            // Saving configuration never starts the agent and never restarts it silently. A running
            // service is offered a restart; a stopped one is left stopped and told that its new
            // configuration is waiting for whenever somebody starts it.
            if (_serviceState.IsRunning)
            {
                RestartRequired = true;
                SetApplyResult(AgentApplyResultKind.Success, notes[0], BuildDetail(notes));
                PendingConfirmation = AgentConfigConfirmation.RestartService;
                return;
            }

            if (_serviceState.IsInstalled) notes.Add(Strings["Service.StoppedAfterApply"]);

            SetApplyResult(AgentApplyResultKind.Success, notes[0], BuildDetail(notes));
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    /// <summary>
    /// The follow-up notes, as one line for the tooltip.
    ///
    /// The banner shows the headline - saved, or reset - because that is what an operator needs at a
    /// glance in the strip above the buttons. What the machine refused to touch, and what happens
    /// next, is the part they read when it matters.
    /// </summary>
    private static string? BuildDetail(IReadOnlyList<string> notes) =>
        notes.Count > 1 ? string.Join(" ", notes.Skip(1)) : null;

    /// <summary>Restores every field to the last saved document. Nothing on the machine is touched.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!IsDirty)
        {
            SetApplyResult(AgentApplyResultKind.Info, Strings["Message.NoChanges"]);
            return;
        }

        LoadDocument(_confirmed);
        SetApplyResult(AgentApplyResultKind.Info, Strings["Message.Discarded"]);
    }

    private AgentTransportConfigurationDocument BuildDocument() => new()
    {
        // Written explicitly rather than left absent, so a file this utility saved says what it means
        // and does not rely on the legacy default being read the same way later.
        NamedPipeEnabled = NamedPipeEnabled,
        HttpsEnabled = HttpsEnabled,
        HttpsPrefix = HttpsEnabled ? HttpsEndpoint : null,
        CertificateThumbprint = HttpsEnabled ? CertificateThumbprint : null,
    };

    private void RefreshDirty()
    {
        var current = BuildDocument();

        IsDirty =
            current.NamedPipeIsEnabled != _confirmed.NamedPipeIsEnabled ||
            current.HttpsEnabled != _confirmed.HttpsEnabled ||
            !string.Equals(current.HttpsPrefix, _confirmed.HttpsPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.CertificateThumbprint, _confirmed.CertificateThumbprint, StringComparison.OrdinalIgnoreCase);

        RefreshApplyState();
    }

    private void RefreshApplyState()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ApplyDisabledReason));
        OnPropertyChanged(nameof(HasApplyDisabledReason));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanStartService));
        OnPropertyChanged(nameof(CanStopService));
        OnPropertyChanged(nameof(CanRestartService));
        OnPropertyChanged(nameof(CanImportCertificate));
        OnPropertyChanged(nameof(CanResetHttps));
        OnPropertyChanged(nameof(HttpsResetBlockedReason));
        OnPropertyChanged(nameof(CanChangeStartup));
        OnPropertyChanged(nameof(StartupBlockedReason));
        ResetHttpsCommand.NotifyCanExecuteChanged();
        RefreshApplyState();
    }

    // ---------------------------------------------------------------- load

    /// <summary>
    /// Puts a failed start-up read on the screen.
    ///
    /// Reading the machine is the one thing this window does before an operator touches anything, and
    /// when it fails every section is empty for a reason nobody can see. This is the reason, in the
    /// place they are already looking.
    /// </summary>
    public void ReportStartupFailure(AggregateException? failure)
    {
        var inner = failure?.GetBaseException();
        if (inner is null) return;

        SetApplyResult(
            AgentApplyResultKind.Error,
            Strings["Message.RefreshFailed"],
            $"{inner.GetType().Name}: {inner.Message}");
    }

    /// <summary>Reads everything the window shows. Safe to call again at any time: nothing here writes.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        RefreshCommandStates();

        try
        {
            var document = _store.Read();
            var certificates = _certificates.List();
            _machine = await _inventory.DescribeAsync(cancellationToken).ConfigureAwait(true);

            // About binds these once. Diagnostics survived without this because it rebuilds an
            // observable collection, so the two views disagreed: the same machine reading appeared in
            // the diagnostics list and stayed "unknown" on the About tab.
            OnPropertyChanged(nameof(AboutDotNetRuntime));
            OnPropertyChanged(nameof(AboutAspNetCoreRuntime));

            ReloadCertificates(null, null, certificates);

            _confirmed = document;
            LoadDocument(document);

            ReloadGroup();
            ReloadService();
            RefreshResourceState();

            // Awaited, so the window opens on what the endpoint said rather than on "checking" and a
            // correction a moment later. Both calls above have already queued an immediate refresh;
            // this is the same observation, taken once, before anybody sees the strip.
            await RefreshListenerAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private void ReloadCertificates(
        string? selectedThumbprint,
        AgentCertificateSummary? importedFallback = null,
        IReadOnlyList<AgentCertificateSummary>? knownCertificates = null)
    {
        var certificates = knownCertificates ?? _certificates.List();

        Certificates.Clear();
        foreach (var certificate in certificates)
        {
            Certificates.Add(new AgentCertificateOption(certificate, Strings));
        }

        if (importedFallback is not null && Certificates.All(option => !string.Equals(
                option.Thumbprint,
                importedFallback.Thumbprint,
                StringComparison.OrdinalIgnoreCase)))
        {
            Certificates.Add(new AgentCertificateOption(importedFallback, Strings));
        }

        if (!string.IsNullOrWhiteSpace(selectedThumbprint))
        {
            SelectedCertificate = Certificates.FirstOrDefault(option => string.Equals(
                option.Thumbprint,
                AgentHttpsPrefixRules.NormalizeThumbprint(selectedThumbprint),
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private void LoadDocument(AgentTransportConfigurationDocument document)
    {
        // Set with the guard suppressed so a legitimate load is not fought by the last-transport rule,
        // then notify once. Loading is not a user unticking a checkbox.
        _suppressTransportGuard = true;
        try
        {
            NamedPipeEnabled = document.NamedPipeIsEnabled;
            HttpsEnabled = document.HttpsEnabled;
        }
        finally
        {
            _suppressTransportGuard = false;
        }

        if (AgentHttpsPrefixRules.TrySplit(document.HttpsPrefix, out var host, out var port))
        {
            HttpsHost = host!;
            HttpsPort = port;
        }
        else
        {
            HttpsHost = string.Empty;
            HttpsPort = DefaultHttpsPort;
        }

        SelectedCertificate = document.CertificateThumbprint is { } thumbprint
            ? Certificates.FirstOrDefault(option => string.Equals(
                option.Thumbprint,
                AgentHttpsPrefixRules.NormalizeThumbprint(thumbprint),
                StringComparison.OrdinalIgnoreCase))
            : null;

        OnPropertyChanged(nameof(CanToggleNamedPipe));
        OnPropertyChanged(nameof(CanToggleHttps));
        OnPropertyChanged(nameof(ShowsLastTransportNotice));
        OnPropertyChanged(nameof(ActiveTransportsText));
        OnPropertyChanged(nameof(HttpsPortText));

        RefreshHttpsValidation();
        RefreshDirty();
    }

    /// <summary>
    /// Reads the three system resources for whichever endpoint is currently configured.
    ///
    /// Only when HTTPS is on and the endpoint is valid: describing resources for a half-typed host name
    /// would query a prefix nobody has, and report absent for a binding that exists under the real one.
    /// </summary>
    private void RefreshResourceState()
    {
        if (HttpsEnabled && HttpsIsValid && CertificateThumbprint is { } thumbprint)
        {
            var binding = new AgentHttpsBinding(HttpsHost.Trim(), HttpsPort, thumbprint);
            _appliedBinding = binding;
            _resourceState = _resources.Describe(binding);
            _resourceStateWasQueried = true;
        }
        else if (_appliedBinding is { } previous)
        {
            _resourceState = _resources.Describe(previous);
            _resourceStateWasQueried = true;
        }
        else
        {
            _resourceState = AgentHttpsResourceSnapshot.None;
            _resourceStateWasQueried = false;
        }

        RebuildResourceStatus();
        RebuildDiagnostics();

        // Apply, Reset and every endpoint change arrive here. The endpoint that was just described is
        // the one the probe targets, so the observation is asked for in the same place the target
        // changes rather than repeated at each of the callers.
        RequestListenerRefresh();
    }

    // ---------------------------------------------------------------- listener monitor

    /// <summary>
    /// How often the listener is asked whether it is still there.
    ///
    /// One second, because this is the state an administrator watches change: they stop the service
    /// and look at the row. Anything slower makes the screen look broken, and anything faster buys
    /// nothing a person can perceive while costing a connect attempt per tick.
    /// </summary>
    private static readonly TimeSpan ListenerPollingInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The last thing the endpoint said. Unknown until something is asked, and back to Unknown as
    /// soon as there is nothing to ask - a stale "listening" outliving the service that served it is
    /// the exact failure this whole section exists to remove.
    /// </summary>
    private AgentListenerObservation _listener = AgentListenerObservation.Unknown;

    /// <summary>Cancelled when the window closes. Nothing here survives it.</summary>
    private CancellationTokenSource? _listenerMonitor;

    /// <summary>
    /// One probe at a time, across every path that can start one - the tick, an immediate request and
    /// the startup read all pass through this. A tick that arrives while a probe is still running is
    /// dropped rather than queued: the answer it would produce is the answer already being fetched,
    /// and queueing them is how a slow endpoint turns one probe per second into a backlog.
    /// </summary>
    private readonly SemaphoreSlim _listenerProbeGate = new(1, 1);

    /// <summary>
    /// The wake-up for an immediate refresh, replaced once per cycle.
    ///
    /// A completion source rather than a semaphore because several events can land together - Apply
    /// finishes, the service is re-read, the surface changes back - and this coalesces them into the
    /// single probe they all want, with no waiter left registered for the next one to consume.
    /// </summary>
    private TaskCompletionSource _listenerSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the loop is running. False before the window opens and after it closes.</summary>
    public bool IsListenerMonitorRunning => _listenerMonitor is not null;

    /// <summary>
    /// Starts the one loop this window gets.
    ///
    /// Idempotent on purpose: it is called when the window opens, and calling it again - from a
    /// second Opened, a navigation, a re-bound data context - must not leave two loops probing the
    /// same endpoint on interleaved clocks.
    /// </summary>
    public void StartListenerMonitor()
    {
        if (_listenerMonitor is not null) return;

        var monitor = new CancellationTokenSource();
        _listenerMonitor = monitor;
        _ = MonitorListenerAsync(monitor.Token);
    }

    /// <summary>
    /// Ends it. Called when the window closes, and safe to call when nothing is running.
    ///
    /// The loop is not awaited here because this runs from a Closed handler, which cannot wait. It
    /// does not need to be: every step past an await re-checks the token before it touches this
    /// object, so a probe still in flight completes into a loop that publishes nothing.
    /// </summary>
    public void StopListenerMonitor()
    {
        var monitor = _listenerMonitor;
        _listenerMonitor = null;

        if (monitor is null) return;

        monitor.Cancel();
        monitor.Dispose();
    }

    /// <summary>
    /// Asks for an observation now instead of at the next tick.
    ///
    /// The single entry point every event uses - starting, stopping or restarting the service,
    /// applying, resetting HTTPS, changing the endpoint, coming back to the configuration surface.
    /// It never probes on the calling thread and never blocks: it wakes the loop that already owns
    /// the serialisation, which is what keeps "several things just happened" to one probe.
    /// </summary>
    public void RequestListenerRefresh() => _listenerSignal.TrySetResult();

    private async Task MonitorListenerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var signal = _listenerSignal;

            // Nothing to wait for when a request is already standing - the window has just opened, or
            // several events landed while the last probe was running. Waiting a full period first
            // would make the immediate refresh not immediate.
            if (!signal.Task.IsCompleted)
            {
                // Whichever comes first: the period elapsing, or something asking for an answer now.
                // WhenAny does not throw on a cancelled delay, so the token is checked rather than
                // caught.
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var period = Task.Delay(ListenerPollingInterval, _time, cycle.Token);

                await Task.WhenAny(period, signal.Task).ConfigureAwait(true);

                // A request that won the race leaves a timer still counting down. Cancelling releases
                // it rather than letting one accumulate per event for as long as the window is open,
                // and the delay is then awaited so its cancellation is observed rather than dropped.
                cycle.Cancel();
                await ObserveQuietlyAsync(period).ConfigureAwait(true);
            }

            if (cancellationToken.IsCancellationRequested) return;

            // Arm the next cycle only if this one was woken by a request, and only if no other thread
            // has already replaced it. Everything that arrived before this point has been served.
            if (signal.Task.IsCompleted)
            {
                Interlocked.CompareExchange(
                    ref _listenerSignal,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                    signal);
            }

            await ObserveListenerAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Reads the listener once, and waits for the answer.
    ///
    /// Called by the loop, and awaited by <see cref="RefreshAsync"/> so the window opens showing what
    /// the endpoint actually said rather than opening on a guess and correcting itself.
    /// </summary>
    public async Task RefreshListenerAsync(CancellationToken cancellationToken = default) =>
        await ObserveListenerAsync(cancellationToken).ConfigureAwait(true);

    private async Task ObserveListenerAsync(CancellationToken cancellationToken)
    {
        var target = ListenerProbeTarget;

        if (target is null || _listenerProbe is null)
        {
            // Nothing to ask: HTTPS is off, the service is not running, or the configuration the
            // listener would need is not all there. Each of those is already reported by name in the
            // row itself, and no network call can add to it.
            PublishListener(AgentListenerObservation.Unknown);
            return;
        }

        if (!await _listenerProbeGate.WaitAsync(0, cancellationToken).ConfigureAwait(true)) return;

        try
        {
            var observation = await _listenerProbe.ProbeAsync(target, cancellationToken).ConfigureAwait(true);

            // The window may have closed while the endpoint was deciding whether to answer.
            if (cancellationToken.IsCancellationRequested) return;

            PublishListener(observation);
        }
        catch (OperationCanceledException)
        {
            // Cancelled, not failed. The row keeps whatever it last knew.
        }
        catch (Exception exception)
        {
            // One probe throwing is a fact about this attempt, not the end of the loop: the next tick
            // asks again. An adapter is expected to translate its own failures, so reaching here at
            // all means something unexpected - which is reported rather than swallowed.
            PublishListener(AgentListenerObservation.Unreachable(
                $"{exception.GetType().Name}: {exception.Message}"));
        }
        finally
        {
            _listenerProbeGate.Release();
        }
    }

    /// <summary>
    /// The endpoint worth asking about, or null when the row is decided without asking.
    ///
    /// The same four conditions the row itself checks before it gets as far as the listener, in the
    /// same order, so the monitor never probes for a state that is already reported by something
    /// nearer the cause - and so a stopped service costs no network call at all.
    ///
    /// The endpoint is the one the strip already describes: the validated draft while it is valid,
    /// and the last one described otherwise. Probing anything else would put a second definition of
    /// "the endpoint" on the same screen.
    /// </summary>
    private AgentHttpsBinding? ListenerProbeTarget =>
        HttpsEnabled
        && _serviceState.IsInstalled
        && _serviceState.IsRunning
        && HttpsIsValid
        && _resourceState.IsFullyConfigured
            ? _appliedBinding
            : null;

    /// <summary>Awaits a task purely so that its cancellation is not left unobserved.</summary>
    private static async Task ObserveQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Records an observation, and redraws only when it actually changed.
    ///
    /// A listener that stays up for twenty minutes is twelve hundred identical answers, and rebuilding
    /// the strip for each of them would repopulate an observable collection once a second under an
    /// operator's cursor. The record compares by value, so an unchanged answer costs nothing and the
    /// row does not flicker between identical states.
    /// </summary>
    private void PublishListener(AgentListenerObservation observation)
    {
        if (_listener == observation) return;

        _listener = observation;
        RebuildResourceStatus();
        RebuildDiagnostics();
    }

    // ---------------------------------------------------------------- status and diagnostics

    public ObservableCollection<AgentStatusItemViewModel> ResourceStatus { get; } = [];

    public ObservableCollection<AgentStatusItemViewModel> Diagnostics { get; } = [];

    private void RebuildResourceStatus()
    {
        ResourceStatus.Clear();

        if (!HttpsEnabled)
        {
            ResourceStatus.Add(DisabledResource(Strings["Resources.SslBinding"], "NutIconTls"));
            ResourceStatus.Add(DisabledResource(Strings["Resources.UrlReservation"], "NutIconRemote"));
            ResourceStatus.Add(DisabledResource(Strings["Resources.Firewall"], "NutIconShield"));
            ResourceStatus.Add(DisabledResource(Strings["Resources.Listener"], "NutIconConnection"));
            return;
        }

        if (!_resourceStateWasQueried)
        {
            // HTTPS is on but the draft does not yet name an endpoint, so nothing has been asked of
            // Windows. Saying "not configured" here would be a claim about the machine this window
            // has not made - and on a server that already had a foreign binding on the port, the
            // claim was wrong.
            ResourceStatus.Add(UncheckedResource(Strings["Resources.SslBinding"], "NutIconTls"));
            ResourceStatus.Add(UncheckedResource(Strings["Resources.UrlReservation"], "NutIconRemote"));
            ResourceStatus.Add(UncheckedResource(
                Strings.Format("Resources.Firewall.Port", HttpsPort), "NutIconShield"));
            ResourceStatus.Add(UncheckedResource(Strings["Resources.Listener"], "NutIconConnection"));
            return;
        }

        ResourceStatus.Add(Describe(
            Strings["Resources.SslBinding"], _resourceState.SslBinding, "NutIconTls", AgentResourceGender.Masculine));
        ResourceStatus.Add(Describe(
            Strings["Resources.UrlReservation"], _resourceState.UrlReservation, "NutIconRemote", AgentResourceGender.Feminine));
        // The firewall row names its port when there is one to name, as the reference does.
        var firewall = HttpsEnabled
            ? Strings.Format("Resources.Firewall.Port", HttpsPort)
            : Strings["Resources.Firewall"];
        ResourceStatus.Add(Describe(
            firewall, _resourceState.FirewallRule, "NutIconShield", AgentResourceGender.Masculine, isRule: true));
        ResourceStatus.Add(DescribeListener());
    }

    /// <summary>
    /// Present, but reporting that nothing has been asked rather than that nothing is there.
    ///
    /// NotConfigured rather than Error: an incomplete draft is a normal state on the way to a
    /// complete one, and the tooltip says what would make the query happen.
    /// </summary>
    private AgentStatusItemViewModel UncheckedResource(string label, string iconKey) =>
        AgentStatusItemViewModel.From(
            Strings,
            label,
            AgentDiagnosticState.NotConfigured,
            Strings["Resources.State.NotChecked"],
            iconKey,
            Strings["Resources.NotChecked.Detail"]);

    private AgentStatusItemViewModel DisabledResource(string label, string iconKey) =>
        AgentStatusItemViewModel.From(
            Strings, label, AgentDiagnosticState.NotConfigured, Strings["Resources.State.HttpsDisabled"], iconKey);

    /// <summary>
    /// Which way the Portuguese adjective has to agree.
    ///
    /// "SSL Binding configurado" and "URL Reservation configurada" are the same state and not the
    /// same word. English has no agreement and maps both forms to "Configured", so this costs nothing
    /// there - but leaving it out would put a grammatical error on the Portuguese screen, which is
    /// the language this product is primarily read in.
    /// </summary>
    private enum AgentResourceGender
    {
        Masculine,
        Feminine,
    }

    /// <summary>
    /// Whether the HTTPS listener is actually up.
    ///
    /// The four conditions below are the ones that make the question answerable without asking: HTTPS
    /// off, service absent, service stopped and configuration incomplete each explain the row by
    /// themselves, and each sends an administrator somewhere different. Past them, the answer is
    /// observed rather than composed.
    ///
    /// It used to be composed all the way through - enabled, valid, owned and running was reported as
    /// listening - and that is wrong on the machine that matters most: HTTP.sys can refuse the prefix
    /// while the service sits in Running, and the row then showed a green light for an endpoint
    /// nothing could reach. A running service is not a running listener, so the last step asks.
    ///
    /// What it does not claim: that the answer came from NutManager rather than from something else
    /// on the port, and that any client has authenticated. Ownership is the three rows above this one,
    /// and authentication is not a resource at all.
    /// </summary>
    private AgentStatusItemViewModel DescribeListener()
    {
        const string icon = "NutIconConnection";
        var label = Strings["Resources.Listener"];

        if (!HttpsEnabled)
        {
            return AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.NotConfigured, Strings["Resources.State.HttpsDisabled"], icon);
        }

        // Below, the short phrase is the column and the precise sentence is the tooltip. The four
        // cases stay four cases: which one an administrator is in still decides where they go next.
        if (!_serviceState.IsInstalled)
        {
            // Not installed is not stopped. Reporting a service that does not exist as merely stopped
            // sends an administrator to the wrong place, and it is exactly the conflation the
            // diagnostics rules forbid.
            return AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.Error, Strings["Resources.State.Listener.Unavailable"], icon,
                Strings["Resources.Listener.ServiceMissing"]);
        }

        if (!_serviceState.IsRunning)
        {
            // The configuration can be perfect and nothing is listening, because the service is not
            // running. Saying "ready" here would be the most misleading line on the screen.
            return AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.Attention, Strings["Resources.State.Listener.Unavailable"], icon,
                Strings["Resources.Listener.ServiceStopped"]);
        }

        if (!HttpsIsValid || !_resourceState.IsFullyConfigured)
        {
            return AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.Attention, Strings["Resources.State.Listener.Incomplete"], icon,
                Strings["Resources.Listener.Incomplete"]);
        }

        return _listener.State switch
        {
            AgentListenerReachability.Listening => AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.Ready, Strings["Resources.State.Listener.Active"], icon,
                Strings.Format("Resources.Listener.Listening", HttpsEndpoint)),

            // Everything configured, the service running, and nothing answering. Reported exactly as
            // it is: the three rows above stay green because they are still correct, and this one
            // does not borrow their correctness. The socket error, when there is one, is the tooltip.
            AgentListenerReachability.Unreachable => AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.Attention, Strings["Resources.State.Listener.Unavailable"], icon,
                Join(Strings.Format("Resources.Listener.NotAnswering", HttpsEndpoint), _listener.Detail)),

            // The first observation has not come back yet. Claiming either answer here would be a
            // guess, and the one that guesses "listening" is the one that shows a green light for a
            // dead endpoint.
            _ => AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.NotConfigured, Strings["Resources.State.Listener.Checking"], icon,
                Strings["Resources.Listener.Checking"]),
        };
    }

    /// <summary>The localized sentence, followed by the adapter technical note when there is one.</summary>
    private static string Join(string sentence, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? sentence : $"{sentence} {detail}";

    /// <summary>
    /// One status column.
    ///
    /// The classification is untouched - ownership still decides the state, and this method gets no
    /// vote in it. What changed is what reaches the card: a short phrase built from the ownership and
    /// localised here, instead of the adapter sentence. That sentence is written in English by
    /// infrastructure and names an AppId or a rule; inline it made the four columns wildly different
    /// heights and put English inside the Portuguese window. It moves to the tooltip intact.
    /// </summary>
    private AgentStatusItemViewModel Describe(
        string label,
        AgentResourceState state,
        string iconKey,
        AgentResourceGender gender,
        bool isRule = false)
    {
        var diagnostic = state.Ownership switch
        {
            AgentResourceOwnership.OwnedByNutManager => AgentDiagnosticState.Ready,
            // Present but owned elsewhere: not an error in NutManager, and not something to fix by
            // deleting it. Attention, with the tooltip saying who owns it.
            AgentResourceOwnership.ForeignOwner => AgentDiagnosticState.Attention,
            AgentResourceOwnership.Absent => HttpsEnabled ? AgentDiagnosticState.Error : AgentDiagnosticState.NotConfigured,
            _ => AgentDiagnosticState.Error,
        };

        var summary = state.Ownership switch
        {
            AgentResourceOwnership.OwnedByNutManager => Configured(gender),
            // A firewall rule that is not ours is most often one an administrator or an older install
            // left behind, so it is named as what it is rather than as a rival application.
            AgentResourceOwnership.ForeignOwner => isRule
                ? Strings["Resources.State.UnmanagedRule"]
                : Strings["Resources.State.Foreign"],
            AgentResourceOwnership.Absent => NotConfigured(gender),
            AgentResourceOwnership.Unknown => Strings["Resources.State.Unknown"],
            _ => Strings["Resources.State.Error"],
        };

        return AgentStatusItemViewModel.From(Strings, label, diagnostic, summary, iconKey, state.Detail);
    }

    private string Configured(AgentResourceGender gender) => gender == AgentResourceGender.Feminine
        ? Strings["Resources.State.ConfiguredFeminine"]
        : Strings["Resources.State.Configured"];

    private string NotConfigured(AgentResourceGender gender) => gender == AgentResourceGender.Feminine
        ? Strings["Resources.State.NotConfiguredFeminine"]
        : Strings["Resources.State.NotConfigured"];

    /// <summary>
    /// The diagnostics list.
    ///
    /// Each line is one fact and they are never merged: the agent being installed is not the agent
    /// running, NUT being installed is not NUT running, and a transport being enabled is not a client
    /// having authenticated over it. A screen that collapsed those would be easier to read and would
    /// answer the wrong question.
    /// </summary>
    // ---------------------------------------------------------------- startup preference

    /// <summary>
    /// Whether Windows starts the agent by itself.
    ///
    /// The service control manager is the only place this lives. Writing it into agent.json as well
    /// would create a second answer that nothing reconciles: an operator can change the start type in
    /// services.msc, and the copy here would be wrong from that moment on without ever saying so. It
    /// is read back from the machine after every change for the same reason.
    ///
    /// Applied immediately rather than collected into Apply. Apply writes the transport document, and
    /// a service start type is not part of that document.
    /// </summary>
    [ObservableProperty]
    private bool _startsWithWindows;

    /// <summary>True while the switch is being set from the machine, so echoing a fact back is not a change.</summary>
    private bool _syncingStartup;

    /// <summary>
    /// The last thing the startup switch did.
    ///
    /// Deliberately empty until something happens. It reports an action, and the standing state is
    /// already shown by the switch itself - a line that said "the service will start with Windows"
    /// every time the tab opened would be a second, wordier copy of the control above it.
    /// </summary>
    [ObservableProperty]
    private string? _startupResultText;

    /// <summary>
    /// Which of the three it was, so the line can carry the right glyph.
    ///
    /// Manual is a warning rather than an error: the operator asked for it, and it succeeded. What it
    /// deserves is the raised eyebrow of a consequence, not the red of a fault.
    /// </summary>
    [ObservableProperty]
    private AgentSettingsFeedback _startupResultKind = AgentSettingsFeedback.None;

    public bool HasStartupResult => StartupResultKind is not AgentSettingsFeedback.None;

    partial void OnStartupResultKindChanged(AgentSettingsFeedback value) =>
        OnPropertyChanged(nameof(HasStartupResult));

    /// <summary>
    /// The switch was moved. Off is asked about first; on is applied as asked.
    ///
    /// Turning automatic start off means a machine that reboots comes back without its agent, and
    /// nothing on screen would have said so afterwards. Turning it on takes nothing away, so asking
    /// would be a dialog for the sake of having one.
    /// </summary>
    partial void OnStartsWithWindowsChanged(bool value)
    {
        if (_syncingStartup) return;

        if (!value && _serviceState.StartType == AgentServiceStartType.Automatic)
        {
            // Put back to what the machine still says while the question is open. The switch must
            // never sit in a position the service control manager has not agreed to.
            SyncStartupFromService();
            PendingConfirmation = AgentConfigConfirmation.ManualStartup;
            return;
        }

        _ = ApplyStartupPreferenceAsync(value);
    }

    /// <summary>
    /// There is a start type to change only when there is a service, and only when nothing else is
    /// already in flight against it.
    /// </summary>
    public bool CanChangeStartup => _serviceState.IsInstalled && !IsBusy;

    /// <summary>Why the switch is unavailable, said plainly rather than left to a disabled control.</summary>
    public string? StartupBlockedReason =>
        _serviceState.IsInstalled ? null : Strings["Settings.Startup.NotInstalled"];

    /// <summary>
    /// Whether to offer the way to the thing that would make the switch usable.
    ///
    /// A disabled control with a tooltip explains the problem to whoever hovers it. This offers the
    /// answer instead, beside the control that cannot be used, and it exists only while that is true.
    /// </summary>
    public bool ShowStartupHelp => !_serviceState.IsInstalled;

    /// <summary>
    /// Goes to the panel that can install the service. Navigation and nothing else - it opens a tab
    /// in this same window, touches no machine state, and asks nothing of the operator on the way.
    /// </summary>
    [RelayCommand]
    private void ShowServiceInstallation() => SettingsTab = AgentSettingsTab.Agent;

    /// <summary>Forgets the last action, so a result never outlives the panel it belongs to.</summary>
    private void ClearStartupResult()
    {
        StartupResultText = null;
        StartupResultKind = AgentSettingsFeedback.None;
    }

    private void SyncStartupFromService()
    {
        _syncingStartup = true;
        try
        {
            StartsWithWindows = _serviceState.StartType == AgentServiceStartType.Automatic;
        }
        finally
        {
            _syncingStartup = false;
        }

        OnPropertyChanged(nameof(CanChangeStartup));
        OnPropertyChanged(nameof(StartupBlockedReason));
        OnPropertyChanged(nameof(ShowStartupHelp));
        OnPropertyChanged(nameof(CanInstallService));
        OnPropertyChanged(nameof(ServiceInstallDescription));
        InstallServiceCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Automatic or Manual, and never Disabled.
    ///
    /// Turning a boot preference off must not take away the operator's ability to start the agent by
    /// hand, and Disabled is precisely that. Nothing is started or stopped here either: this changes
    /// what Windows does at the next boot, and an operator changing a boot preference has not asked
    /// for anything to happen to the running service right now.
    /// </summary>
    private async Task ApplyStartupPreferenceAsync(bool automatic)
    {
        if (!CanChangeStartup)
        {
            SyncStartupFromService();
            return;
        }

        var preference = automatic
            ? AgentServiceStartupPreference.Automatic
            : AgentServiceStartupPreference.Manual;

        IsBusy = true;
        try
        {
            var outcome = await _service.SetStartupAsync(preference, CancellationToken.None).ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                StartupResultKind = automatic
                    ? AgentSettingsFeedback.Success
                    : AgentSettingsFeedback.Warning;
                StartupResultText =
                    Strings[automatic ? "Settings.Startup.Automatic.Done" : "Settings.Startup.Manual.Done"];
            }
            else
            {
                StartupResultKind = AgentSettingsFeedback.Error;
                StartupResultText = outcome.Failure ?? Strings["Settings.Startup.Failed"];
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StartupResultKind = AgentSettingsFeedback.Error;
            StartupResultText = Strings["Settings.Startup.Failed"];
        }
        finally
        {
            IsBusy = false;

            // Whatever happened, the switch is set from what the machine now reports rather than from
            // what was asked for. A refused change must not leave the control claiming it succeeded.
            ReloadService();
        }
    }

    // ---------------------------------------------------------------- service installation

    /// <summary>
    /// Whether there is a service to register.
    ///
    /// Read from the service control manager rather than remembered. A preference recording that this
    /// window once installed the service would keep claiming so after somebody removed it, and the
    /// button would then offer an action the machine has already refused.
    /// </summary>
    public bool CanInstallService => !_serviceState.IsInstalled && !IsBusy;

    [ObservableProperty]
    private string? _installResultText;

    [ObservableProperty]
    private AgentSettingsFeedback _installResultKind = AgentSettingsFeedback.None;

    public bool HasInstallResult => InstallResultKind is not AgentSettingsFeedback.None;

    partial void OnInstallResultKindChanged(AgentSettingsFeedback value) =>
        OnPropertyChanged(nameof(HasInstallResult));

    private void ClearInstallResult()
    {
        InstallResultText = null;
        InstallResultKind = AgentSettingsFeedback.None;
    }

    /// <summary>
    /// The sentence above the button, which changes with what the machine has.
    ///
    /// The section stays on screen either way. An install control that disappeared once it had been
    /// used would leave an operator wondering whether they imagined it, and the answer to "is this
    /// registered" is worth keeping visible.
    /// </summary>
    public string ServiceInstallDescription => _serviceState.IsInstalled
        ? Strings["Settings.Agent.Install.Already"]
        : Strings["Settings.Agent.Install.Description"];

    /// <summary>
    /// Registers the service. It does not start it.
    ///
    /// Installing and running are separate decisions, and this makes only the first. The service is
    /// created stopped, and the Start control on the configuration surface is where somebody says it
    /// should run - the same separation the product installer keeps.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallService))]
    private async Task InstallServiceAsync(CancellationToken cancellationToken)
    {
        // Defence in depth behind the disabled button. ICommand.Execute does not consult CanExecute,
        // so a keyboard binding, a test or a later refactor could reach this with a service already
        // registered - and registering is not something to attempt and then apologise for.
        if (!CanInstallService) return;

        ClearInstallResult();

        IsBusy = true;
        RefreshCommandStates();

        try
        {
            var result = await _service.InstallAsync(cancellationToken).ConfigureAwait(true);

            (InstallResultKind, InstallResultText) = result.Outcome switch
            {
                AgentServiceInstallOutcome.Installed =>
                    (AgentSettingsFeedback.Success, Strings["Settings.Agent.Install.Done"]),

                // Somebody else got there first. Reported as the state it leaves behind rather than as
                // a failure, and nothing is done to the service that is already registered.
                AgentServiceInstallOutcome.AlreadyInstalled =>
                    (AgentSettingsFeedback.Warning, Strings["Settings.Agent.Install.Already"]),

                _ => (AgentSettingsFeedback.Error, Strings["Settings.Agent.Install.Failed"]),
            };

            // The Win32 detail belongs in diagnostics, not in a sentence under a button.
            _installFailure = !result.Succeeded && !string.IsNullOrWhiteSpace(result.Failure)
                ? result.Failure
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            InstallResultKind = AgentSettingsFeedback.Error;
            InstallResultText = Strings["Settings.Agent.Install.Failed"];
            _installFailure = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();

            // Read back from the machine, so the button and the rows below it report what is actually
            // registered rather than what was asked for.
            ReloadService();
        }
    }

    /// <summary>The last registration failure, kept for the diagnostics list rather than the card.</summary>
    private string? _installFailure;

    // ---------------------------------------------------------------- what the machine reports

    /// <summary>
    /// The start type in words, from the typed value rather than the raw string Windows returned, so
    /// the two cultures say it rather than echoing an English WMI token.
    /// </summary>
    public string ServiceStartTypeText => _serviceState.StartType switch
    {
        AgentServiceStartType.Boot => Strings["Service.StartType.Boot"],
        AgentServiceStartType.System => Strings["Service.StartType.System"],
        AgentServiceStartType.Automatic => Strings["Service.StartType.Automatic"],
        AgentServiceStartType.Manual => Strings["Service.StartType.Manual"],
        AgentServiceStartType.Disabled => Strings["Service.StartType.Disabled"],
        _ => Strings["About.Unknown"],
    };

    /// <summary>
    /// The account the service runs as. An account name is not a credential and nothing here reads,
    /// stores or displays a password.
    /// </summary>
    public string ServiceAccountText =>
        string.IsNullOrWhiteSpace(_serviceState.Account) ? Strings["About.Unknown"] : _serviceState.Account;

    /// <summary>
    /// The transports that are on, named the way the transport card names them, from the edited state
    /// rather than the file - this reports what the window would save, not a second reading.
    /// </summary>
    public string ActiveTransportsText
    {
        get
        {
            var active = new List<string>(2);
            if (NamedPipeEnabled) active.Add(Strings["Transport.NamedPipe"]);
            if (HttpsEnabled) active.Add(Strings["Transport.Https"]);
            return active.Count == 0 ? Strings["Settings.Agent.None"] : string.Join(", ", active);
        }
    }

    /// <summary>The port, or nothing at all when the transport that uses it is off.</summary>
    public string HttpsPortText => HttpsEnabled
        ? HttpsPort.ToString(CultureInfo.InvariantCulture)
        : Strings["Settings.Agent.None"];

    // ---------------------------------------------------------------- about

    /// <summary>The product version, from the assembly that is actually running.</summary>
    public string AboutVersion =>
        typeof(AgentConfigViewModel).Assembly.GetName().Version?.ToString(3) ?? Strings["About.Unknown"];

    /// <summary>
    /// The build, which is the commit the version was stamped from rather than the version again.
    ///
    /// An informational version reads "1.0.1+bd49437e6f63249..." - correct, and forty characters of
    /// it are a number nobody reads at that length. The version is already on the line above, so what
    /// is useful here is the part after the plus: short enough to take in at a glance, long enough to
    /// find. A build with no commit metadata shows the version it does have rather than nothing.
    /// </summary>
    public string AboutBuild => ShortenBuild(
        typeof(AgentConfigViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion,
        AboutVersion);

    /// <summary>
    /// The commit out of an informational version, abbreviated. Separated from the property so the
    /// policy can be exercised against strings this build does not happen to produce.
    /// </summary>
    internal static string ShortenBuild(string? informational, string fallback)
    {
        if (string.IsNullOrWhiteSpace(informational)) return fallback;

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0 || plus == informational.Length - 1) return informational;

        var metadata = informational[(plus + 1)..];
        return metadata.Length <= ShortCommitLength ? metadata : metadata[..ShortCommitLength];
    }

    /// <summary>Seven characters, which is what Git itself abbreviates to.</summary>
    private const int ShortCommitLength = 7;

    /// <summary>Read from the same inventory the diagnostics list reports, so the two cannot disagree.</summary>
    public string AboutDotNetRuntime => _machine.DotNetRuntimeVersion ?? Strings["About.Unknown"];

    public string AboutAspNetCoreRuntime => _machine.AspNetCoreRuntimeVersion ?? Strings["About.Unknown"];

    public string AboutDeveloper => "Marcelo Pacheco";

    /// <summary>
    /// The address the link opens, shown as text so the target is visible before it is followed and
    /// still reachable on a machine with no browser installed.
    /// </summary>
    public string AboutProjectPageUrl => _projectPage?.ProjectPageUrl ?? string.Empty;

    public bool CanOpenProjectPage => _projectPage is not null;

    [ObservableProperty]
    private bool _projectPageFailed;

    /// <summary>
    /// Opens the product's own project page. No parameter, here or in the contract: there is one
    /// address, it is a constant in the launcher, and this command cannot name another.
    /// </summary>
    [RelayCommand]
    private void OpenProjectPage()
    {
        if (_projectPage is null) return;

        ProjectPageFailed = !_projectPage.OpenProjectPage();
    }

    /// <summary>
    /// Whether the service control manager answered when asked how the service is configured.
    ///
    /// Three outcomes, kept apart on purpose. A service that is not installed has no configuration to
    /// read and is not a failure; a configuration that was read is reported with its values; and a
    /// read that failed says so and carries the Win32 code, which is the difference between "access
    /// denied" and "the service is gone" and the only thing that tells an administrator which it was.
    ///
    /// This exists because the Agent panel showed "Unknown" for the start mode and the account with
    /// nothing anywhere to say why. Unknown is the honest answer there - and the reason belongs
    /// somewhere, which is here.
    /// </summary>
    private AgentStatusItemViewModel DescribeServiceConfiguration()
    {
        var label = Strings["Diagnostics.ServiceConfiguration"];

        if (!_serviceState.IsInstalled)
        {
            return AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.NotConfigured, Strings["Diagnostics.NotInstalled"]);
        }

        if (ServiceConfigurationWasRead)
        {
            return AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.Ready,
                $"{ServiceStartTypeText} · {ServiceAccountText}",
                technicalDetail: _serviceState.Failure);
        }

        return AgentStatusItemViewModel.From(
            Strings, label, AgentDiagnosticState.Attention,
            Strings["Diagnostics.ServiceConfiguration.Failed"],
            technicalDetail: _serviceState.Failure ?? Strings["Diagnostics.ServiceConfiguration.NoDetail"]);
    }

    /// <summary>
    /// Whether the configuration query produced values rather than the absence of them.
    ///
    /// Both fields, because the query fills both or neither: an unknown start type beside a named
    /// account would mean something stranger than a failed read, and reporting either half as a
    /// success would be the guess this window exists to avoid.
    /// </summary>
    private bool ServiceConfigurationWasRead =>
        _serviceState.StartType is not AgentServiceStartType.Unknown
        && !string.IsNullOrWhiteSpace(_serviceState.Account);

    private void RebuildDiagnostics()
    {
        Diagnostics.Clear();

        Diagnostics.Add(AgentStatusItemViewModel.From(
            Strings, Strings["Diagnostics.DotNet"],
            _machine.DotNetRuntimeVersion is null ? AgentDiagnosticState.Error : AgentDiagnosticState.Ready,
            _machine.DotNetRuntimeVersion ?? Strings["Diagnostics.NotInstalled"]));

        Diagnostics.Add(AgentStatusItemViewModel.From(
            Strings, Strings["Diagnostics.AspNetCore"],
            _machine.AspNetCoreRuntimeVersion is null ? AgentDiagnosticState.Error : AgentDiagnosticState.Ready,
            _machine.AspNetCoreRuntimeVersion ?? Strings["Diagnostics.NotInstalled"]));

        Diagnostics.Add(AgentStatusItemViewModel.From(
            Strings, Strings["Diagnostics.AgentRegistered"],
            !_serviceState.IsInstalled
                ? AgentDiagnosticState.Error
                : string.IsNullOrWhiteSpace(_serviceState.Failure)
                    ? AgentDiagnosticState.Ready
                    : AgentDiagnosticState.Attention,
            _serviceState.IsInstalled ? ServiceStateText : Strings["Diagnostics.NotInstalled"],
            technicalDetail: _serviceState.Failure));

        Diagnostics.Add(DescribeServiceConfiguration());

        if (_installFailure is { } installFailure)
        {
            // Only after an attempt, and only when it failed. A registration that worked is reported
            // by the service rows above, which now describe a service that exists.
            Diagnostics.Add(AgentStatusItemViewModel.From(
                Strings, Strings["Diagnostics.ServiceInstall"],
                AgentDiagnosticState.Error,
                Strings["Diagnostics.ServiceInstall.Failed"],
                technicalDetail: installFailure));
        }

        Diagnostics.Add(AgentStatusItemViewModel.From(
            Strings, Strings["Diagnostics.Nut"],
            _machine.NutDetected ? AgentDiagnosticState.Ready : AgentDiagnosticState.Attention,
            _machine.NutDetail ?? (_machine.NutDetected ? null : Strings["Diagnostics.NotDetected"])));

        Diagnostics.Add(AgentStatusItemViewModel.From(
            Strings, Strings["Diagnostics.Operators"],
            _groupState.Exists ? AgentDiagnosticState.Ready : AgentDiagnosticState.Attention,
            _groupState.Exists ? _groupState.Sid : Strings["Diagnostics.Missing"]));

        Diagnostics.Add(AgentStatusItemViewModel.From(
            Strings, Strings["Diagnostics.EventLog"],
            _machine.EventLogSourceRegistered ? AgentDiagnosticState.Ready : AgentDiagnosticState.Error,
            _machine.EventLogSourceRegistered ? Strings["Diagnostics.Present"] : Strings["Diagnostics.Missing"]));

        Diagnostics.Add(AgentStatusItemViewModel.From(
            Strings, Strings["Diagnostics.NamedPipe"],
            NamedPipeEnabled ? AgentDiagnosticState.Ready : AgentDiagnosticState.NotConfigured,
            NamedPipeEnabled ? Strings["Diagnostics.Enabled"] : Strings["Diagnostics.Disabled"]));

        Diagnostics.Add(AgentStatusItemViewModel.From(
            Strings, Strings["Diagnostics.Https"],
            HttpsEnabled
                ? (HttpsIsValid ? AgentDiagnosticState.Ready : AgentDiagnosticState.Attention)
                : AgentDiagnosticState.NotConfigured,
            HttpsEnabled ? HttpsEndpoint : Strings["Diagnostics.Disabled"]));

        if (!HttpsEnabled) return;

        Diagnostics.Add(AgentStatusItemViewModel.From(
            Strings, Strings["Diagnostics.Certificate"],
            HttpsIsValid ? AgentDiagnosticState.Ready : AgentDiagnosticState.Attention,
            SelectedCertificate?.DisplayName ?? Strings["Https.Certificate.None"]));

        foreach (var resource in ResourceStatus) Diagnostics.Add(resource);
    }
}
