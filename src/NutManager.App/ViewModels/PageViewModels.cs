using System.Globalization;
using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using NutManager.App.Services;
using NutManager.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NutManager.Core.Models;
using NutManager.Core.Services;
using NutManager.Core.Status;

namespace NutManager.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }

    public string Title { get; }

    public string Description { get; }

    /// <summary>
    /// Called as the shell navigates away from this page.
    ///
    /// For discarding presentation state that belongs to the visit rather than to the product: an
    /// unanswered confirmation, a success banner for something already finished. Both survive
    /// navigation otherwise and reappear later out of context — a destructive confirmation that comes
    /// back is worse than a wasted click, because the operator is being asked again about something
    /// they walked away from.
    ///
    /// It must not cancel work already in progress, close a session, or discard anything the user
    /// would have to redo. Unsaved drafts are not presentation state and stay exactly where they are.
    /// </summary>
    public virtual void OnDeactivated()
    {
    }
}
public sealed partial class OverviewPageViewModel : PageViewModel
{
    private readonly INutClient? _nutClient;
    private readonly NutEndpoint? _endpoint;
    private readonly string? _upsName;
    private readonly IUpsPollingCoordinator? _polling;

    public OverviewPageViewModel()
        : this(UiLanguagePreference.PtBr)
    {
    }

    public OverviewPageViewModel(UiLanguagePreference language)
        : base(new NutManagerLocalizer(language).Get("Overview.Title"), new NutManagerLocalizer(language).Get("Overview.Description"))
    {
        Strings = new NutManagerLocalizer(language);
        _connectionState = ConnectionState.Disconnected;
        _dataFreshness = DataFreshness.Unavailable;
        _metricCards = CreateMetricCards(null);
        _statusItems = Array.Empty<OverviewStatusItemViewModel>();
    }

    public OverviewPageViewModel(
        INutClient nutClient,
        NutEndpoint endpoint,
        string upsName,
        ConnectionState connectionState,
        DataFreshness dataFreshness)
        : this(nutClient, endpoint, upsName, connectionState, dataFreshness, UiLanguagePreference.PtBr)
    {
    }

    public OverviewPageViewModel(
        INutClient nutClient,
        NutEndpoint endpoint,
        string upsName,
        ConnectionState connectionState,
        DataFreshness dataFreshness,
        UiLanguagePreference language)
        : base(new NutManagerLocalizer(language).Get("Overview.Title"), new NutManagerLocalizer(language).Get("Overview.Description"))
    {
        ArgumentNullException.ThrowIfNull(nutClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(upsName);

        Strings = new NutManagerLocalizer(language);
        _nutClient = nutClient;
        _endpoint = endpoint;
        _upsName = upsName;
        _connectionState = connectionState;
        _dataFreshness = dataFreshness;
        _metricCards = CreateMetricCards(null);
        _statusItems = Array.Empty<OverviewStatusItemViewModel>();
    }

    /// <summary>
    /// The endpoint is passed separately because the polling state does not carry it: the
    /// coordinator reports readings, not where they came from. Without it the connection card
    /// reported the server address as unavailable while the shell header was showing that very
    /// address, which is a wiring gap rather than a missing NUT variable.
    /// </summary>
    public OverviewPageViewModel(
        IUpsPollingCoordinator polling,
        UiLanguagePreference language = UiLanguagePreference.PtBr,
        NutEndpoint? endpoint = null)
        : this(language)
    {
        _polling = polling;
        _endpoint = endpoint;
        polling.StateChanged += ApplyPollingState;
        ApplyPollingState(polling.State);
    }

    public NutManagerLocalizer Strings { get; }

    private void ApplyPollingState(PollingState state)
    {
        Snapshot = state.Snapshot;
        ConnectionState = state.ConnectionState;
        DataFreshness = state.DataFreshness;
        LoadError = state.LastError;
        StatusItems = state.Snapshot?.StatusTokens.Select(CreateStatusItem).ToArray() ?? Array.Empty<OverviewStatusItemViewModel>();
        MetricCards = CreateMetricCards(state.Snapshot);
    }

    [ObservableProperty]
    private UpsSnapshot? _snapshot;

    [ObservableProperty]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private DataFreshness _dataFreshness;

    [ObservableProperty]
    private IReadOnlyList<OverviewMetricViewModel> _metricCards;

    [ObservableProperty]
    private IReadOnlyList<OverviewStatusItemViewModel> _statusItems;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _loadError;

    public UpsIdentity? Identity => Snapshot?.Identity;

    public string SourceLabel => Snapshot?.Source == DataSource.Simulated ? Strings.Get("Shell.SimulationActive") : string.Empty;

    public bool IsSimulated => Snapshot?.Source == DataSource.Simulated;

    public bool HasNoStatusItems => StatusItems.Count == 0;

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);

    public string ConnectionStateText => ConnectionState switch
    {
        ConnectionState.Disconnected => Strings.Get("Status.Disconnected"),
        ConnectionState.Connecting => Strings.Get("Status.Connecting"),
        ConnectionState.Connected => Strings.Get("Status.Connected"),
        ConnectionState.Reconnecting => Strings.Get("Status.Reconnecting"),
        ConnectionState.ConnectionFailed => Strings.Get("Status.ConnectionFailed"),
        _ => Strings.Get("Status.Unavailable")
    };

    public string DataFreshnessText => DataFreshness switch
    {
        DataFreshness.Unavailable => Strings.Get("Status.Unavailable"),
        DataFreshness.Fresh => Strings.Get("Status.Fresh"),
        DataFreshness.Stale => Strings.Get("Status.Stale"),
        _ => Strings.Get("Status.Unavailable")
    };

    public string LastSuccessfulUpdateText => Snapshot is null
        ? Strings.Get("Status.Unavailable")
        : NutTimestampPresentation.Local(Snapshot.LastSuccessfulUpdate, "g");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_nutClient is null || _endpoint is null || _upsName is null)
        {
            return;
        }

        IsLoading = true;
        LoadError = null;

        try
        {
            Snapshot = await _nutClient.GetSnapshotAsync(_endpoint, _upsName, cancellationToken);
            StatusItems = Snapshot.StatusTokens.Select(CreateStatusItem).ToArray();
            MetricCards = CreateMetricCards(Snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            LoadError = Strings.Get("Overview.LoadError");
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSnapshotChanged(UpsSnapshot? value)
    {
        OnPropertyChanged(nameof(Identity));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(IsSimulated));
        OnPropertyChanged(nameof(LastSuccessfulUpdateText));
        NotifyDashboardChanged();
    }

    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnectionPending));
        OnPropertyChanged(nameof(IsConnectionCritical));
    }

    partial void OnDataFreshnessChanged(DataFreshness value) =>
        OnPropertyChanged(nameof(DataFreshnessText));

    partial void OnLoadErrorChanged(string? value) =>
        OnPropertyChanged(nameof(HasLoadError));

    partial void OnStatusItemsChanged(IReadOnlyList<OverviewStatusItemViewModel> value)
    {
        OnPropertyChanged(nameof(HasNoStatusItems));
        NotifyDashboardChanged();
    }

    // ==================== Dashboard presentation (T27A) ====================
    // Every reading below is projected straight from the current snapshot. A missing NUT variable
    // stays missing: the card keeps its composition and shows the unavailable label instead of a
    // substituted or remembered value.

    private string? Variable(string name) =>
        Snapshot?.Variables.TryGetValue(name, out var variable) == true && !string.IsNullOrWhiteSpace(variable.Value)
            ? variable.Value
            : null;

    private string UnavailableText => Strings.Get("Status.Unavailable");

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    public double? BatteryPercent => Snapshot?.BatteryChargePercentage is { } value ? (double)value : null;

    public bool HasBatteryPercent => BatteryPercent is not null;

    public double BatteryBarValue => BatteryPercent ?? 0d;

    public string BatteryValueText => Snapshot?.BatteryChargePercentage is { } value ? $"{Number(value)}%" : UnavailableText;

    public string BatterySeverityClass => BatteryPercent switch
    {
        null => "unavailable",
        < 20 => "critical",
        < 50 => "warning",
        _ => "healthy"
    };

    public string? BatteryVoltageText => Snapshot?.BatteryVoltage is { } value ? $"{Number(value)} V" : null;

    public bool HasBatteryVoltage => Snapshot?.BatteryVoltage is not null;

    /// <summary>
    /// Battery condition reported by a real NUT status token. NUT does not publish a "charged"
    /// state, so when no battery-related token is present this stays null and the row is hidden
    /// rather than showing an unavailable label beside an available charge reading.
    /// </summary>
    private string? BatteryStateToken =>
        StatusTokens(StatusSemanticState.Charging) ??
        StatusTokens(StatusSemanticState.Discharging) ??
        StatusTokens(StatusSemanticState.LowBattery) ??
        StatusTokens(StatusSemanticState.ReplaceBattery);

    public string BatteryStateText => BatteryStateToken ?? UnavailableText;

    public bool HasBatteryStateText => BatteryStateToken is not null;

    public bool HasLoadPercent => LoadPercent is not null;

    public bool HasInputVoltage => Snapshot?.InputVoltage is not null;

    public bool HasOutputVoltage => Snapshot?.OutputVoltage is not null;

    /// <summary>Localized unavailable label, rendered in a reduced style instead of the metric size.</summary>
    public string UnavailableLabel => UnavailableText;

    private string? StatusTokens(StatusSemanticState state) => Snapshot?.StatusTokens
        .Where(token => token.State == state)
        .Select(token => token.State switch
        {
            StatusSemanticState.Charging => Strings.Get("UpsStatus.Charging"),
            StatusSemanticState.Discharging => Strings.Get("UpsStatus.Discharging"),
            StatusSemanticState.LowBattery => Strings.Get("UpsStatus.LowBattery"),
            StatusSemanticState.ReplaceBattery => Strings.Get("UpsStatus.ReplaceBattery"),
            _ => token.OriginalToken
        })
        .FirstOrDefault();

    public double? LoadPercent => Snapshot?.LoadPercentage is { } value ? (double)value : null;

    public string LoadValueText => Snapshot?.LoadPercentage is { } value ? $"{Number(value)}%" : UnavailableText;

    /// <summary>
    /// Text drawn inside the gauge arc. An absent reading renders an em dash placeholder so the
    /// long localized unavailable label is shown once, in reduced style, outside the arc.
    /// </summary>
    public string LoadGaugeText => Snapshot?.LoadPercentage is { } value ? $"{Number(value)}%" : "—";

    /// <summary>Real and apparent power are optional NUT variables; they are shown only when reported.</summary>
    public string? LoadPowerText
    {
        get
        {
            var watts = Variable("ups.realpower");
            var voltAmps = Variable("ups.power");
            return (watts, voltAmps) switch
            {
                (not null, not null) => $"{watts} W / {voltAmps} VA",
                (not null, null) => $"{watts} W",
                (null, not null) => $"{voltAmps} VA",
                _ => null
            };
        }
    }

    public bool HasLoadPowerText => LoadPowerText is not null;

    public string RuntimeValueText => Snapshot?.Runtime is { } runtime ? FormatDuration(runtime) : UnavailableText;

    public bool HasRuntime => Snapshot?.Runtime is not null;

    /// <summary>Raw NUT reading behind the humanised runtime, shown as technical metadata only.</summary>
    public string? RuntimeRawText => Variable("battery.runtime") is { } seconds ? $"battery.runtime {seconds} s" : null;

    public bool HasRuntimeRawText => RuntimeRawText is not null;

    public string InputVoltageText => Snapshot?.InputVoltage is { } value ? $"{Number(value)} V" : UnavailableText;

    public string OutputVoltageText => Snapshot?.OutputVoltage is { } value ? $"{Number(value)} V" : UnavailableText;

    public string? FrequencyText => Snapshot?.Frequency is { } value ? $"{Number(value)} Hz" : null;

    public bool HasFrequency => Snapshot?.Frequency is not null;

    public string TemperatureText => Snapshot?.Temperature is { } value ? $"{Number(value)} °C" : UnavailableText;

    public bool HasTemperature => Snapshot?.Temperature is not null;

    public string DriverText => Variable("driver.name") ?? UnavailableText;

    public string? DriverVersionText => Variable("driver.version.internal") ?? Variable("driver.version");

    public bool HasDriverVersion => DriverVersionText is not null;

    public string UpsTypeText => Variable("ups.type") ?? UnavailableText;

    public bool HasUpsType => Variable("ups.type") is not null;

    public OverviewStatusItemViewModel? PrimaryStatus => StatusItems.Count > 0 ? StatusItems[0] : null;

    public bool HasPrimaryStatus => PrimaryStatus is not null;

    public string PrimaryStatusToken => PrimaryStatus?.OriginalToken ?? "—";

    public string PrimaryStatusText => PrimaryStatus?.StateText ?? UnavailableText;

    public bool IsPrimaryStatusUnknown => PrimarySemanticState == StatusSemanticState.Unknown;

    private StatusSeverity? PrimarySeverity => Snapshot?.StatusTokens.Count > 0
        ? Snapshot.StatusTokens.Max(token => token.Severity)
        : null;

    public bool IsStatusHealthy => PrimarySeverity is StatusSeverity.Normal or StatusSeverity.Informational;

    public bool IsStatusWarning => PrimarySeverity == StatusSeverity.Warning;

    public bool IsStatusCritical => PrimarySeverity == StatusSeverity.Critical;

    public bool IsStatusUnavailable => PrimarySeverity is null;

    /// <summary>
    /// The semantic state of the token the badge is actually showing. Deliberately the first token
    /// rather than the most severe one: the badge prints the first token's name, and an icon that
    /// described a different token would contradict the text beside it.
    /// </summary>
    private StatusSemanticState? PrimarySemanticState =>
        Snapshot?.StatusTokens.Count > 0 ? Snapshot.StatusTokens[0].State : null;

    /// <summary>
    /// Whether the badge should show a mains plug or a battery. Neither is true for a state that is
    /// neither — bypass, output off, a token NUT does not define — because the power source is then
    /// genuinely not one of the two, and drawing a plug there would report something the UPS never
    /// said.
    /// </summary>
    public bool IsRunningOnMains => PrimarySemanticState == StatusSemanticState.Online;

    public bool IsRunningOnBattery => PrimarySemanticState
        is StatusSemanticState.OnBattery
        or StatusSemanticState.LowBattery
        or StatusSemanticState.Discharging;

    /// <summary>
    /// Plain-language meaning of the reported status token. This explains a real NUT state; it is
    /// not a derived health score and is absent when the state is unknown.
    /// </summary>
    public string? PrimaryStatusDescription => Snapshot?.StatusTokens.Count > 0
        ? Snapshot.StatusTokens
            .OrderByDescending(token => token.Severity)
            .Select(token => token.State switch
            {
                StatusSemanticState.Online => Strings.Get("UpsStatus.Online.Description"),
                StatusSemanticState.OnBattery => Strings.Get("UpsStatus.OnBattery.Description"),
                StatusSemanticState.LowBattery => Strings.Get("UpsStatus.LowBattery.Description"),
                StatusSemanticState.ReplaceBattery => Strings.Get("UpsStatus.ReplaceBattery.Description"),
                StatusSemanticState.Overloaded => Strings.Get("UpsStatus.Overloaded.Description"),
                StatusSemanticState.Bypass => Strings.Get("UpsStatus.Bypass.Description"),
                StatusSemanticState.Calibration => Strings.Get("UpsStatus.Calibration.Description"),
                StatusSemanticState.OutputOff => Strings.Get("UpsStatus.OutputOff.Description"),
                _ => null
            })
            .FirstOrDefault(description => description is not null)
        : null;

    public bool HasPrimaryStatusDescription => PrimaryStatusDescription is not null;

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    public bool IsConnectionPending => ConnectionState is ConnectionState.Connecting or ConnectionState.Reconnecting;

    public bool IsConnectionCritical => ConnectionState is ConnectionState.Disconnected or ConnectionState.ConnectionFailed;

    // Active configuration and administration shortcuts are supplied by the shell, which already
    // owns this state. The dashboard only presents them; it performs no administrative action.
    [ObservableProperty]
    private IReadOnlyList<OverviewInfoRowViewModel> _activeProfileRows = [];

    [ObservableProperty]
    private IReadOnlyList<OverviewInfoRowViewModel> _activeConnectivityRows = [];

    [ObservableProperty]
    private IReadOnlyList<OverviewShortcutViewModel> _administrationShortcuts = [];

    public bool HasActiveConfiguration => ActiveProfileRows.Count > 0 || ActiveConnectivityRows.Count > 0;

    public bool HasAdministrationShortcuts => AdministrationShortcuts.Count > 0;

    public void SetDashboardContext(
        IReadOnlyList<OverviewInfoRowViewModel> activeProfile,
        IReadOnlyList<OverviewInfoRowViewModel> activeConnectivity,
        IReadOnlyList<OverviewShortcutViewModel> shortcuts)
    {
        ActiveProfileRows = activeProfile ?? [];
        ActiveConnectivityRows = activeConnectivity ?? [];
        AdministrationShortcuts = shortcuts ?? [];
    }

    partial void OnActiveProfileRowsChanged(IReadOnlyList<OverviewInfoRowViewModel> value) =>
        OnPropertyChanged(nameof(HasActiveConfiguration));

    partial void OnActiveConnectivityRowsChanged(IReadOnlyList<OverviewInfoRowViewModel> value) =>
        OnPropertyChanged(nameof(HasActiveConfiguration));

    partial void OnAdministrationShortcutsChanged(IReadOnlyList<OverviewShortcutViewModel> value) =>
        OnPropertyChanged(nameof(HasAdministrationShortcuts));

    public string EndpointText => _endpoint is not null
        ? $"{_endpoint.Host}:{_endpoint.Port.ToString(CultureInfo.InvariantCulture)}"
        : UnavailableText;

    public string SelectedUpsText => Snapshot?.Identity.Name ?? _upsName ?? UnavailableText;

    private void NotifyDashboardChanged()
    {
        foreach (var property in DashboardProperties) OnPropertyChanged(property);
    }

    private static readonly string[] DashboardProperties =
    [
        nameof(BatteryPercent), nameof(HasBatteryPercent), nameof(BatteryBarValue), nameof(BatteryValueText),
        nameof(BatterySeverityClass), nameof(BatteryVoltageText), nameof(HasBatteryVoltage), nameof(BatteryStateText),
        nameof(HasBatteryStateText), nameof(HasLoadPercent), nameof(HasInputVoltage), nameof(HasOutputVoltage),
        nameof(PrimaryStatusDescription), nameof(HasPrimaryStatusDescription),
        nameof(LoadPercent), nameof(LoadValueText), nameof(LoadGaugeText), nameof(LoadPowerText), nameof(HasLoadPowerText),
        nameof(RuntimeValueText), nameof(HasRuntime), nameof(RuntimeRawText), nameof(HasRuntimeRawText),
        nameof(InputVoltageText), nameof(OutputVoltageText), nameof(FrequencyText), nameof(HasFrequency),
        nameof(TemperatureText), nameof(HasTemperature), nameof(DriverText), nameof(DriverVersionText),
        nameof(HasDriverVersion), nameof(UpsTypeText), nameof(HasUpsType),
        nameof(PrimaryStatus), nameof(HasPrimaryStatus), nameof(PrimaryStatusToken), nameof(PrimaryStatusText),
        nameof(IsPrimaryStatusUnknown),
        nameof(IsStatusHealthy), nameof(IsStatusWarning), nameof(IsStatusCritical), nameof(IsStatusUnavailable),
        nameof(IsRunningOnMains), nameof(IsRunningOnBattery),
        nameof(IsConnected), nameof(IsConnectionPending), nameof(IsConnectionCritical),
        nameof(EndpointText), nameof(SelectedUpsText)
    ];

    private IReadOnlyList<OverviewMetricViewModel> CreateMetricCards(UpsSnapshot? snapshot) =>
    [
        CreateDecimalMetric(Strings.Get("Overview.Metric.BatteryCharge"), snapshot?.BatteryChargePercentage, "%"),
        CreateDurationMetric(Strings.Get("Overview.Metric.Runtime"), snapshot?.Runtime),
        CreateDecimalMetric(Strings.Get("Overview.Metric.Load"), snapshot?.LoadPercentage, "%"),
        CreateDecimalMetric(Strings.Get("Overview.Metric.InputVoltage"), snapshot?.InputVoltage, "V"),
        CreateDecimalMetric(Strings.Get("Overview.Metric.OutputVoltage"), snapshot?.OutputVoltage, "V"),
        CreateDecimalMetric(Strings.Get("Overview.Metric.Frequency"), snapshot?.Frequency, "Hz"),
        CreateDecimalMetric(Strings.Get("Overview.Metric.Temperature"), snapshot?.Temperature, "°C"),
        CreateDecimalMetric(Strings.Get("Overview.Metric.BatteryVoltage"), snapshot?.BatteryVoltage, "V")
    ];

    private OverviewMetricViewModel CreateDecimalMetric(string title, decimal? value, string unit) =>
        value is null
            ? new OverviewMetricViewModel(title, Strings.Get("Status.Unavailable"), null)
            : new OverviewMetricViewModel(title, value.Value.ToString("0.##", CultureInfo.CurrentCulture), unit);

    private OverviewMetricViewModel CreateDurationMetric(string title, TimeSpan? value) =>
        value is null
            ? new OverviewMetricViewModel(title, Strings.Get("Status.Unavailable"), null)
            : new OverviewMetricViewModel(title, FormatDuration(value.Value), null);

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours} h {value.Minutes:D2} min"
        : $"{Math.Max(0, (int)value.TotalMinutes)} min";

    private OverviewStatusItemViewModel CreateStatusItem(UpsStatusToken token) =>
        new(
            token.OriginalToken,
            token.State switch
            {
                StatusSemanticState.Online => Strings.Get("UpsStatus.Online"),
                StatusSemanticState.OnBattery => Strings.Get("UpsStatus.OnBattery"),
                StatusSemanticState.LowBattery => Strings.Get("UpsStatus.LowBattery"),
                StatusSemanticState.ReplaceBattery => Strings.Get("UpsStatus.ReplaceBattery"),
                StatusSemanticState.Charging => Strings.Get("UpsStatus.Charging"),
                StatusSemanticState.Discharging => Strings.Get("UpsStatus.Discharging"),
                StatusSemanticState.Bypass => Strings.Get("UpsStatus.Bypass"),
                StatusSemanticState.OutputOff => Strings.Get("UpsStatus.OutputOff"),
                StatusSemanticState.Overloaded => Strings.Get("UpsStatus.Overloaded"),
                StatusSemanticState.Calibration => Strings.Get("UpsStatus.Calibration"),
                _ => token.OriginalToken
            },
            token.Severity switch
            {
                StatusSeverity.Normal => Strings.Get("Severity.Normal"),
                StatusSeverity.Informational => Strings.Get("Severity.Informational"),
                StatusSeverity.Warning => Strings.Get("Severity.Warning"),
                StatusSeverity.Critical => Strings.Get("Severity.Critical"),
                _ => Strings.Get("Common.Unknown")
            },
            token.State == StatusSemanticState.Unknown);
}

public sealed partial class DiagnosticsPageViewModel : PageViewModel, IDisposable
{
    private readonly ApplicationSettings _settings;
    private readonly ApplicationRuntimeInfo _runtimeInfo;
    private readonly IUpsPollingCoordinator? _polling;
    private readonly DevicesPageViewModel? _devices;
    private readonly ILocalNutInstallationDetector? _installationDetector;
    private readonly ILocalNutVersionResolver? _versionResolver;
    private ManagedNutServerRuntimeContext? _profileContext;
    private PollingState _pollingState;
    private NutInstallationInfo _localInstallation = NutInstallationInfo.NotDetected();
    private NutVersionSource _localVersionSource = NutVersionSource.Unavailable;
    private string? _diagnosticCopyStatusMessage;

    public DiagnosticsPageViewModel()
        : this(new ApplicationSettings(), new ApplicationRuntimeInfo("-", "-", "-", "-", "-"))
    {
    }

    public DiagnosticsPageViewModel(
        ApplicationSettings settings,
        ApplicationRuntimeInfo runtimeInfo,
        IUpsPollingCoordinator? polling = null,
        DevicesPageViewModel? devices = null,
        ILocalNutInstallationDetector? installationDetector = null,
        ManagedNutServerRuntimeContext? profileContext = null,
        UiLanguagePreference language = UiLanguagePreference.PtBr,
        ILocalNutVersionResolver? versionResolver = null)
        : base(new NutManagerLocalizer(language).Get("Diagnostics.Title"), new NutManagerLocalizer(language).Get("Diagnostics.Description"))
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtimeInfo);

        _settings = settings;
        _runtimeInfo = runtimeInfo;
        _polling = polling;
        _devices = devices;
        _installationDetector = installationDetector;
        _versionResolver = versionResolver;
        _profileContext = profileContext;
        Strings = new NutManagerLocalizer(language);
        _pollingState = polling?.State ?? PollingState.Unavailable;

        if (_polling is not null)
        {
            _polling.StateChanged += OnPollingStateChanged;
        }

        if (_devices is not null)
        {
            _devices.PropertyChanged += OnDevicesPropertyChanged;
        }
    }

    public NutManagerLocalizer Strings { get; }

    // Categorical states derived from the live polling/installation state. These are never
    // aggregated into a score: no health percentage or pass/fail tally is invented.
    public bool IsConnectionHealthy => _pollingState.ConnectionState == ConnectionState.Connected;
    public bool IsConnectionCritical => _pollingState.ConnectionState is ConnectionState.Disconnected or ConnectionState.ConnectionFailed;
    public bool IsDataFresh => _pollingState.DataFreshness == DataFreshness.Fresh;
    public bool IsDataStale => _pollingState.DataFreshness == DataFreshness.Stale;
    public bool HasSnapshot => _pollingState.Snapshot is not null;
    public bool HasLastError => !string.IsNullOrWhiteSpace(_pollingState.LastError);
    public bool IsLocalInstallationDetected => _localInstallation.IsDetected;
    public bool HasDevicesDiscovered => DiscoveredUpsCount > 0;

    /// <summary>
    /// The vocabulary of what this page groups. "Application and environment" left it along with the
    /// card that carried it: version, runtime and platform are product identity, not a diagnostic,
    /// and they are shown on the About page now.
    /// </summary>
    public IReadOnlyList<string> DiagnosticGroups =>
    [
        Strings.Get("Diagnostics.Group.Overview"),
        Strings.Get("Diagnostics.Group.Connection"),
        Strings.Get("Diagnostics.Group.Polling"),
        Strings.Get("Diagnostics.Group.Discovery"),
        Strings.Get("Diagnostics.Group.Technical")
    ];

    public string? DiagnosticCopyStatusMessage
    {
        get => _diagnosticCopyStatusMessage;
        private set
        {
            if (SetProperty(ref _diagnosticCopyStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasDiagnosticCopyStatusMessage));
            }
        }
    }

    public bool HasDiagnosticCopyStatusMessage => !string.IsNullOrWhiteSpace(DiagnosticCopyStatusMessage);

    public string ApplicationName => "NUT Manager";
    /// <summary>
    /// What the card shows: "v1.0.0". The technical version, with its build metadata, goes into the
    /// copied report instead — see <see cref="CreateDiagnosticReport"/>. Separating them is the whole
    /// point: support needs the exact build, and nobody reading a version field needs a commit hash.
    /// </summary>
    public string ApplicationVersion => _runtimeInfo.DisplayVersion;

    /// <summary>The full informational version, kept for the report and for support.</summary>
    public string ApplicationBuildVersion => _runtimeInfo.Version;
    public string Runtime => _runtimeInfo.Runtime;
    public string OperatingSystem => _runtimeInfo.OperatingSystem;
    public string Architecture => _runtimeInfo.Architecture;

    public string ModeText => Strings.Get("Diagnostics.LiveServer");
    public string Host => _profileContext?.Endpoint.Host ?? Strings.Get("Status.Unavailable");
    public string Port => _profileContext?.Endpoint.Port.ToString(CultureInfo.InvariantCulture) ?? Strings.Get("Status.Unavailable");
    public string ConnectionTimeoutText => FormatDuration(_settings.ConnectionTimeout);
    public string PollingIntervalText => FormatDuration(_settings.PollingInterval);
    public string PreferredUpsName => _profileContext?.Profile.Monitoring.PreferredUpsName ?? Strings.Get("Common.NotConfigured");
    public string ManagedProfileName => _profileContext?.Profile.Name ?? Strings.Get("Diagnostics.CurrentLocalProfile");
    public string ManagementModeText => _profileContext?.Profile.Management.Mode == NutManagementMode.Remote ? Strings.Get("Management.Remote") : Strings.Get("Management.Local");
    public string ManagementAccessText => _profileContext?.Profile.AccessMode == ManagedNutServerAccessMode.ReadOnly ? Strings.Get("Access.ReadOnly") : Strings.Get("Diagnostics.AccessManage");

    /// <summary>
    /// Keeps the diagnostic report honest about the access mode after it is changed and saved.
    ///
    /// The report is copied and pasted into support conversations, so a stale access mode there is
    /// worse than a stale one on screen: it travels, and nobody reading it knows it is old.
    /// </summary>
    public void ApplyAccessMode(ManagedNutServerAccessMode accessMode)
    {
        if (_profileContext is not { } context || context.Profile.AccessMode == accessMode) return;

        var profile = new ManagedNutServerProfile(
            context.Profile.Id, context.Profile.Name, context.Profile.Monitoring, context.Profile.Management, accessMode);

        _profileContext = context with
        {
            Profile = profile,
            Capabilities = ManagedServerCapabilities.FromProfile(profile)
        };

        OnPropertyChanged(nameof(ManagementAccessText));
    }
    public bool IsLocalManagementProfile => _profileContext?.Profile.Management.Mode != NutManagementMode.Remote;

    public int DiscoveredUpsCount => _devices?.Devices.Count ?? 0;
    public string SelectedUpsName => _devices?.SelectedDevice?.Name ?? _pollingState.UpsName ?? Strings.Get("Diagnostics.NoUpsSelected");
    public string SelectedUpsDescription => DisplayIdentity?.Description ?? Strings.Get("Status.Unavailable");
    public string Manufacturer => DisplayIdentity?.Manufacturer ?? Strings.Get("Status.Unavailable");
    public string Model => DisplayIdentity?.Model ?? Strings.Get("Status.Unavailable");
    public string SerialNumber => DisplayIdentity?.SerialNumber ?? Strings.Get("Status.Unavailable");

    public string ConnectionStateText => ToConnectionStateText(_pollingState.ConnectionState);
    public string DataFreshnessText => ToDataFreshnessText(_pollingState.DataFreshness);
    public string SnapshotStatusText => _pollingState.Snapshot is null ? Strings.Get("Diagnostics.SnapshotUnavailable") : Strings.Get("Diagnostics.SnapshotAvailable");
    public string DataSourceText => _pollingState.Snapshot?.Source switch
    {
        DataSource.Simulated => Strings.Get("Shell.SimulationActive"),
        DataSource.Live => Strings.Get("Diagnostics.DataSource.NutServer"),
        _ => Strings.Get("Status.Unavailable")
    };
    public string LastSuccessfulUpdateText => _pollingState.Snapshot is null
        ? Strings.Get("Status.Unavailable")
        : NutTimestampPresentation.Local(_pollingState.Snapshot.LastSuccessfulUpdate, "g");
    public string LastErrorText => string.IsNullOrWhiteSpace(_pollingState.LastError) ? Strings.Get("Diagnostics.NoError") : _pollingState.LastError;

    /// <summary>
    /// Whether a NUT installation was found **on this machine**.
    ///
    /// For a remote profile that question has no bearing on anything the operator is looking at, and
    /// answering it with "no installation found" invites exactly the wrong reading: that the server
    /// being managed has no NUT. The station running the desktop application usually has none, and
    /// that says nothing at all about GANDALF.
    ///
    /// So a remote profile gets a state of its own rather than a local answer dressed up as a remote
    /// one. Nothing here probes the server: no agent operation, no new endpoint, no inferred remote
    /// detection. It reports that the local question does not apply, which is the only honest thing it
    /// knows.
    /// </summary>
    public string LocalInstallationStatusText => IsLocalManagementProfile
        ? (_localInstallation.IsDetected
            ? Strings.Get("Diagnostics.InstallationFound")
            : Strings.Get("Diagnostics.InstallationNotFound"))
        : Strings.Get("Diagnostics.LocalTechnicalNotApplicable");
    public string InstallationDirectoryText => _localInstallation.InstallationDirectory ?? Strings.Get("Status.Unavailable");
    public string ConfigurationDirectoryText => _localInstallation.ConfigurationDirectory ?? Strings.Get("Status.Unavailable");
    public string LocalInstallationVersionText => _localInstallation.Version ?? Strings.Get("Status.Unavailable");
    public string LocalVersionSourceText => _localVersionSource switch
    {
        NutVersionSource.FileMetadata => Strings.Get("Diagnostics.VersionSource.Metadata"),
        NutVersionSource.ExecutableFallback => Strings.Get("Diagnostics.VersionSource.Fallback"),
        _ => Strings.Get("Status.Unavailable")
    };
    public string DetectionSourceText => _localInstallation.DetectionSource ?? Strings.Get("Status.Unavailable");
    public string ExecutablesText => _localInstallation.Executables.Count == 0
        ? Strings.Get("Diagnostics.NoExecutables")
        : string.Join(Environment.NewLine, _localInstallation.Executables.Select(entry => $"{entry.Key}: {entry.Value}"));
    public string ConfigurationFilesText => _localInstallation.ConfigurationFiles.Count == 0
        ? Strings.Get("Diagnostics.NoFiles")
        : string.Join(Environment.NewLine, _localInstallation.ConfigurationFiles.Select(file =>
            $"{file.Name}: {(file.Exists ? (file.IsReadable ? Strings.Get("Diagnostics.FileAvailable") : Strings.Get("Diagnostics.FileUnreadable")) : Strings.Get("Diagnostics.FileMissing"))}"));

    public string CreateDiagnosticReport()
    {
        var lines = new[]
        {
            Strings.Get("Diagnostics.Report.Title"),
            ReportLine("Diagnostics.Report.ApplicationVersion", ApplicationBuildVersion),
            ReportLine("Diagnostics.Report.Runtime", Runtime),
            ReportLine("Diagnostics.Report.OperatingSystem", OperatingSystem),
            ReportLine("Diagnostics.Report.Architecture", Architecture),
            ReportLine("Diagnostics.Report.Mode", ModeText),
            ReportLine("Diagnostics.Report.Profile", ManagedProfileName),
            ReportLine("Diagnostics.Report.MonitoringEndpoint", $"{Host}:{Port}"),
            ReportLine("Diagnostics.Report.ManagementMode", ManagementModeText),
            ReportLine("Diagnostics.Report.Access", ManagementAccessText),
            ReportLine("Diagnostics.Report.Connection", ConnectionStateText),
            ReportLine("Diagnostics.Report.Freshness", DataFreshnessText),
            ReportLine("Diagnostics.Report.Snapshot", SnapshotStatusText),
            ReportLine("Diagnostics.Report.Source", DataSourceText),
            ReportLine("Diagnostics.Report.DiscoveredUps", DiscoveredUpsCount.ToString(CultureInfo.InvariantCulture)),
            ReportLine("Diagnostics.Report.SelectedUps", SelectedUpsName),
            ReportLine("Diagnostics.Report.LocalInstallation", LocalInstallationStatusText),
            ReportLine("Diagnostics.Report.NutVersion", LocalInstallationVersionText),
            ReportLine("Diagnostics.Report.DetectionSource", DetectionSourceText),
            ReportLine("Diagnostics.Report.ErrorState", string.IsNullOrWhiteSpace(_pollingState.LastError)
                ? Strings.Get("Diagnostics.Report.None")
                : Strings.Get("Diagnostics.Report.PresentRedacted")),
        };
        return string.Join("\n", lines);
    }

    public void ReportDiagnosticCopyResult(bool succeeded) =>
        DiagnosticCopyStatusMessage = Strings.Get(succeeded ? "Diagnostics.Copied" : "Diagnostics.CopyFailed");
    public string? LocalInstallationError { get; private set; }
    public bool HasLocalInstallationError => !string.IsNullOrWhiteSpace(LocalInstallationError);
    public bool IsDetectingLocalInstallation { get; private set; }
    public bool CanInspectLocalInstallation =>
        IsLocalManagementProfile &&
        _installationDetector is not null &&
        !IsDetectingLocalInstallation;

    [RelayCommand]
    private Task DetectLocalInstallationAsync() => RefreshLocalInstallationAsync(CancellationToken.None);

    public async Task RefreshLocalInstallationAsync(CancellationToken cancellationToken = default)
    {
        if (_profileContext?.Profile.Management.Mode == NutManagementMode.Remote)
        {
            ApplyLocalInstallation(NutInstallationInfo.NotDetected());
            LocalInstallationError = Strings.Get("Diagnostics.RemoteNoLocalDetection");
            NotifyLocalInstallationPropertiesChanged();
            return;
        }

        if (_installationDetector is null)
        {
            ApplyLocalInstallation(NutInstallationInfo.NotDetected());
            LocalInstallationError = Strings.Get("Diagnostics.DetectionUnavailable");
            NotifyLocalInstallationPropertiesChanged();
            return;
        }

        await InspectLocalInstallationAsync(
            token => _installationDetector.DetectAsync(token),
            cancellationToken);
    }

    public Task InspectLocalInstallationDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (_profileContext?.Profile.Management.Mode == NutManagementMode.Remote)
        {
            ApplyLocalInstallation(NutInstallationInfo.NotDetected());
            LocalInstallationError = Strings.Get("Diagnostics.RemoteNoLocalInspection");
            NotifyLocalInstallationPropertiesChanged();
            return Task.CompletedTask;
        }

        if (_installationDetector is null)
        {
            ApplyLocalInstallation(NutInstallationInfo.NotDetected());
            LocalInstallationError = Strings.Get("Diagnostics.DetectionUnavailable");
            NotifyLocalInstallationPropertiesChanged();
            return Task.CompletedTask;
        }

        return InspectLocalInstallationAsync(
            token => _installationDetector.InspectDirectoryAsync(directory, token),
            cancellationToken);
    }

    public void Dispose()
    {
        if (_polling is not null)
        {
            _polling.StateChanged -= OnPollingStateChanged;
        }

        if (_devices is not null)
        {
            _devices.PropertyChanged -= OnDevicesPropertyChanged;
        }
    }

    private UpsIdentity? DisplayIdentity => _pollingState.Snapshot?.Identity ?? _devices?.SelectedDevice;

    private async Task InspectLocalInstallationAsync(
        Func<CancellationToken, Task<NutInstallationInfo>> inspectAsync,
        CancellationToken cancellationToken)
    {
        IsDetectingLocalInstallation = true;
        LocalInstallationError = null;
        NotifyLocalInstallationPropertiesChanged();
        try
        {
            var installation = await inspectAsync(cancellationToken);
            var resolution = _versionResolver is null
                ? (string.IsNullOrWhiteSpace(installation.Version)
                    ? NutVersionResolution.Unavailable
                    : new NutVersionResolution(installation.Version, NutVersionSource.FileMetadata))
                : await _versionResolver.ResolveAsync(installation, cancellationToken);
            _localVersionSource = resolution.Source;
            if (string.IsNullOrWhiteSpace(installation.Version) && !string.IsNullOrWhiteSpace(resolution.Version))
            {
                installation = installation with { Version = resolution.Version };
            }
            ApplyLocalInstallation(installation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ApplyLocalInstallation(NutInstallationInfo.NotDetected());
            LocalInstallationError = Strings.Get("Diagnostics.InspectionFailed");
        }
        finally
        {
            IsDetectingLocalInstallation = false;
            NotifyLocalInstallationPropertiesChanged();
        }
    }

    private void ApplyLocalInstallation(NutInstallationInfo installation)
    {
        if (!installation.IsDetected || string.IsNullOrWhiteSpace(installation.Version))
        {
            _localVersionSource = NutVersionSource.Unavailable;
        }
        _localInstallation = installation;
        LocalInstallationError = installation.ErrorMessage;
    }

    private void OnPollingStateChanged(PollingState state) => RunOnUiThread(() =>
    {
        _pollingState = state;
        NotifyPollingPropertiesChanged();
    });

    private void OnDevicesPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(DevicesPageViewModel.Devices) or nameof(DevicesPageViewModel.SelectedDevice))
        {
            RunOnUiThread(NotifyDevicePropertiesChanged);
        }
    }

    private void NotifyPollingPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedUpsName));
        OnPropertyChanged(nameof(SelectedUpsDescription));
        OnPropertyChanged(nameof(Manufacturer));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(SerialNumber));
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(DataFreshnessText));
        OnPropertyChanged(nameof(SnapshotStatusText));
        OnPropertyChanged(nameof(DataSourceText));
        OnPropertyChanged(nameof(LastSuccessfulUpdateText));
        OnPropertyChanged(nameof(LastErrorText));
        OnPropertyChanged(nameof(IsConnectionHealthy));
        OnPropertyChanged(nameof(IsConnectionCritical));
        OnPropertyChanged(nameof(IsDataFresh));
        OnPropertyChanged(nameof(IsDataStale));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(HasLastError));
    }

    private void NotifyDevicePropertiesChanged()
    {
        OnPropertyChanged(nameof(DiscoveredUpsCount));
        OnPropertyChanged(nameof(SelectedUpsName));
        OnPropertyChanged(nameof(SelectedUpsDescription));
        OnPropertyChanged(nameof(Manufacturer));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(SerialNumber));
    }

    private void NotifyLocalInstallationPropertiesChanged()
    {
        OnPropertyChanged(nameof(LocalInstallationStatusText));
        OnPropertyChanged(nameof(InstallationDirectoryText));
        OnPropertyChanged(nameof(ConfigurationDirectoryText));
        OnPropertyChanged(nameof(LocalInstallationVersionText));
        OnPropertyChanged(nameof(LocalVersionSourceText));
        OnPropertyChanged(nameof(DetectionSourceText));
        OnPropertyChanged(nameof(ExecutablesText));
        OnPropertyChanged(nameof(ConfigurationFilesText));
        OnPropertyChanged(nameof(LocalInstallationError));
        OnPropertyChanged(nameof(HasLocalInstallationError));
        OnPropertyChanged(nameof(IsDetectingLocalInstallation));
        OnPropertyChanged(nameof(CanInspectLocalInstallation));
    }

    private static void RunOnUiThread(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    private static string FormatDuration(TimeSpan value) => value.TotalSeconds % 1 == 0
        ? $"{value.TotalSeconds:0} s"
        : value.ToString("c", CultureInfo.InvariantCulture);

    private string ReportLine(string labelKey, string value) => $"{Strings.Get(labelKey)}: {value}";

    public string ToConnectionStateText(ConnectionState state) => state switch
    {
        ConnectionState.Disconnected => Strings.Get("Status.Disconnected"),
        ConnectionState.Connecting => Strings.Get("Status.Connecting"),
        ConnectionState.Connected => Strings.Get("Status.Connected"),
        ConnectionState.Reconnecting => Strings.Get("Status.Reconnecting"),
        ConnectionState.ConnectionFailed => Strings.Get("Status.ConnectionFailed"),
        _ => Strings.Get("Status.Unavailable")
    };

    public string ToDataFreshnessText(DataFreshness freshness) => freshness switch
    {
        DataFreshness.Unavailable => Strings.Get("Status.Unavailable"),
        DataFreshness.Fresh => Strings.Get("Status.Fresh"),
        DataFreshness.Stale => Strings.Get("Status.Stale"),
        _ => Strings.Get("Status.Unavailable")
    };
}
