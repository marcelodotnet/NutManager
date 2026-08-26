using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NutManager.App.Localization;
using NutManager.App.Services;
using NutManager.Core.Administration;
using NutManager.Core.Agent;
using NutManager.Core.Configuration;
using NutManager.Core.Configuration.Semantic;
using NutManager.Core.Models;
using NutManager.Core.Services;

namespace NutManager.App.ViewModels;

public sealed partial class AdministrationPageViewModel : PageViewModel
{
    private const string UnavailableText = "Indisponível";
    private readonly ILocalNutInstallationDetector? _installationDetector;
    private INutConfigurationFilePipeline? _configurationPipeline;
    private readonly ILocalNutWindowsAdministration? _windowsAdministration;
    private readonly ILocalNutDriverDiagnostics? _driverDiagnostics;
    private readonly ILocalNutDriverCatalogSource? _driverCatalogSource;
    private ManagedNutServerRuntimeContext? _profileContext;

    // The same agent client the remote service monitor uses, for one read-only operation. It is
    // absent for a local profile, and its absence is the whole of the "no remote inspection here"
    // decision — there is no second transport and no fallback to anything else.
    private INutManagerAgentClient? _agentClient;
    private readonly RemoteManagementSessionViewModel? _remoteManagement;
    private NutInstallationInfo? _currentInstallation;
    private NutConfigurationFileSnapshot? _loadedSnapshot;
    private NutConfigurationPreparedChange? _preparedChange;
    private IReadOnlyList<NutConfigurationEntryViewModel> _entries = Array.Empty<NutConfigurationEntryViewModel>();
    private int _draftVersion;
    private int _preparedDraftVersion = -1;
    private int _installationContextVersion;
    private int _navigationGeneration;
    private CancellationTokenSource? _navigationCancellation;

    public AdministrationPageViewModel()
        : this(null, null, null, null, null, null, UiLanguagePreference.PtBr, null)
    {
    }

    public AdministrationPageViewModel(
        ILocalNutInstallationDetector? installationDetector,
        INutConfigurationFilePipeline? configurationPipeline,
        ILocalNutWindowsAdministration? windowsAdministration = null,
        ILocalNutDriverDiagnostics? driverDiagnostics = null,
        ManagedNutServerRuntimeContext? profileContext = null,
        RemoteManagementSessionViewModel? remoteManagement = null,
        UiLanguagePreference language = UiLanguagePreference.PtBr,
        ILocalNutDriverCatalogSource? driverCatalogSource = null,
        RemoteWindowsServiceViewModel? remoteWindowsService = null,
        RemoteWindowsServiceControlViewModel? remoteWindowsServiceControl = null,
        INutManagerAgentClient? agentClient = null)
        : base(
            new NutManagerLocalizer(language).Get("Administration.Title"),
            profileContext?.Profile.Management.Mode == NutManagementMode.Remote
                ? new NutManagerLocalizer(language).Get("Administration.Description.Remote")
                : new NutManagerLocalizer(language).Get("Administration.Description.Local"))
    {
        _installationDetector = installationDetector;
        _configurationPipeline = configurationPipeline;
        _windowsAdministration = windowsAdministration;
        _driverDiagnostics = driverDiagnostics;
        _driverCatalogSource = driverCatalogSource;
        _profileContext = profileContext;
        _remoteManagement = remoteManagement;
        RemoteWindowsService = remoteWindowsService;
        RemoteWindowsServiceControl = remoteWindowsServiceControl;
        _agentClient = agentClient;

        // A local profile with diagnostics inspects locally from the outset. A remote profile starts
        // with nothing established: whether that server can be inspected is a question only its
        // agent can answer, and it is asked rather than assumed.
        _deviceInspectionSource = IsLocalManagementProfile && _driverDiagnostics is not null
            ? NutDeviceInspectionSource.Local
            : NutDeviceInspectionSource.Unavailable;
        Strings = new NutManagerLocalizer(language);
        if (_remoteManagement is not null)
        {
            _remoteManagement.ConfigurationContextChanged += OnRemoteConfigurationContextChanged;
            _remoteManagement.PropertyChanged += OnRemoteManagementPropertyChanged;
        }
        // All five supported files remain in the navigation. The profile's managed-file selection
        // is an enabled state, not a filter: disabling a file leaves its module discoverable but
        // prevents selection and any pipeline work.
        ConfigurationFiles = new ObservableCollection<NutConfigurationFileItemViewModel>(
            CreateFileItems(_profileContext?.Profile.Management.ManagedFiles));
        Sections = Array.Empty<NutConfigurationSectionViewModel>();
        PreviewLines = Array.Empty<NutConfigurationPreviewLineViewModel>();
        AdministrationSections = AdministrationPresentation.CreateSections(
            Strings,
            IsRemoteManagementProfile,
            _profileContext?.Profile.AccessMode != ManagedNutServerAccessMode.ReadOnly);
        _selectedAdministrationSection = AdministrationSections[0];
    }

    public NutManagerLocalizer Strings { get; }

    public IReadOnlyList<AdministrationSectionItemViewModel> AdministrationSections { get; private set; }

    [ObservableProperty]
    private AdministrationSectionItemViewModel _selectedAdministrationSection;

    public bool IsNutConfigurationSectionSelected => SelectedAdministrationSection.Section == AdministrationSection.NutConfiguration;
    public bool IsWindowsServiceSectionSelected => SelectedAdministrationSection.Section == AdministrationSection.WindowsService;
    public bool IsDevicesDriversSectionSelected => SelectedAdministrationSection.Section == AdministrationSection.DevicesAndDrivers;
    public bool IsRemoteAccessSectionSelected => SelectedAdministrationSection.Section == AdministrationSection.RemoteAccess;

    public string ManagementModeDisplayText => IsRemoteManagementProfile
        ? Strings.Get("Management.Remote")
        : Strings.Get("Management.Local");

    public string AccessModeDisplayText => _profileContext?.Profile.AccessMode == ManagedNutServerAccessMode.ReadOnly
        ? Strings.Get("Access.ReadOnly")
        : Strings.Get("Access.Manage");

    public string TransportDisplayText => !IsRemoteManagementProfile
        ? Strings.Get("Administration.Context.LocalTransport")
        : _profileContext?.Profile.Management.ConfigurationTransport == RemoteConfigurationTransportKind.Smb
            ? Strings.Get("Transport.Smb")
            : Strings.Get("Transport.Sftp");

    public string AdministrationAvailabilityText => SelectedAdministrationSection.AvailabilityText;

    public ObservableCollection<NutConfigurationFileItemViewModel> ConfigurationFiles { get; }

    public bool IsConfigurationFileListEmpty => ConfigurationFiles.All(file => !file.IsManaged);

    /// <summary>
    /// Applies the confirmed managed-file scope of the profile already running in this process.
    /// This deliberately does not replace the runtime profile, transport or session: endpoint and
    /// connection changes still take effect on the next application start. If the open file was
    /// disabled, its editor is closed immediately so a stale draft cannot reach the write pipeline.
    /// </summary>
    public void UpdateManagedConfigurationFiles(ManagedNutConfigurationFiles managedFiles)
    {
        ArgumentNullException.ThrowIfNull(managedFiles);

        foreach (var file in ConfigurationFiles)
        {
            file.IsManaged = managedFiles.Contains(file.FileKind);
        }

        if (SelectedFile is { IsManaged: false })
        {
            ClearLoadedDocument(clearSelectedFile: true);
            SetStatus(Strings.Get("Administration.File.NotEnabled"));
        }

        OnPropertyChanged(nameof(IsConfigurationFileListEmpty));
        RefreshConfigurationFileTiles();
        NotifyWorkflowPropertiesChanged();
    }

    // ==================== Configuration file rail ====================

    /// <summary>
    /// Mirrors the page's selection and draft state onto the tiles, so the file strip can mark them
    /// without reaching back into the page for every item.
    ///
    /// The strip used to fold: a toggle, a persisted preference, an effective state separate from
    /// that preference, and a width threshold that folded it regardless. All of it went. The tiles
    /// are one fixed size in one row, which is small enough that hiding it was never worth a
    /// control of its own — and a switcher that can hide the thing you switch with is a way to get
    /// lost, not a way to save room.
    /// </summary>
    private void RefreshConfigurationFileTiles()
    {
        foreach (var file in ConfigurationFiles)
        {
            file.IsSelected = ReferenceEquals(file, SelectedFile);
            file.HasPendingChanges = file.IsSelected && HasDraftChanges;
        }
    }

    [ObservableProperty]
    private IReadOnlyList<NutConfigurationSectionViewModel> _sections;

    [ObservableProperty]
    private IReadOnlyList<NutConfigurationPreviewLineViewModel> _previewLines;

    [ObservableProperty]
    private UpsConfigurationEditorViewModel? _upsConfigurationEditor;

    [ObservableProperty]
    private NutGeneralConfigurationEditorViewModel? _nutGeneralConfigurationEditor;

    [ObservableProperty]
    private UpsdConfigurationEditorViewModel? _upsdConfigurationEditor;

    [ObservableProperty]
    private UpsdUsersConfigurationEditorViewModel? _upsdUsersConfigurationEditor;

    [ObservableProperty]
    private UpsmonConfigurationEditorViewModel? _upsmonConfigurationEditor;

    [ObservableProperty]
    private SemanticConfigurationReviewViewModel? _semanticReview;

    [ObservableProperty]
    private NutConfigurationFileItemViewModel? _selectedFile;

    [ObservableProperty]
    private bool _isDetectingInstallation;

    [ObservableProperty]
    private bool _isBusy;

    // Navigation between configuration files is the one operation the user is expected to repeat
    // quickly, so it gets its own flag: the file list must stay usable while a file is loading,
    // which it cannot do if the only signal available is the shared IsBusy.
    [ObservableProperty]
    private bool _isLoadingFile;

    [ObservableProperty]
    private bool _isPreviewConfirmed;

    [ObservableProperty]
    private string _installationStatusText = "Nenhuma instalação NUT local encontrada";

    [ObservableProperty]
    private string _installationDirectoryText = UnavailableText;

    [ObservableProperty]
    private string _configurationDirectoryText = UnavailableText;

    [ObservableProperty]
    private string _installationVersionText = UnavailableText;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isCriticalResult;

    [ObservableProperty]
    private string? _backupPath;

    [ObservableProperty]
    private string? _recoveryPath;

    [ObservableProperty]
    private string? _temporaryPath;

    [ObservableProperty]
    private IReadOnlyList<NutServiceInfo> _windowsServices = Array.Empty<NutServiceInfo>();

    [ObservableProperty]
    private NutServiceInfo? _selectedWindowsService;

    /// <summary>Last inspection snapshot, kept to report why the service list looks the way it does.</summary>
    private NutWindowsAdministrationSnapshot? _windowsAdministrationSnapshot;

    // Starts as "not yet determined", never as "unsupported platform": before the first inspection
    // completes the platform verdict is unknown, and the stale initial value was being rendered as
    // a false "not available on this platform" message on Windows.
    [ObservableProperty]
    private NutPermissionAssessment _windowsPermissionAssessment = NutPermissionAssessment.NotDetermined(string.Empty);

    [ObservableProperty]
    private IReadOnlyList<NutProcessInfo> _windowsProcesses = Array.Empty<NutProcessInfo>();

    [ObservableProperty]
    private IReadOnlyList<NutEventLogEntry> _windowsEvents = Array.Empty<NutEventLogEntry>();

    [ObservableProperty]
    private NutEventLogStatus _windowsEventLogStatus = NutEventLogStatus.Success;

    [ObservableProperty]
    private string? _windowsEventLogDiagnosticMessage;

    [ObservableProperty]
    private NutAdministrativeActionRequest? _pendingAdministrativeAction;

    [ObservableProperty]
    private bool _isAdministrativeActionConfirmed;

    [ObservableProperty]
    private string? _administrativeStatusMessage;

    [ObservableProperty]
    private bool _isAdministrativeCritical;

    [ObservableProperty]
    private IReadOnlyList<NutComPortInfo> _comPorts = Array.Empty<NutComPortInfo>();

    /// <summary>
    /// The same ports, prepared for the screen: status, and the identity line composed from what the
    /// device actually reported. Derived rather than stored twice — <see cref="ComPorts"/> stays the
    /// record the ups.conf editor consumes for its port choices.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<DetectedComPortViewModel> _detectedComPorts = Array.Empty<DetectedComPortViewModel>();

    [ObservableProperty]
    private NutDeviceInspectionSource _deviceInspectionSource;

    /// <summary>
    /// Whether the port list on screen is an answer at all.
    ///
    /// False before the first reading and whenever the source could not be asked, and that is what
    /// keeps an unreachable agent from being presented as a server with no serial ports. Every
    /// statement about a configured port being present or absent is gated on this.
    /// </summary>
    [ObservableProperty]
    private bool _isComPortListKnown;

    /// <summary>
    /// Whether anything has actually read <c>ups.conf</c>. False before the first reading, and false
    /// whenever the file could not be opened at all — a remote profile whose configuration session
    /// has not been established yet has no transport to read it with.
    ///
    /// An empty list means one of two unrelated things, and the screen has to tell them apart: a file
    /// that was read and declares no drivers, or a file nobody has opened. Without this the second is
    /// reported as the first, which states something about a file the application has never seen.
    /// </summary>
    [ObservableProperty]
    private bool _isConfiguredDriverListKnown;

    [ObservableProperty]
    private IReadOnlyList<NutConfiguredDriver> _configuredDrivers = Array.Empty<NutConfiguredDriver>();

    [ObservableProperty]
    private NutConfiguredDriver? _selectedConfiguredDriver;

    [ObservableProperty]
    private string? _upsdrvctlPath;

    [ObservableProperty]
    private NutDriverDiagnosticRequest? _pendingDriverDiagnostic;

    private string? _upsConfFingerprint;

    [ObservableProperty]
    private bool _isDriverDiagnosticConfirmed;

    [ObservableProperty]
    private NutDriverDiagnosticResult? _driverDiagnosticResult;

    [ObservableProperty]
    private string? _driverDiagnosticStatusMessage;

    public string SelectedFileName => SelectedFile?.FileName ?? UnavailableText;

    public string SelectedFileStatusText => SelectedFile?.StatusText ?? "Nenhum arquivo selecionado";

    public string SelectedFileEncodingText => _loadedSnapshot is null ? UnavailableText : ToEncodingText(_loadedSnapshot.Encoding);

    public bool HasLoadedFile => _loadedSnapshot is not null;

    public bool HasNoLoadedFile => !HasLoadedFile;

    // While a file is loading there is deliberately no document: the placeholder that invites the
    // user to pick a file would be wrong, so the editor area shows the loading state instead.
    public bool IsEditorPlaceholderVisible => HasNoLoadedFile && !IsLoadingFile;

    private ISemanticConfigurationEditor? ActiveSemanticEditor =>
        UpsConfigurationEditor ?? (ISemanticConfigurationEditor?)NutGeneralConfigurationEditor ?? UpsdConfigurationEditor
        ?? (ISemanticConfigurationEditor?)UpsdUsersConfigurationEditor ?? UpsmonConfigurationEditor;

    public bool HasDraftChanges => _entries.Any(entry => entry.IsChanged) || ActiveSemanticEditor?.HasChanges == true;

    public bool IsUpsConfigurationEditorVisible => UpsConfigurationEditor is not null;

    public bool IsNutGeneralConfigurationEditorVisible => NutGeneralConfigurationEditor is not null;

    public bool IsUpsdConfigurationEditorVisible => UpsdConfigurationEditor is not null;

    public bool IsUpsdUsersConfigurationEditorVisible => UpsdUsersConfigurationEditor is not null;

    public bool IsUpsmonConfigurationEditorVisible => UpsmonConfigurationEditor is not null;

    public bool IsLegacyConfigurationEditorVisible => HasLoadedFile && ActiveSemanticEditor is null;

    public bool HasPreview => _preparedChange is not null;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    private ManagedServerCapabilities Capabilities => _profileContext?.Capabilities ?? new ManagedServerCapabilities(true, true, true, true, true, false);

    public string ManagedProfileName => _profileContext?.Profile.Name ?? Strings.Get("Administration.Context.CurrentLocalProfile");

    public string ManagedProfileMonitoringEndpoint => _profileContext is null
        ? "localhost:3493"
        : $"{_profileContext.Endpoint.Host}:{_profileContext.Endpoint.Port}";

    /// <summary>
    /// Read-only view of the Windows service running NUT on the remote host, present only for a
    /// remote profile. It carries no action: the local service commands on this page act on this
    /// machine, and letting them reach a remote host is exactly the mistake this separation prevents.
    /// </summary>
    public RemoteWindowsServiceViewModel? RemoteWindowsService { get; }

    public bool HasRemoteWindowsService => RemoteWindowsService is not null;

    /// <summary>
    /// Control, kept as its own object so the monitor above stays incapable of acting. A view that
    /// wants a Stop button has to bind to this one, which makes the separation visible in the XAML.
    /// </summary>
    public RemoteWindowsServiceControlViewModel? RemoteWindowsServiceControl { get; }

    public bool HasRemoteWindowsServiceControl => RemoteWindowsServiceControl is not null;

    public bool IsRemoteManagementProfile => _profileContext?.Profile.Management.Mode == NutManagementMode.Remote;

    public bool IsLocalManagementProfile => !IsRemoteManagementProfile;

    public string RemoteConfigurationDirectory => _profileContext?.Profile.Management.ConfigurationTransport == RemoteConfigurationTransportKind.Smb
        ? _profileContext.Profile.Management.SmbConfigurationDirectory ?? _profileContext.Profile.Management.SmbSharePath ?? Strings.Get("Common.NotConfigured")
        : _profileContext?.Profile.Management.RemoteConfigurationDirectory ?? Strings.Get("Common.NotConfigured");

    public string ManagementAvailabilityText => IsRemoteManagementProfile
        ? _remoteManagement?.StatusMessage ?? (_remoteManagement?.IsSmb == true
            ? Strings.Get("Administration.Remote.ConnectSmbPrompt")
            : Strings.Get("Administration.Remote.ConnectSftp"))
        : Strings.Get("Administration.Local.Available");

    public RemoteManagementSessionViewModel? RemoteManagement => _remoteManagement;

    public bool IsRemoteConfigurationReady => _remoteManagement?.CanReadConfiguration == true;

    public bool IsConfigurationEditorVisible => IsLocalManagementProfile || IsRemoteConfigurationReady;

    public bool CanChangeRemoteSessionContext => IsRemoteManagementProfile && !HasDraftChanges && !HasPreview && !IsBusy;

    public bool CanConnectRemote => CanChangeRemoteSessionContext && _remoteManagement?.CanConnect == true;

    public bool CanDisconnectRemote => CanChangeRemoteSessionContext && _remoteManagement?.CanDisconnect == true;

    public bool CanTrustRemoteHostKey => CanChangeRemoteSessionContext && _remoteManagement?.CanTrustHostKey == true;

    public bool CanBrowseRemoteDirectory => CanChangeRemoteSessionContext && _remoteManagement?.CanBrowse == true;

    public bool CanChooseRemoteDirectory => CanChangeRemoteSessionContext && _remoteManagement?.CanChooseDirectory == true;

    public bool CanValidateRemoteDirectory => CanChangeRemoteSessionContext && _remoteManagement?.CanValidateDirectory == true;

    public bool CanUseRemoteDirectory => CanChangeRemoteSessionContext && _remoteManagement?.CanUseCurrentDirectory == true;

    public bool CanProbeRemoteWriteCapability => CanChangeRemoteSessionContext && _remoteManagement?.CanProbeWriteCapability == true;

    public string? RemoteWriteAuthorizationUnavailableTooltip =>
        _profileContext?.Profile.AccessMode == ManagedNutServerAccessMode.ReadOnly
            ? Strings.Get("Administration.Remote.SafeWrite.ReadOnlyTooltip")
            : null;

    public bool RequiresRemoteWriteAuthorization =>
        HasLoadedFile &&
        IsRemoteManagementProfile &&
        _profileContext?.Profile.AccessMode == ManagedNutServerAccessMode.Manage &&
        _remoteManagement is { CanReadConfiguration: true, IsWriteCapabilityUnverified: true };

    public bool HasBackupPath => !string.IsNullOrWhiteSpace(BackupPath);

    public bool HasRecoveryPath => !string.IsNullOrWhiteSpace(RecoveryPath);

    public bool HasTemporaryPath => !string.IsNullOrWhiteSpace(TemporaryPath);

    private bool CanInspectConfiguration => IsRemoteManagementProfile
        ? _remoteManagement?.CanReadConfiguration == true
        : Capabilities.CanInspectLocalManagement;

    private bool CanEditConfiguration => IsRemoteManagementProfile
        ? _remoteManagement?.CanEditConfiguration == true
        : Capabilities.CanEditConfiguration;

    private bool IsRemoteSessionBusy => _remoteManagement?.IsBusy == true;

    public bool CanEditEntries => CanEditConfiguration && HasLoadedFile && !IsBusy && !IsDetectingInstallation && !IsRemoteSessionBusy;

    public bool CanReview => CanEditConfiguration && HasLoadedFile && HasDraftChanges &&
        (ActiveSemanticEditor is null || !ActiveSemanticEditor.HasChanges || ActiveSemanticEditor.CanReview) &&
        !IsBusy && !IsDetectingInstallation && !IsRemoteSessionBusy;

    public bool CanApply => CanEditConfiguration && HasPreview && _preparedDraftVersion == _draftVersion && IsPreviewConfirmed && !IsBusy && !IsDetectingInstallation && !IsRemoteSessionBusy;

    public bool CanDiscard => (HasDraftChanges || HasPreview) && !IsBusy && !IsDetectingInstallation && !IsRemoteSessionBusy;

    public bool CanReload => CanInspectConfiguration && SelectedFile is not null && !HasDraftChanges && !IsBusy && !IsDetectingInstallation && !IsRemoteSessionBusy;

    public bool CanChangeInstallation => Capabilities.CanInspectLocalManagement && !IsDetectingInstallation && !IsBusy && !HasDraftChanges && !HasPreview;

    public bool CanDetectInstallation => CanChangeInstallation;

    // A write, a review or an installation change owns the editor and must not be interrupted, so
    // those still close the list. Loading a file does not: switching files is precisely how the user
    // recovers from a slow or wrong pick, and disabling the list mid-click is what made it stop
    // responding. A superseded load is cancelled instead.
    private bool IsBusyOutsideNavigation => IsBusy && !IsLoadingFile;

    public bool CanSelectConfigurationFile => CanInspectConfiguration && !IsDetectingInstallation && !IsBusyOutsideNavigation && !HasDraftChanges && !HasPreview && !IsRemoteSessionBusy;

    public bool IsWindowsAdministrationAvailable => _windowsAdministration is not null && WindowsPermissionAssessment.State != NutPermissionState.Unknown;

    public bool HasWindowsServices => WindowsServices.Count > 0;
    public bool HasNoWindowsServices => !HasWindowsServices;
    public bool HasSelectedWindowsService => SelectedWindowsService is not null;
    public bool HasNoWindowsProcesses => WindowsProcesses.Count == 0;
    public bool HasNoWindowsEvents => WindowsEvents.Count == 0;

    /// <summary>Event rows with the Windows "description cannot be found" wrapper removed.</summary>
    public IReadOnlyList<WindowsEventRowViewModel> WindowsEventRows => WindowsEvents
        .Select(entry => new WindowsEventRowViewModel(
            NutTimestampPresentation.Local(entry.Timestamp, "g"),
            entry.Level,
            entry.Provider,
            NutEventMessagePresentation.Friendly(entry.Message)))
        .ToArray();

    /// <summary>
    /// Process association needs rights this session may not have: NUT runs as LocalSystem. When
    /// the modules could not be read, say so rather than claiming no NUT process exists.
    /// </summary>
    public bool IsProcessInspectionDenied => _windowsAdministrationSnapshot?.ProcessInspectionDenied == true;

    public string WindowsProcessEmptyText => Strings.Get(IsProcessInspectionDenied
        ? "Administration.Windows.Processes.InspectionDenied"
        : "Administration.Windows.NoProcesses");

    public string SelectedWindowsServiceStateText => SelectedWindowsService?.State switch
    {
        NutServiceState.Running => Strings.Get("ServiceState.Running"),
        NutServiceState.Stopped => Strings.Get("ServiceState.Stopped"),
        NutServiceState.StartPending => Strings.Get("ServiceState.StartPending"),
        NutServiceState.StopPending => Strings.Get("ServiceState.StopPending"),
        NutServiceState.Paused => Strings.Get("ServiceState.Paused"),
        NutServiceState.Failed => Strings.Get("ServiceState.Failed"),
        _ => Strings.Get("Status.Unavailable")
    };

    public string SelectedWindowsServiceStartModeText => SelectedWindowsService?.StartMode switch
    {
        NutServiceStartMode.Automatic => Strings.Get("StartMode.Automatic"),
        NutServiceStartMode.Manual => Strings.Get("StartMode.Manual"),
        NutServiceStartMode.Disabled => Strings.Get("StartMode.Disabled"),
        _ => Strings.Get("Status.Unavailable")
    };

    /// <summary>
    /// A service can be discovered by identity while its binary is not verifiably part of the
    /// detected installation. T16 refuses to mutate such a service, so the commands must reflect
    /// that instead of offering an action that is guaranteed to fail.
    /// </summary>
    public bool IsSelectedWindowsServiceControllable =>
        SelectedWindowsService?.AssociationConfidence == NutAssociationConfidence.BinaryPath;

    public bool IsSelectedWindowsServiceUnverified =>
        SelectedWindowsService is not null && !IsSelectedWindowsServiceControllable;

    /// <summary>Localized explanation of why the service list is in its current state.</summary>
    public string WindowsServiceDiscoveryText => _windowsAdministrationSnapshot switch
    {
        // "Not inspected" must never be reported as "not found": they are different states.
        null => Strings.Get("Administration.Windows.Discovery.NotInspected"),
        { ServiceDiscoveryStatus: NutServiceDiscoveryStatus.AccessDenied } => Strings.Get("Administration.Windows.Discovery.AccessDenied"),
        { ServiceDiscoveryStatus: NutServiceDiscoveryStatus.QueryFailed } => Strings.Get("Administration.Windows.Discovery.QueryFailed"),
        _ when HasWindowsServices => Strings.Get("Administration.Windows.Discovery.Found"),
        _ => Strings.Get("Administration.Windows.Discovery.NotFound")
    };

    /// <summary>
    /// Sanitized technical trace, shown only while no service could be associated so the failing
    /// stage can be identified from a screenshot. It lists the NUT candidate and paths already
    /// visible elsewhere in this page; it never enumerates the machine's services or any secret.
    /// </summary>
    /// <summary>Why the last inspection attempt was skipped, when it was skipped.</summary>
    private string? _windowsInspectionSkipReason;

    private string? WindowsInspectionSkipReason
    {
        get => _windowsInspectionSkipReason;
        set
        {
            _windowsInspectionSkipReason = value;
            OnPropertyChanged(nameof(WindowsServiceDiagnosticText));
            OnPropertyChanged(nameof(HasWindowsServiceDiagnostic));
            OnPropertyChanged(nameof(WindowsServiceDiscoveryText));
        }
    }

    public string? WindowsServiceDiagnosticText
    {
        get
        {
            if (HasWindowsServices) return null;

            // No snapshot means no inspection ever completed. Saying so is the whole point of this
            // block: reporting "no service found" for a state that was never inspected hides the
            // real stage of the failure.
            if (_windowsAdministrationSnapshot is null)
            {
                return string.Join(Environment.NewLine,
                [
                    "Inspeção da administração do Windows: não concluída",
                    $"Motivo: {WindowsInspectionSkipReason ?? "a inspeção ainda não foi executada"}",
                    $"Perfil local: {Yes(IsLocalManagementProfile)}",
                    $"Instalação detectada: {Yes(_currentInstallation is { IsDetected: true })}",
                    $"Raiz da instalação: {_currentInstallation?.InstallationDirectory ?? "—"}"
                ]);
            }

            if (_windowsAdministrationSnapshot.Trace is not { } trace)
            {
                return $"Descoberta: {_windowsAdministrationSnapshot.ServiceDiscoveryStatus} (sem trace técnico)";
            }

            var lines = new List<string>
            {
                $"Plataforma suportada: {Yes(trace.PlatformSupported)}",
                $"Enumeração concluída: {Yes(trace.EnumerationSucceeded)} ({trace.EnumeratedServiceCount})",
                $"Identidade NUT encontrada: {Yes(trace.ExactKnownServiceFound)}",
                $"Raiz da instalação: {trace.InstallationRoot ?? "—"}",
                $"Serviço candidato: {trace.CandidateServiceName ?? "—"}",
                $"Executável: {trace.CandidateExecutable ?? "—"}",
                $"Containment: {trace.ContainmentResult switch { true => "válido", false => "fora da raiz", _ => "não avaliado" }}",
                $"Associação: {trace.Association}"
            };
            if (trace.FailureReason is { } reason) lines.Add($"Motivo: {reason}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public bool HasWindowsServiceDiagnostic => WindowsServiceDiagnosticText is not null;

    private static string Yes(bool value) => value ? "sim" : "não";

    public bool IsWindowsServiceDiscoveryProblem =>
        _windowsAdministrationSnapshot?.ServiceDiscoveryStatus is NutServiceDiscoveryStatus.AccessDenied or NutServiceDiscoveryStatus.QueryFailed;

    // Categorical service state for presentation. Colour is always paired with the state text, and
    // an undetected service reads as unavailable rather than as a failure.
    public bool IsWindowsServiceRunning => SelectedWindowsService?.State == NutServiceState.Running;
    public bool IsWindowsServiceStopped => SelectedWindowsService?.State == NutServiceState.Stopped;
    public bool IsWindowsServiceFailed => SelectedWindowsService?.State == NutServiceState.Failed;
    public bool IsWindowsServiceTransitioning => SelectedWindowsService?.State is NutServiceState.StartPending or NutServiceState.StopPending or NutServiceState.Paused;
    public bool IsWindowsServiceUnknown => SelectedWindowsService is null;

    public bool IsDriverDiagnosticsAvailable => _driverDiagnostics is not null;
    public bool HasConfiguredDrivers => ConfiguredDrivers.Count > 0;

    /// <summary>Read, and it declares none. Only this may be stated as a fact about the file.</summary>
    public bool HasNoConfiguredDrivers => IsConfiguredDriverListKnown && !HasConfiguredDrivers;

    /// <summary>Nobody has read it. Distinct from the file being empty, and never presented as it.</summary>
    public bool IsConfiguredDriverListUnknown => !IsConfiguredDriverListKnown;
    public bool HasSelectedConfiguredDriver => SelectedConfiguredDriver is not null;

    /// <summary>
    /// Only ever true once a source has actually answered. Before that, and whenever the source could
    /// not be asked, the screen says why rather than claiming the machine has no ports.
    /// </summary>
    public bool HasNoComPorts => IsComPortListKnown && DetectedComPorts.Count == 0;

    public bool IsDeviceInspectionAvailable => DeviceInspectionSource != NutDeviceInspectionSource.Unavailable;

    public bool IsDeviceInspectionUnavailable => !IsDeviceInspectionAvailable;

    public bool IsRemoteDeviceInspection => DeviceInspectionSource == NutDeviceInspectionSource.RemoteAgent;

    /// <summary>
    /// Names the source in the operator's own language. A remote reading is never described as a
    /// local diagnostic: which machine was examined is the first thing that has to be unambiguous.
    /// </summary>
    public string DeviceInspectionSourceText => DeviceInspectionSource switch
    {
        NutDeviceInspectionSource.RemoteAgent => Strings.Get("Administration.Drivers.SourceRemoteAgent"),
        NutDeviceInspectionSource.Local => Strings.Get("Administration.Drivers.SourceLocal"),
        _ => Strings.Get("Administration.Drivers.Unavailable")
    };

    /// <summary>
    /// The whole point of the remote view: active driver diagnostics stay local, and the screen says
    /// so instead of presenting buttons that would have to be refused.
    /// </summary>
    public bool AreActiveDiagnosticsAvailable => IsLocalManagementProfile && IsDriverDiagnosticsAvailable;

    /// <summary>
    /// Selecting a configured driver is inspection, not action, so it follows the inspection source
    /// rather than the diagnostics capability. A read-only or remote profile can still look at what
    /// is configured and how it relates to what was detected; what it cannot do is run anything.
    /// </summary>
    public bool CanSelectConfiguredDriver => IsDeviceInspectionAvailable && !IsBusy && !HasPendingAdministrativeAction;

    /// <summary>
    /// Distinguishes "configured in ups.conf" from "currently enumerated by Windows" so a port that
    /// exists in configuration but is absent right now reads as a state rather than a contradiction.
    /// </summary>
    public bool IsSelectedDriverPortPresent => SelectedConfiguredDriver?.IsConfiguredComPortPresent == true;

    public bool HasSelectedDriverPortState => SelectedConfiguredDriver?.NormalizedComPort is not null && IsComPortListKnown;

    /// <summary>
    /// Whether the configured port is one the inspected machine currently exposes, and which machine
    /// that was. The remote wording names the server on purpose: "not detected" about the operator's
    /// own workstation would be a true statement about the wrong computer.
    /// </summary>
    public string SelectedDriverPortStateText => SelectedConfiguredDriver?.NormalizedComPort is null
        ? Strings.Get("Status.Unavailable")
        : Strings.Get((IsSelectedDriverPortPresent, IsRemoteDeviceInspection) switch
        {
            (true, true) => "Administration.Drivers.PortDetectedOnServer",
            (false, true) => "Administration.Drivers.PortNotDetectedOnServer",
            (true, false) => "Administration.Drivers.PortConfiguredAndDetected",
            _ => "Administration.Drivers.PortConfiguredNotDetected"
        });

    public string SelectedConfiguredDriverStateText => SelectedConfiguredDriver?.Executable.State switch
    {
        NutDriverExecutableState.Available => Strings.Get("DriverState.Available"),
        NutDriverExecutableState.Missing => Strings.Get("DriverState.Missing"),
        NutDriverExecutableState.Untrusted => Strings.Get("DriverState.Untrusted"),
        NutDriverExecutableState.InvalidName => Strings.Get("DriverState.InvalidName"),
        NutDriverExecutableState.NotApplicable => Strings.Get("DriverState.NotApplicable"),
        _ => Strings.Get("Status.Unavailable")
    };

    public bool HasPendingDriverDiagnostic => PendingDriverDiagnostic is not null;

    public bool HasDriverDiagnosticResult => DriverDiagnosticResult is not null;

    public string PendingDriverDiagnosticText => PendingDriverDiagnostic is null
        ? "Nenhum diagnóstico pendente"
        : ToDriverDiagnosticText(PendingDriverDiagnostic.Kind);

    public bool PendingDriverDiagnosticContactsHardware => PendingDriverDiagnostic?.Kind == NutDriverDiagnosticKind.DriverDataDump;

    public string PendingDriverDiagnosticTool => PendingDriverDiagnostic?.Kind switch
    {
        NutDriverDiagnosticKind.UpsdrvctlHelp or NutDriverDiagnosticKind.UpsdrvctlList or NutDriverDiagnosticKind.UpsdrvctlStatus or NutDriverDiagnosticKind.UpsdrvctlDryRunStart => UpsdrvctlPath ?? UnavailableText,
        _ => PendingDriverDiagnostic?.Driver?.Executable.Path ?? UnavailableText
    };

    public string PendingDriverDiagnosticUpsName => PendingDriverDiagnostic?.Driver?.UpsName ?? "Não aplicável";

    public string PendingDriverDiagnosticPort => PendingDriverDiagnostic?.Driver?.NormalizedComPort ?? PendingDriverDiagnostic?.Driver?.ConfiguredPort ?? "Não aplicável";

    public string PendingDriverDiagnosticHardwareText => PendingDriverDiagnosticContactsHardware ? "Sim" : "Não";

    public string NutServiceStateForDriverDiagnostic => WindowsServices.Any(service => service.State == NutServiceState.Running) ? "Em execução" : WindowsServices.Any(service => service.State == NutServiceState.Stopped) ? "Parado" : "Indisponível";

    public bool HasPendingAdministrativeAction => PendingAdministrativeAction is not null;

    public string PendingAdministrativeActionText => PendingAdministrativeAction?.Action switch
    {
        NutAdministrativeAction.StartService => "Iniciar serviço",
        NutAdministrativeAction.StopService => "Parar serviço",
        NutAdministrativeAction.RestartService => "Reiniciar serviço",
        NutAdministrativeAction.RepairConfigurationPermissions => "Corrigir permissões de configuração",
        _ => "Nenhuma ação administrativa pendente"
    };

    public bool CanPrepareAdministrativeAction => Capabilities.CanExecuteAdministrativeActions && !IsBusy && !IsDetectingInstallation && !HasDraftChanges && !HasPreview && !HasPendingDriverDiagnostic && _currentInstallation is { IsDetected: true } && _windowsAdministration is not null;

    public bool CanExecuteAdministrativeAction => Capabilities.CanExecuteAdministrativeActions && HasPendingAdministrativeAction && IsAdministrativeActionConfirmed && !HasDraftChanges && !HasPreview && !IsBusy && !IsDetectingInstallation && IsPendingAdministrativeActionCurrent();

    // All three gates additionally require a controllable service: T16 refuses to mutate a service
    // whose binary is not verifiably part of the detected installation, so offering the action
    // would present a target that cannot actually be acted on.
    public bool CanStartWindowsService => CanPrepareAdministrativeAction && IsSelectedWindowsServiceControllable && SelectedWindowsService is { State: NutServiceState.Stopped, StartMode: not NutServiceStartMode.Disabled };

    public bool CanStopWindowsService => CanPrepareAdministrativeAction && IsSelectedWindowsServiceControllable && SelectedWindowsService?.State == NutServiceState.Running;

    public bool CanRestartWindowsService => CanPrepareAdministrativeAction && IsSelectedWindowsServiceControllable && SelectedWindowsService is { StartMode: not NutServiceStartMode.Disabled } service && service.State is (NutServiceState.Running or NutServiceState.Stopped);

    public bool CanRefreshDriverDiagnostics => Capabilities.CanInspectLocalManagement && _driverDiagnostics is not null && !IsBusy && !IsDetectingInstallation && !HasDraftChanges && !HasPreview && !HasPendingAdministrativeAction;

    /// <summary>
    /// Whether the agent can be asked at all from here. It requires a remote profile and a client;
    /// whether that particular agent offers hardware inspection is settled by its handshake, not by
    /// this gate, because a gate cannot know what a server it has not spoken to supports.
    /// </summary>
    public bool CanRefreshRemoteDeviceInspection => IsRemoteManagementProfile && _agentClient is not null &&
        !IsBusy && !IsDetectingInstallation && !HasDraftChanges && !HasPreview && !HasPendingAdministrativeAction;

    /// <summary>One Refresh button, routed by profile. The screen is the same; the source is not.</summary>
    public bool CanRefreshDeviceInspection => IsRemoteManagementProfile
        ? CanRefreshRemoteDeviceInspection
        : CanRefreshDriverDiagnostics;

    public bool CanPrepareDriverDiagnostic => Capabilities.CanRunDriverDiagnostics && _driverDiagnostics is not null && !IsBusy && !IsDetectingInstallation && !HasDraftChanges && !HasPreview && !HasPendingAdministrativeAction && _currentInstallation is { IsDetected: true };

    public bool CanExecuteDriverDiagnostic => Capabilities.CanRunDriverDiagnostics && HasPendingDriverDiagnostic && IsDriverDiagnosticConfirmed && !IsBusy && !IsDetectingInstallation && !HasDraftChanges && !HasPreview && !HasPendingAdministrativeAction && IsPendingDriverDiagnosticCurrent();

    public bool IsDriverDiagnosticCritical => DriverDiagnosticResult?.Status is NutDriverDiagnosticStatus.Conflict or NutDriverDiagnosticStatus.Failed or NutDriverDiagnosticStatus.Timeout or NutDriverDiagnosticStatus.CleanupFailed;

    public string DriverDiagnosticCriticalText => "CRÍTICO — o resultado do diagnóstico requer atenção manual.";

    public string AdministrativeCriticalText => Strings.Get("Administration.Windows.CriticalNotice");

    public bool IsPermissionRepairPending => PendingAdministrativeAction?.Action == NutAdministrativeAction.RepairConfigurationPermissions;

    public string PendingPermissionIdentity => PendingAdministrativeAction?.PermissionRepairPlan?.UserIdentity ?? UnavailableText;

    public string PendingPermissionSid => PendingAdministrativeAction?.PermissionRepairPlan?.UserSid ?? UnavailableText;

    public string PendingPermissionDirectory => PendingAdministrativeAction?.PermissionRepairPlan?.ConfigurationDirectory ?? UnavailableText;

    public IReadOnlyList<string> PendingPermissionTargets => PendingAdministrativeAction?.PermissionRepairPlan?.AffectedPaths ?? Array.Empty<string>();

    public string CriticalResultText => "CRÍTICO — a configuração pode necessitar recuperação manual.";

    public event Action<SemanticConfigurationReviewViewModel?>? SemanticReviewChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsRemoteManagementProfile)
        {
            InstallationStatusText = "Gerenciamento remoto não conectado";
            SetStatus(ManagementAvailabilityText);

            // The devices view is the one part of this page a remote profile can populate without a
            // configuration session, because it asks the agent rather than the file transport.
            await RefreshRemoteDeviceInspectionAsync(cancellationToken);
            return;
        }

        await RefreshInstallationAsync(cancellationToken);
        await RefreshWindowsAdministrationAsync(cancellationToken);
        await RefreshDriverDiagnosticsAsync(cancellationToken);
    }

    public async Task RefreshInstallationAsync(CancellationToken cancellationToken = default)
    {
        if (IsRemoteManagementProfile)
        {
            SetStatus(ManagementAvailabilityText);
            return;
        }

        if (!CanChangeInstallation)
        {
            SetInstallationChangeBlockedStatus();
            return;
        }

        if (_installationDetector is null)
        {
            ApplyInstallation(NutInstallationInfo.NotDetected());
            SetStatus("A detecção local não está disponível.");
            return;
        }

        IsDetectingInstallation = true;
        SetStatus(null);
        var detectionDraftVersion = _draftVersion;
        var detectionInstallationContextVersion = _installationContextVersion;
        try
        {
            var installation = await _installationDetector.DetectAsync(cancellationToken);
            TryApplyDetectedInstallation(installation, detectionDraftVersion, detectionInstallationContextVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("A detecção da instalação foi cancelada.");
        }
        catch (Exception)
        {
            if (TryApplyDetectedInstallation(NutInstallationInfo.NotDetected(), detectionDraftVersion, detectionInstallationContextVersion))
            {
                SetStatus("Não foi possível detectar a instalação local do NUT.");
            }
        }
        finally
        {
            IsDetectingInstallation = false;
        }
    }

    public async Task InspectInstallationDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (IsRemoteManagementProfile)
        {
            SetStatus(ManagementAvailabilityText);
            return;
        }

        if (!CanChangeInstallation)
        {
            SetInstallationChangeBlockedStatus();
            return;
        }

        if (_installationDetector is null)
        {
            SetStatus("A detecção local não está disponível.");
            return;
        }

        IsDetectingInstallation = true;
        SetStatus(null);
        var detectionDraftVersion = _draftVersion;
        var detectionInstallationContextVersion = _installationContextVersion;
        try
        {
            var installation = await _installationDetector.InspectDirectoryAsync(directory, cancellationToken);
            TryApplyDetectedInstallation(installation, detectionDraftVersion, detectionInstallationContextVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("A inspeção da pasta foi cancelada.");
        }
        catch (Exception)
        {
            SetStatus("Não foi possível inspecionar a pasta selecionada.");
        }
        finally
        {
            IsDetectingInstallation = false;
        }
    }

    public async Task SelectFileAsync(NutConfigurationFileItemViewModel? file, CancellationToken cancellationToken = default)
    {
        if (!CanInspectConfiguration)
        {
            SetStatus(ManagementAvailabilityText);
            return;
        }

        if (file is null || ReferenceEquals(file, SelectedFile) && file.IsManaged)
        {
            return;
        }

        if (!ConfigurationFiles.Contains(file))
        {
            SetStatus(Strings.Get("Administration.File.NotEnabled"));
            return;
        }

        if (HasDraftChanges || HasPreview)
        {
            SetStatus("Aplique ou descarte as alterações antes de trocar de arquivo.");
            OnPropertyChanged(nameof(SelectedFile));
            return;
        }

        // Only work that owns the editor blocks a new pick. A load already in flight does not: it is
        // superseded below.
        if (IsBusyOutsideNavigation || IsDetectingInstallation)
        {
            SetStatus("Aguarde a operação atual antes de trocar de arquivo.");
            OnPropertyChanged(nameof(SelectedFile));
            return;
        }

        if (!file.IsManaged)
        {
            // A stale request can arrive after a profile edit or restored navigation state. Never
            // leave a disabled tile current: migrate deterministically to the first managed file
            // that can be loaded, or clear the editor when none exists. No disabled file is handed
            // to the pipeline.
            var fallback = ConfigurationFiles.FirstOrDefault(candidate => candidate.IsManaged && candidate.CanLoad);
            if (fallback is not null)
            {
                await SelectFileAsync(fallback, cancellationToken);
            }
            else
            {
                ClearLoadedDocument(clearSelectedFile: true);
                SetStatus(Strings.Get("Administration.File.NotEnabled"));
            }

            return;
        }

        if (!file.CanLoad)
        {
            SetStatus(file.State switch
            {
                NutConfigurationFileState.Missing => "O arquivo não existe neste diretório.",
                NutConfigurationFileState.AccessDenied => "Permissão insuficiente. A elevação administrativa será tratada pela etapa de administração do Windows.",
                _ => "O arquivo não está disponível para carregamento."
            });
            return;
        }

        // The highlight follows the click before the load starts, so the file header and the list
        // always agree on which file is being opened.
        SelectedFile = file;
        await LoadSelectedFileAsync(file, file.FullPath!, file.FileKind, _installationContextVersion, cancellationToken);
    }

    public async Task ReviewChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!CanReview || _loadedSnapshot is null || _configurationPipeline is null)
        {
            return;
        }

        IsBusy = true;
        InvalidatePreview();
        SetStatus(null);
        try
        {
            var reloaded = await _configurationPipeline.LoadAsync(
                _loadedSnapshot.TargetPath,
                _loadedSnapshot.FileKind,
                cancellationToken);
            if (reloaded.Status != NutConfigurationLoadStatus.Success || reloaded.Snapshot is null)
            {
                SetLoadFailureStatus(reloaded.Status);
                return;
            }

            if (!MatchesLoadedSnapshot(reloaded.Snapshot, _loadedSnapshot))
            {
                SetStatus("O arquivo foi alterado externamente desde que foi carregado.");
                return;
            }

            NutConfigurationPreparedChange prepared;
            if (ActiveSemanticEditor?.HasChanges == true)
            {
                if (!ActiveSemanticEditor.CanReview)
                {
                    SetStatus(Strings.Get("Config.Validation.ResolveBeforeReview"));
                    return;
                }
                var generated = ActiveSemanticEditor.Prepare(_configurationPipeline);
                prepared = generated.PreparedChange;
                SemanticReview = new SemanticConfigurationReviewViewModel(generated, ActiveSemanticEditor.Draft.Projection, Strings);
                SemanticReviewChanged?.Invoke(SemanticReview);
            }
            else if (!TryApplyDrafts(reloaded.Snapshot.Document))
            {
                SetStatus("O arquivo foi alterado externamente ou não é mais compatível com as alterações em edição.");
                return;
            }
            else
            {
                prepared = _configurationPipeline.Prepare(reloaded.Snapshot);
            }
            if (!prepared.HasChanges)
            {
                SetStatus("Não há alterações para revisar.");
                return;
            }

            _preparedChange = prepared;
            _preparedDraftVersion = _draftVersion;
            PreviewLines = prepared.Preview.Lines
                .Select(line => new NutConfigurationPreviewLineViewModel(line.LineNumber, line.OriginalText, line.CandidateText, line.IsRedacted))
                .ToArray();
            NotifyWorkflowPropertiesChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("A revisão das alterações foi cancelada.");
        }
        catch (Exception)
        {
            SetStatus("Não foi possível preparar a revisão das alterações.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ApplyChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!CanApply || _preparedChange is null || _configurationPipeline is null)
        {
            return;
        }

        IsBusy = true;
        SetStatus(null);
        BackupPath = null;
        RecoveryPath = null;
        TemporaryPath = null;
        try
        {
            var result = await _configurationPipeline.ApplyAsync(_preparedChange, cancellationToken);
            BackupPath = result.BackupPath;
            RecoveryPath = result.RecoveryPath;
            TemporaryPath = result.TemporaryPath;
            ApplyResultStatus(result);
            if (result.Status == NutConfigurationApplyStatus.RemoteCommitOutcomeUnknown)
            {
                _remoteManagement?.InvalidateWriteCapabilityAfterUncertainOutcome();
            }

            if (result.Status == NutConfigurationApplyStatus.Success)
            {
                var successMessage = StatusMessage;
                await LoadSelectedFileAsync(CancellationToken.None, preserveStatus: true);
                SetStatus(successMessage);
            }
            else
            {
                InvalidatePreview();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("A aplicação das alterações foi cancelada.");
        }
        catch (Exception)
        {
            SetStatus("Não foi possível aplicar a configuração.", critical: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ReloadSelectedFileAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || IsDetectingInstallation)
        {
            SetStatus("Aguarde a operação atual antes de recarregar o arquivo.");
            return;
        }

        if (HasDraftChanges)
        {
            SetStatus("Há alterações locais. Descarte-as antes de recarregar o arquivo.");
            return;
        }

        await LoadSelectedFileAsync(cancellationToken);
    }

    public async Task DiscardChangesAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || IsDetectingInstallation)
        {
            SetStatus("Aguarde a operação atual antes de descartar alterações.");
            return;
        }

        if (SelectedFile is null)
        {
            return;
        }

        InvalidatePreview();
        foreach (var entry in _entries)
        {
            entry.ResetDraft();
        }

        ActiveSemanticEditor?.Reset();

        await LoadSelectedFileAsync(cancellationToken);
    }

    public async Task RefreshWindowsAdministrationAsync(CancellationToken cancellationToken = default)
    {
        if (!Capabilities.CanInspectLocalManagement)
        {
            WindowsInspectionSkipReason = "capability: CanInspectLocalManagement = false";
            AdministrativeStatusMessage = ManagementAvailabilityText;
            return;
        }

        if (_windowsAdministration is null)
        {
            // The capability was not provided to this session. That is distinct from the host
            // operating system being unable to support local administration at all.
            WindowsInspectionSkipReason = "capability: nenhuma implementação de administração Windows nesta sessão";
            var unavailable = Strings.Get("Administration.Windows.CapabilityUnavailable");
            WindowsPermissionAssessment = NutPermissionAssessment.NotDetermined(unavailable);
            AdministrativeStatusMessage = unavailable;
            return;
        }

        // This guard used to return silently, leaving the page reporting "no service found" for a
        // state where nothing had actually been inspected. Record why so the reason is visible.
        if (IsBusy || IsDetectingInstallation)
        {
            WindowsInspectionSkipReason = $"ocupado: IsBusy={IsBusy}, IsDetectingInstallation={IsDetectingInstallation}";
            return;
        }

        WindowsInspectionSkipReason = null;
        IsBusy = true;
        try
        {
            await LoadWindowsAdministrationAsync(cancellationToken);
            InvalidateAdministrativeAction();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AdministrativeStatusMessage = "A atualização da administração do Windows foi cancelada.";
        }
        catch
        {
            // A failed *read* of the local administration state is an unavailable capability, not a
            // critical administrative condition. Critical stays reserved for an attempted action
            // that failed or needs manual intervention, so the banner keeps its meaning.
            AdministrativeStatusMessage = "Não foi possível atualizar a administração local do Windows.";
        }
        finally { IsBusy = false; }
    }

    public async Task RefreshDriverDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefreshDriverDiagnostics)
        {
            return;
        }

        IsBusy = true;
        var contextVersion = _installationContextVersion;
        try
        {
            var snapshot = await _driverDiagnostics!.InspectAsync(_currentInstallation ?? NutInstallationInfo.NotDetected(), cancellationToken);
            if (contextVersion != _installationContextVersion)
            {
                return;
            }

            DeviceInspectionSource = NutDeviceInspectionSource.Local;
            ApplyComPorts(snapshot.ComPorts, snapshot.IsPlatformSupported);
            ApplyConfiguredDrivers(snapshot.ConfiguredDrivers, known: true);
            UpsdrvctlPath = snapshot.UpsdrvctlPath;
            _upsConfFingerprint = snapshot.UpsConfFingerprint;
            DriverDiagnosticStatusMessage = snapshot.DiagnosticMessage;
            DriverDiagnosticResult = null;
            InvalidateDriverDiagnostic();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DriverDiagnosticStatusMessage = "A atualização de dispositivos e drivers foi cancelada.";
        }
        catch
        {
            DriverDiagnosticStatusMessage = "Não foi possível atualizar os dispositivos e drivers do NUT.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One read-only reading of the managed server's serial hardware, through the agent.
    ///
    /// Two exchanges, in this order and for this reason. The handshake first, because whether that
    /// agent offers hardware inspection at all is a fact only it can state — an agent built before
    /// this capability existed does not advertise it, and the honest response to that is to say the
    /// server cannot be inspected rather than to send it a request it will refuse. Then the snapshot.
    ///
    /// Nothing here can act on the server. The client has one method for this and it names no port,
    /// no speed and no command; there is no path from this method to opening a device, running a
    /// driver, or writing a configuration file.
    /// </summary>
    public async Task RefreshRemoteDeviceInspectionAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefreshRemoteDeviceInspection)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var host = _profileContext?.Endpoint.Host ?? string.Empty;
            var handshake = await _agentClient!.HandshakeAsync(host, cancellationToken);
            var observation = NutAgentObservation.From(host, handshake, null, DateTimeOffset.UtcNow);

            if (!observation.AgentReachable)
            {
                SetRemoteInspectionUnavailable(Strings.Get("Administration.Drivers.RemoteAgentUnavailable"));
                return;
            }

            // Read from the advertised capability list rather than from a version string. An agent
            // whose control is unavailable — no pinned NUT service, no usable audit sink — still
            // enumerates devices perfectly well, and a version test would hide that.
            if (!observation.Advertises(NutAgentOperation.GetHardwareSnapshot))
            {
                SetRemoteInspectionUnavailable(Strings.Get("Administration.Drivers.RemoteCapabilityMissing"));
                return;
            }

            var snapshot = await _agentClient.GetHardwareSnapshotAsync(host, cancellationToken);
            if (!snapshot.Succeeded || snapshot.Value is not { } hardware)
            {
                SetRemoteInspectionUnavailable(Strings.Get("Administration.Drivers.RemoteSnapshotFailed"));
                return;
            }

            DeviceInspectionSource = NutDeviceInspectionSource.RemoteAgent;
            ApplyComPorts(hardware.ComPorts, hardware.EnumerationSucceeded);

            // The agent answered and said it could not enumerate. That is not an empty machine, and
            // the difference survives all the way to the screen.
            DriverDiagnosticStatusMessage = hardware.EnumerationSucceeded
                ? hardware.Detail
                : Strings.Get("Administration.Drivers.RemoteEnumerationFailed");

            await LoadRemoteConfiguredDriversAsync(
                hardware.EnumerationSucceeded ? hardware.ComPorts : null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DriverDiagnosticStatusMessage = Strings.Get("Administration.Drivers.RefreshCancelled");
        }
        catch
        {
            SetRemoteInspectionUnavailable(Strings.Get("Administration.Drivers.RemoteSnapshotFailed"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reads the managed server's <c>ups.conf</c> through the configuration transport that already
    /// owns it, purely to relate what is configured to what was detected.
    ///
    /// This is a load and nothing else. It introduces no second reader, no writer, and no path that
    /// could modify the document: the port relationship is reported so an operator can see it, and
    /// acting on it stays where it belongs, in the graphical editor and its safe-write pipeline.
    /// </summary>
    private async Task LoadRemoteConfiguredDriversAsync(
        IReadOnlyList<NutComPortInfo>? detectedPorts,
        CancellationToken cancellationToken)
    {
        var upsConf = ConfigurationFiles.FirstOrDefault(file => file.FileKind == NutConfigurationFileKind.UpsConf);
        if (_configurationPipeline is null || upsConf is not { IsManaged: true, FullPath: { } path })
        {
            // No remote session, or the profile does not manage ups.conf. Nothing opened the file, so
            // nothing may be said about its contents — reporting an empty list here would describe a
            // file with no sections, which is a different thing and may well be false.
            ApplyConfiguredDrivers(Array.Empty<NutConfiguredDriver>(), known: false);
            return;
        }

        var load = await _configurationPipeline.LoadAsync(path, NutConfigurationFileKind.UpsConf, cancellationToken);
        if (load.Status != NutConfigurationLoadStatus.Success || load.Snapshot is null)
        {
            // Reached for and refused. Still not an answer about what the file contains.
            ApplyConfiguredDrivers(Array.Empty<NutConfiguredDriver>(), known: false);
            return;
        }

        var drivers = NutRemoteConfiguredDriverReader.Read(load.Snapshot.Document, detectedPorts);
        ApplyConfiguredDrivers(drivers, known: true);
    }

    /// <summary>
    /// The server could not be inspected, and the screen has to say that rather than show an empty
    /// port list. The known-list flag is cleared with the ports, which is what stops a configured
    /// port from reading as absent when the truth is that nobody was able to look.
    /// </summary>
    private void SetRemoteInspectionUnavailable(string message)
    {
        DeviceInspectionSource = NutDeviceInspectionSource.Unavailable;
        ApplyComPorts(Array.Empty<NutComPortInfo>(), known: false);
        ApplyConfiguredDrivers(Array.Empty<NutConfiguredDriver>(), known: false);
        DriverDiagnosticStatusMessage = message;
    }

    /// <summary>Publishes one port list and whether it is an answer, so the two can never disagree.</summary>
    private void ApplyComPorts(IReadOnlyList<NutComPortInfo> ports, bool known)
    {
        ComPorts = ports;
        IsComPortListKnown = known;
    }

    /// <summary>
    /// The same for the configured drivers: one list, and whether anyone actually read the file it
    /// claims to describe. Published together for the same reason — set apart, the two drift, and an
    /// unread file starts reporting itself as an empty one.
    /// </summary>
    private void ApplyConfiguredDrivers(IReadOnlyList<NutConfiguredDriver> drivers, bool known)
    {
        ConfiguredDrivers = drivers;
        IsConfiguredDriverListKnown = known;
        SelectedConfiguredDriver = drivers.Count == 1 ? drivers[0] : null;
    }

    public void PrepareDriverDiagnostic(NutDriverDiagnosticKind kind)
    {
        if (!CanPrepareDriverDiagnostic)
        {
            DriverDiagnosticStatusMessage = HasDraftChanges || HasPreview
                ? "Aplique ou descarte as alterações antes de executar diagnósticos do NUT."
                : "O diagnóstico não está disponível no contexto atual.";
            return;
        }

        var requiresDriver = kind is not NutDriverDiagnosticKind.UpsdrvctlHelp;
        if (requiresDriver && SelectedConfiguredDriver is null)
        {
            DriverDiagnosticStatusMessage = "Selecione um dispositivo configurado antes de preparar o diagnóstico.";
            return;
        }

        if (kind == NutDriverDiagnosticKind.DriverDataDump && !CanPrepareHardwareDiagnostic(SelectedConfiguredDriver))
        {
            return;
        }

        PendingDriverDiagnostic = new NutDriverDiagnosticRequest(
            kind,
            _currentInstallation!.InstallationDirectory!,
            _currentInstallation.ConfigurationDirectory!,
            SelectedConfiguredDriver,
            kind == NutDriverDiagnosticKind.UpsdrvctlHelp ? null : _upsConfFingerprint);
        IsDriverDiagnosticConfirmed = false;
        DriverDiagnosticStatusMessage = null;
        DriverDiagnosticResult = null;
        NotifyDriverDiagnosticPropertiesChanged();
    }

    public async Task ExecuteDriverDiagnosticAsync(CancellationToken cancellationToken = default)
    {
        if (!CanExecuteDriverDiagnostic || PendingDriverDiagnostic is null || _driverDiagnostics is null)
        {
            return;
        }

        var request = PendingDriverDiagnostic;
        IsBusy = true;
        try
        {
            var result = await _driverDiagnostics.ExecuteAsync(request, cancellationToken);
            if (!IsPendingDriverDiagnosticCurrent(request))
            {
                return;
            }

            DriverDiagnosticResult = result;
            DriverDiagnosticStatusMessage = result.Message;
            InvalidateDriverDiagnostic();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DriverDiagnosticStatusMessage = "O diagnóstico foi cancelado antes de iniciar.";
        }
        catch
        {
            DriverDiagnosticStatusMessage = "Não foi possível executar o diagnóstico do NUT.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void PrepareServiceAction(NutAdministrativeAction action)
    {
        if (!CanPrepareAdministrativeAction || SelectedWindowsService is not { IsAssociated: true } service || action is not (NutAdministrativeAction.StartService or NutAdministrativeAction.StopService or NutAdministrativeAction.RestartService))
        {
            AdministrativeStatusMessage = HasDraftChanges || HasPreview ? "Aplique ou descarte as alterações de configuração antes de executar uma ação administrativa." : "A ação administrativa não está disponível no contexto atual.";
            return;
        }

        PendingAdministrativeAction = new NutAdministrativeActionRequest(Guid.NewGuid(), action, _currentInstallation!.InstallationDirectory!, _currentInstallation.ConfigurationDirectory!, service.ServiceName);
        IsAdministrativeActionConfirmed = false;
        AdministrativeStatusMessage = null;
        IsAdministrativeCritical = false;
        NotifyAdministrativePropertiesChanged();
    }

    public void PreparePermissionRepair()
    {
        if (!CanPrepareAdministrativeAction || WindowsPermissionAssessment is not { UserSid: { Length: > 0 } sid, Identity: { Length: > 0 } identity, HasExplicitDeny: false } assessment)
        {
            AdministrativeStatusMessage = "As permissões não podem ser corrigidas automaticamente neste contexto.";
            return;
        }

        var effectiveIdentities = assessment.EffectiveIdentitySids ?? [sid];
        var plan = new NutPermissionRepairPlan(_currentInstallation!.ConfigurationDirectory!, identity, sid, assessment.AffectedPaths, EffectiveIdentitySids: effectiveIdentities);
        PendingAdministrativeAction = new NutAdministrativeActionRequest(Guid.NewGuid(), NutAdministrativeAction.RepairConfigurationPermissions, _currentInstallation.InstallationDirectory!, _currentInstallation.ConfigurationDirectory!, PermissionRepairPlan: plan);
        IsAdministrativeActionConfirmed = false;
        AdministrativeStatusMessage = null;
        IsAdministrativeCritical = false;
        NotifyAdministrativePropertiesChanged();
    }

    public async Task ExecuteAdministrativeActionAsync(CancellationToken cancellationToken = default)
    {
        if (!CanExecuteAdministrativeAction || PendingAdministrativeAction is null || _windowsAdministration is null) return;
        IsBusy = true;
        try
        {
            var result = await _windowsAdministration.ExecuteAsync(PendingAdministrativeAction, cancellationToken);
            AdministrativeStatusMessage = result.Message;
            IsAdministrativeCritical = result.Status is NutAdministrativeActionStatus.Failed or NutAdministrativeActionStatus.ManualInterventionRequired;
            InvalidateAdministrativeAction();
            try { await LoadWindowsAdministrationAsync(CancellationToken.None); }
            catch { AdministrativeStatusMessage = result.IsSuccess ? "A ação foi concluída, mas não foi possível atualizar o estado." : result.Message; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AdministrativeStatusMessage = "A ação administrativa foi cancelada.";
        }
        catch
        {
            AdministrativeStatusMessage = "Não foi possível executar a ação administrativa.";
            IsAdministrativeCritical = true;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task DetectInstallationAsync() => RefreshInstallationAsync();

    [RelayCommand]
    private Task ReviewAsync() => ReviewChangesAsync();

    [RelayCommand]
    private Task ApplyAsync() => ApplyChangesAsync();

    [RelayCommand]
    private Task ReloadAsync() => ReloadSelectedFileAsync();

    [RelayCommand]
    private Task DiscardAsync() => DiscardChangesAsync();

    [RelayCommand]
    private Task RefreshWindowsAdministration() => RefreshWindowsAdministrationAsync();

    // One button, two sources, no fallback: a remote profile never quietly inspects this machine
    // instead, and a local profile never reaches for an agent.
    [RelayCommand]
    private Task RefreshDeviceInspection() => IsRemoteManagementProfile
        ? RefreshRemoteDeviceInspectionAsync()
        : RefreshDriverDiagnosticsAsync();

    [RelayCommand]
    private Task RefreshDriverDiagnostics() => RefreshDriverDiagnosticsAsync();

    [RelayCommand]
    private Task ExecuteAdministrativeAction() => ExecuteAdministrativeActionAsync();

    [RelayCommand]
    private Task ExecuteDriverDiagnostic() => ExecuteDriverDiagnosticAsync();

    private async Task LoadSelectedFileAsync(CancellationToken cancellationToken, bool preserveStatus = false)
    {
        if (SelectedFile is not { CanLoad: true, FullPath: { } path } file || _configurationPipeline is null)
        {
            return;
        }

        await LoadSelectedFileAsync(file, path, file.FileKind, _installationContextVersion, cancellationToken, preserveStatus);
    }

    private async Task LoadSelectedFileAsync(
        NutConfigurationFileItemViewModel expectedFile,
        string expectedPath,
        NutConfigurationFileKind expectedFileKind,
        int expectedInstallationContextVersion,
        CancellationToken cancellationToken,
        bool preserveStatus = false)
    {
        if (_configurationPipeline is null)
        {
            return;
        }

        // Each selection supersedes the one before it. The generation decides who may publish and
        // who must stay silent; the token stops the superseded load from doing work nobody wants.
        var generation = ++_navigationGeneration;
        _navigationCancellation?.Cancel();
        var navigation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _navigationCancellation = navigation;

        IsLoadingFile = true;
        IsBusy = true;
        // The previous editor belongs to the previous file. Leaving it on screen under the new
        // file's header would present it as the new file's content.
        ClearLoadedDocument();
        if (!preserveStatus)
        {
            SetStatus(null);
            BackupPath = null;
            RecoveryPath = null;
            TemporaryPath = null;
        }

        try
        {
            var result = await _configurationPipeline.LoadAsync(expectedPath, expectedFileKind, navigation.Token);
            if (generation != _navigationGeneration ||
                !IsCurrentLoadTarget(expectedFile, expectedPath, expectedFileKind, expectedInstallationContextVersion))
            {
                return;
            }

            if (result.Status != NutConfigurationLoadStatus.Success || result.Snapshot is null)
            {
                ClearLoadedDocument();
                SetLoadFailureStatus(result.Status);
                return;
            }

            EditorBuildResult? editors = await BuildEditorsAsync(result.Snapshot, navigation.Token);
            if (generation != _navigationGeneration ||
                !IsCurrentLoadTarget(expectedFile, expectedPath, expectedFileKind, expectedInstallationContextVersion))
            {
                // This generation no longer owns the screen. Its locally-built editors have never
                // been published, so disposing them cannot disturb the newer selection.
                editors.Dispose();
                return;
            }

            if (navigation.IsCancellationRequested)
            {
                editors.Dispose();
                navigation.Token.ThrowIfCancellationRequested();
            }

            PublishEditors(result.Snapshot, expectedFile, editors);
            InvalidatePreview();
            OnPropertyChanged(nameof(SelectedFileEncodingText));
            OnPropertyChanged(nameof(HasLoadedFile));
            OnPropertyChanged(nameof(HasNoLoadedFile));
            NotifyWorkflowPropertiesChanged();
        }
        catch (OperationCanceledException) when (navigation.IsCancellationRequested)
        {
            // Being superseded is ordinary navigation, not a failure, so it reports nothing. Only a
            // cancellation the caller actually asked for is worth showing.
            if (cancellationToken.IsCancellationRequested &&
                generation == _navigationGeneration &&
                IsCurrentLoadTarget(expectedFile, expectedPath, expectedFileKind, expectedInstallationContextVersion))
            {
                SetStatus("O carregamento do arquivo foi cancelado.");
            }
        }
        catch (Exception)
        {
            if (generation == _navigationGeneration &&
                IsCurrentLoadTarget(expectedFile, expectedPath, expectedFileKind, expectedInstallationContextVersion))
            {
                ClearLoadedDocument();
                SetStatus("Não foi possível carregar o arquivo de configuração.");
            }
        }
        finally
        {
            // A superseded load must never clear the busy state of the load that replaced it.
            // IsBusy is cleared first on purpose: clearing IsLoadingFile first would make the list
            // count as busy-outside-navigation for one notification and flick it disabled.
            if (generation == _navigationGeneration)
            {
                IsBusy = false;
                IsLoadingFile = false;
            }

            if (ReferenceEquals(_navigationCancellation, navigation))
            {
                _navigationCancellation = null;
            }

            navigation.Dispose();
        }
    }

    private void ApplyInstallation(NutInstallationInfo installation)
    {
        _currentInstallation = installation;
        InvalidateAdministrativeAction();
        _installationContextVersion++;
        ClearDriverDiagnostics();
        ClearLoadedDocument(clearSelectedFile: true);
        InstallationStatusText = installation.IsDetected
            ? "Instalação NUT encontrada"
            : "Nenhuma instalação NUT local encontrada";
        InstallationDirectoryText = installation.InstallationDirectory ?? UnavailableText;
        ConfigurationDirectoryText = installation.ConfigurationDirectory ?? UnavailableText;
        InstallationVersionText = installation.Version ?? UnavailableText;

        var filesByName = installation.ConfigurationFiles
            .GroupBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var file in ConfigurationFiles)
        {
            filesByName.TryGetValue(file.FileName, out var info);
            file.ApplyInstallationInfo(info);
        }

        OnPropertyChanged(nameof(IsConfigurationFileListEmpty));
    }

    private async Task LoadWindowsAdministrationAsync(CancellationToken cancellationToken)
    {
        if (_windowsAdministration is null) return;
        var snapshot = await _windowsAdministration.InspectAsync(_currentInstallation ?? NutInstallationInfo.NotDetected(), cancellationToken);
        _windowsAdministrationSnapshot = snapshot;
        OnPropertyChanged(nameof(WindowsServiceDiscoveryText));
        OnPropertyChanged(nameof(IsWindowsServiceDiscoveryProblem));
        OnPropertyChanged(nameof(WindowsServiceDiagnosticText));
        OnPropertyChanged(nameof(HasWindowsServiceDiagnostic));
        WindowsServices = snapshot.Services;
        SelectedWindowsService = snapshot.Services.FirstOrDefault();
        WindowsPermissionAssessment = snapshot.Permissions;
        WindowsProcesses = snapshot.Processes;
        WindowsEvents = snapshot.Events;
        WindowsEventLogStatus = snapshot.EventLogStatus;
        WindowsEventLogDiagnosticMessage = snapshot.EventLogDiagnosticMessage;
        AdministrativeStatusMessage = snapshot.DiagnosticMessage;
        IsAdministrativeCritical = false;
        InvalidateDriverDiagnostic();
        NotifyAdministrativePropertiesChanged();
    }

    private void InvalidateAdministrativeAction()
    {
        PendingAdministrativeAction = null;
        IsAdministrativeActionConfirmed = false;
        NotifyAdministrativePropertiesChanged();
    }

    private bool CanPrepareHardwareDiagnostic(NutConfiguredDriver? driver)
    {
        if (driver is null || !driver.Executable.IsAvailable || !driver.Executable.IsTrusted)
        {
            DriverDiagnosticStatusMessage = "O executável do driver não está disponível ou não é confiável.";
            return false;
        }

        if (!WindowsServices.Any(service => service.IsAssociated && service.State == NutServiceState.Stopped) ||
            WindowsServices.Any(service => service.IsAssociated && service.State != NutServiceState.Stopped))
        {
            DriverDiagnosticStatusMessage = "O serviço NUT está em execução ou com estado desconhecido e pode estar usando o dispositivo. Pare-o explicitamente na seção Serviço antes de iniciar o diagnóstico do driver.";
            return false;
        }

        if (driver.RuntimeState == NutDriverRuntimeState.Running)
        {
            DriverDiagnosticStatusMessage = "Há um processo do driver configurado em execução. Nenhum processo existente será interrompido.";
            return false;
        }

        if (driver.NormalizedComPort is not null && !driver.IsConfiguredComPortPresent)
        {
            DriverDiagnosticStatusMessage = "A porta COM configurada não foi detectada pelo Windows.";
            return false;
        }

        return true;
    }

    private bool IsPendingDriverDiagnosticCurrent(NutDriverDiagnosticRequest? request = null)
    {
        var pending = request ?? PendingDriverDiagnostic;
        if (pending is null || _currentInstallation?.InstallationDirectory is null || _currentInstallation.ConfigurationDirectory is null)
        {
            return false;
        }

        return string.Equals(pending.InstallationDirectory, _currentInstallation.InstallationDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pending.ConfigurationDirectory, _currentInstallation.ConfigurationDirectory, StringComparison.OrdinalIgnoreCase) &&
            (pending.Driver is null || string.Equals(pending.Driver.UpsName, SelectedConfiguredDriver?.UpsName, StringComparison.Ordinal));
    }

    private void InvalidateDriverDiagnostic()
    {
        PendingDriverDiagnostic = null;
        IsDriverDiagnosticConfirmed = false;
        NotifyDriverDiagnosticPropertiesChanged();
    }

    private void ClearDriverDiagnostics()
    {
        ApplyComPorts(Array.Empty<NutComPortInfo>(), known: false);
        ApplyConfiguredDrivers(Array.Empty<NutConfiguredDriver>(), known: false);
        UpsdrvctlPath = null;
        _upsConfFingerprint = null;
        DriverDiagnosticResult = null;
        DriverDiagnosticStatusMessage = null;
        InvalidateDriverDiagnostic();
    }

    private bool IsPendingAdministrativeActionCurrent()
    {
        if (PendingAdministrativeAction is null || _currentInstallation?.InstallationDirectory is null || _currentInstallation.ConfigurationDirectory is null) return false;
        if (!string.Equals(PendingAdministrativeAction.InstallationDirectory, _currentInstallation.InstallationDirectory, StringComparison.OrdinalIgnoreCase) || !string.Equals(PendingAdministrativeAction.ConfigurationDirectory, _currentInstallation.ConfigurationDirectory, StringComparison.OrdinalIgnoreCase)) return false;
        return PendingAdministrativeAction.ServiceName is null || string.Equals(PendingAdministrativeAction.ServiceName, SelectedWindowsService?.ServiceName, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryApplyDetectedInstallation(
        NutInstallationInfo installation,
        int detectionDraftVersion,
        int detectionInstallationContextVersion)
    {
        if (_draftVersion != detectionDraftVersion ||
            _installationContextVersion != detectionInstallationContextVersion ||
            HasDraftChanges ||
            HasPreview)
        {
            SetStatus("A instalação não foi atualizada porque surgiram alterações locais durante a operação.");
            return false;
        }

        ApplyInstallation(installation);
        return true;
    }

    private static EditorBuildResult BuildEntries(NutConfigurationDocument document)
    {
        var groups = new List<NutConfigurationSectionViewModel>();
        NutConfigurationSectionViewModel? currentGroup = null;
        var entries = new List<NutConfigurationEntryViewModel>();
        var rawNodeCount = 0;

        for (var index = 0; index < document.Nodes.Count; index++)
        {
            var node = document.Nodes[index];
            if (node is NutSectionNode section)
            {
                currentGroup = new NutConfigurationSectionViewModel(section.Name);
                groups.Add(currentGroup);
                continue;
            }

            NutConfigurationEntryViewModel? entry = node switch
            {
                NutConfigurationAssignmentNode assignment => NutConfigurationEntryViewModel.ForAssignment(index, assignment),
                NutConfigurationDirectiveNode directive => NutConfigurationEntryViewModel.ForDirective(index, directive),
                _ => null
            };
            if (entry is null)
            {
                rawNodeCount++;
                continue;
            }

            currentGroup ??= CreateGeneralGroup(groups);
            currentGroup.Entries.Add(entry);
            entries.Add(entry);
        }

        foreach (var group in groups)
        {
            group.SetRawContentSummary(rawNodeCount);
        }

        return new EditorBuildResult(entries, groups);
    }

    private async Task<EditorBuildResult> BuildEditorsAsync(
        NutConfigurationFileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.FileKind == NutConfigurationFileKind.UpsConf)
        {
            var result = BuildEntries(snapshot.Document);
            IReadOnlyList<string>? installed = null;
            if (IsLocalManagementProfile && _driverCatalogSource is not null && _currentInstallation is not null)
                installed = await _driverCatalogSource.GetInstalledDriverNamesAsync(_currentInstallation, cancellationToken);
            // The port choices come from whatever source answered, provided it answered. A remote
            // agent's list is as usable a set of choices as a local one; a list nobody could read is
            // not offered at all.
            result.UpsEditor = new UpsConfigurationEditorViewModel(
                snapshot,
                installed,
                IsComPortListKnown ? ComPorts : [],
                Strings);
            return result;
        }
        if (snapshot.FileKind == NutConfigurationFileKind.NutConf)
        {
            return new EditorBuildResult([], [])
            {
                NutEditor = new NutGeneralConfigurationEditorViewModel(snapshot, Strings, CanEditConfiguration)
            };
        }
        if (snapshot.FileKind == NutConfigurationFileKind.UpsdConf)
        {
            return new EditorBuildResult([], [])
            {
                UpsdEditor = new UpsdConfigurationEditorViewModel(snapshot, Strings, CanEditConfiguration)
            };
        }
        if (snapshot.FileKind == NutConfigurationFileKind.UpsdUsers)
        {
            return new EditorBuildResult([], [])
            {
                UpsdUsersEditor = new UpsdUsersConfigurationEditorViewModel(snapshot, Strings, CanEditConfiguration)
            };
        }
        if (snapshot.FileKind == NutConfigurationFileKind.UpsmonConf)
        {
            return new EditorBuildResult([], [])
            {
                UpsmonEditor = new UpsmonConfigurationEditorViewModel(snapshot, Strings, CanEditConfiguration)
            };
        }
        return BuildEntries(snapshot.Document);
    }

    private void PublishEditors(
        NutConfigurationFileSnapshot snapshot,
        NutConfigurationFileItemViewModel selectedFile,
        EditorBuildResult result)
    {
        foreach (var entry in _entries)
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
        }

        DisposeSemanticEditors();
        _loadedSnapshot = snapshot;
        selectedFile.SetLoaded();
        _entries = result.Entries;
        Sections = result.Sections;
        foreach (var entry in _entries)
        {
            entry.PropertyChanged += OnEntryPropertyChanged;
        }

        if (result.UpsEditor is not null)
        {
            result.UpsEditor.Changed += OnUpsConfigurationChanged;
            UpsConfigurationEditor = result.UpsEditor;
            result.UpsEditor = null;
        }
        if (result.NutEditor is not null)
        {
            result.NutEditor.Changed += OnSemanticConfigurationChanged;
            NutGeneralConfigurationEditor = result.NutEditor;
            result.NutEditor = null;
        }
        if (result.UpsdEditor is not null)
        {
            result.UpsdEditor.Changed += OnSemanticConfigurationChanged;
            UpsdConfigurationEditor = result.UpsdEditor;
            result.UpsdEditor = null;
        }
        if (result.UpsdUsersEditor is not null)
        {
            result.UpsdUsersEditor.Changed += OnSemanticConfigurationChanged;
            UpsdUsersConfigurationEditor = result.UpsdUsersEditor;
            result.UpsdUsersEditor = null;
        }
        if (result.UpsmonEditor is not null)
        {
            result.UpsmonEditor.Changed += OnSemanticConfigurationChanged;
            UpsmonConfigurationEditor = result.UpsmonEditor;
            result.UpsmonEditor = null;
        }

        _draftVersion++;
        NotifyWorkflowPropertiesChanged();
    }

    private sealed class EditorBuildResult(
        IReadOnlyList<NutConfigurationEntryViewModel> entries,
        IReadOnlyList<NutConfigurationSectionViewModel> sections) : IDisposable
    {
        public IReadOnlyList<NutConfigurationEntryViewModel> Entries { get; } = entries;
        public IReadOnlyList<NutConfigurationSectionViewModel> Sections { get; } = sections;
        public UpsConfigurationEditorViewModel? UpsEditor { get; set; }
        public NutGeneralConfigurationEditorViewModel? NutEditor { get; set; }
        public UpsdConfigurationEditorViewModel? UpsdEditor { get; set; }
        public UpsdUsersConfigurationEditorViewModel? UpsdUsersEditor { get; set; }
        public UpsmonConfigurationEditorViewModel? UpsmonEditor { get; set; }

        public void Dispose()
        {
            UpsEditor?.Dispose();
            NutEditor?.Dispose();
            UpsdEditor?.Dispose();
            UpsdUsersEditor?.Dispose();
            UpsmonEditor?.Dispose();
        }
    }

    private static NutConfigurationSectionViewModel CreateGeneralGroup(ICollection<NutConfigurationSectionViewModel> groups)
    {
        var group = new NutConfigurationSectionViewModel("Geral");
        groups.Add(group);
        return group;
    }

    private void ClearLoadedDocument()
        => ClearLoadedDocument(clearSelectedFile: false);

    private void ClearLoadedDocument(bool clearSelectedFile)
    {
        foreach (var entry in _entries)
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
        }

        _loadedSnapshot = null;
        DisposeSemanticEditors();
        _entries = Array.Empty<NutConfigurationEntryViewModel>();
        Sections = Array.Empty<NutConfigurationSectionViewModel>();
        InvalidatePreview();
        if (clearSelectedFile)
        {
            SelectedFile = null;
        }

        OnPropertyChanged(nameof(SelectedFileEncodingText));
        OnPropertyChanged(nameof(HasLoadedFile));
        OnPropertyChanged(nameof(HasNoLoadedFile));
        OnPropertyChanged(nameof(IsEditorPlaceholderVisible));
        NotifyWorkflowPropertiesChanged();
    }

    private bool IsCurrentLoadTarget(
        NutConfigurationFileItemViewModel expectedFile,
        string expectedPath,
        NutConfigurationFileKind expectedFileKind,
        int expectedInstallationContextVersion) =>
        expectedInstallationContextVersion == _installationContextVersion &&
        ReferenceEquals(SelectedFile, expectedFile) &&
        expectedFile.FileKind == expectedFileKind &&
        string.Equals(expectedFile.FullPath, expectedPath, StringComparison.Ordinal);

    private void SetInstallationChangeBlockedStatus()
    {
        SetStatus(HasDraftChanges || HasPreview
            ? "Descarte ou aplique as alterações antes de trocar a instalação."
            : "Aguarde a operação atual antes de trocar a instalação.");
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(NutConfigurationEntryViewModel.DraftValue))
        {
            return;
        }

        _draftVersion++;
        InvalidateAdministrativeAction();
        InvalidateDriverDiagnostic();
        InvalidatePreview();
        NotifyWorkflowPropertiesChanged();
    }

    private void OnUpsConfigurationChanged()
        => OnSemanticConfigurationChanged();

    private void OnSemanticConfigurationChanged()
    {
        _draftVersion++;
        InvalidateAdministrativeAction();
        InvalidateDriverDiagnostic();
        InvalidatePreview();
        NotifyWorkflowPropertiesChanged();
    }

    private bool TryApplyDrafts(NutConfigurationDocument document)
    {
        foreach (var entry in _entries.Where(entry => entry.IsChanged))
        {
            if (entry.NodeIndex < 0 || entry.NodeIndex >= document.Nodes.Count || !entry.TryApply(document.Nodes[entry.NodeIndex]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesLoadedSnapshot(NutConfigurationFileSnapshot current, NutConfigurationFileSnapshot loaded) =>
        current.OriginalLength == loaded.OriginalLength &&
        string.Equals(current.OriginalFingerprint, loaded.OriginalFingerprint, StringComparison.Ordinal) &&
        current.FileKind == loaded.FileKind;

    private void InvalidatePreview()
    {
        _preparedChange = null;
        _preparedDraftVersion = -1;
        PreviewLines = Array.Empty<NutConfigurationPreviewLineViewModel>();
        IsPreviewConfirmed = false;
        SemanticReview = null;
        SemanticReviewChanged?.Invoke(null);
        NotifyWorkflowPropertiesChanged();
    }

    private void ApplyResultStatus(NutConfigurationApplyResult result)
    {
        switch (result.Status)
        {
            case NutConfigurationApplyStatus.Success:
                SetStatus("Configuração aplicada com sucesso.");
                break;
            case NutConfigurationApplyStatus.NoChanges:
                SetStatus("Não há alterações para aplicar.");
                break;
            case NutConfigurationApplyStatus.TargetNotFound:
                SetStatus("O arquivo não existe neste diretório.");
                break;
            case NutConfigurationApplyStatus.ChangedExternally:
                SetStatus("O arquivo foi alterado externamente desde que foi carregado.");
                break;
            case NutConfigurationApplyStatus.ChangedExternallyRollbackFailed:
                SetStatus("O arquivo foi alterado externamente e a recuperação exige atenção manual.", critical: true);
                break;
            case NutConfigurationApplyStatus.CandidateValidationFailed:
                SetStatus("A validação da configuração candidata falhou.");
                break;
            case NutConfigurationApplyStatus.TempWriteFailed:
                SetStatus("Não foi possível preparar o arquivo temporário.");
                break;
            case NutConfigurationApplyStatus.ReplaceFailed:
                SetStatus("Não foi possível substituir o arquivo de configuração.");
                break;
            case NutConfigurationApplyStatus.PostApplyValidationFailedRolledBack:
                SetStatus("A validação falhou e a configuração original foi restaurada.");
                break;
            case NutConfigurationApplyStatus.PostApplyValidationFailedRollbackFailed:
                SetStatus("A validação falhou e a configuração pode necessitar recuperação manual.", critical: true);
                break;
            case NutConfigurationApplyStatus.VerificationFailedRolledBack:
                SetStatus("A verificação falhou e a configuração original foi restaurada.");
                break;
            case NutConfigurationApplyStatus.VerificationFailedRollbackFailed:
                SetStatus("A verificação falhou e a configuração pode necessitar recuperação manual.", critical: true);
                break;
            case NutConfigurationApplyStatus.RemoteCommitOutcomeUnknown:
                SetStatus("CRÍTICO — a operação remota pode ter sido executada. Atualize e verifique o arquivo antes de tentar novamente.", critical: true);
                break;
            case NutConfigurationApplyStatus.RemoteTemporaryCleanupFailed:
                SetStatus("CRÍTICO — um arquivo temporário remoto contendo configuração pode necessitar remoção manual.", critical: true);
                break;
            case NutConfigurationApplyStatus.Cancelled:
                SetStatus("A aplicação das alterações foi cancelada.");
                break;
            default:
                SetStatus("Não foi possível aplicar a configuração.", critical: true);
                break;
        }
    }

    private void SetLoadFailureStatus(NutConfigurationLoadStatus status) =>
        SetStatus(status switch
        {
            NutConfigurationLoadStatus.TargetNotFound => "O arquivo não existe neste diretório.",
            NutConfigurationLoadStatus.AccessDenied => "Permissão insuficiente. A elevação administrativa será tratada pela etapa de administração do Windows.",
            NutConfigurationLoadStatus.UnsupportedEncoding => "A codificação do arquivo não é suportada.",
            NutConfigurationLoadStatus.Cancelled => "O carregamento do arquivo foi cancelado.",
            _ => "Não foi possível carregar o arquivo de configuração."
        });

    private void SetStatus(string? message, bool critical = false)
    {
        StatusMessage = message;
        IsCriticalResult = critical;
    }

    private void NotifyWorkflowPropertiesChanged()
    {
        RefreshConfigurationFileTiles();
        OnPropertyChanged(nameof(HasDraftChanges));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(IsEditorPlaceholderVisible));
        OnPropertyChanged(nameof(CanEditEntries));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanDiscard));
        OnPropertyChanged(nameof(CanReload));
        OnPropertyChanged(nameof(CanChangeInstallation));
        OnPropertyChanged(nameof(CanDetectInstallation));
        OnPropertyChanged(nameof(CanSelectConfigurationFile));
        OnPropertyChanged(nameof(IsRemoteConfigurationReady));
        OnPropertyChanged(nameof(IsConfigurationEditorVisible));
        OnPropertyChanged(nameof(IsUpsConfigurationEditorVisible));
        OnPropertyChanged(nameof(IsNutGeneralConfigurationEditorVisible));
        OnPropertyChanged(nameof(IsUpsdConfigurationEditorVisible));
        OnPropertyChanged(nameof(IsUpsdUsersConfigurationEditorVisible));
        OnPropertyChanged(nameof(IsUpsmonConfigurationEditorVisible));
        OnPropertyChanged(nameof(IsLegacyConfigurationEditorVisible));
        OnPropertyChanged(nameof(CanChangeRemoteSessionContext));
        OnPropertyChanged(nameof(CanConnectRemote));
        OnPropertyChanged(nameof(CanDisconnectRemote));
        OnPropertyChanged(nameof(CanTrustRemoteHostKey));
        OnPropertyChanged(nameof(CanBrowseRemoteDirectory));
        OnPropertyChanged(nameof(CanValidateRemoteDirectory));
        OnPropertyChanged(nameof(CanUseRemoteDirectory));
        OnPropertyChanged(nameof(CanProbeRemoteWriteCapability));
        OnPropertyChanged(nameof(RequiresRemoteWriteAuthorization));
        NotifyAdministrativePropertiesChanged();
        NotifyDriverDiagnosticPropertiesChanged();
    }

    private void NotifyAdministrativePropertiesChanged()
    {
        OnPropertyChanged(nameof(IsWindowsAdministrationAvailable));
        OnPropertyChanged(nameof(HasPendingAdministrativeAction));
        OnPropertyChanged(nameof(PendingAdministrativeActionText));
        OnPropertyChanged(nameof(CanPrepareAdministrativeAction));
        OnPropertyChanged(nameof(CanExecuteAdministrativeAction));
        OnPropertyChanged(nameof(CanStartWindowsService));
        OnPropertyChanged(nameof(CanStopWindowsService));
        OnPropertyChanged(nameof(CanRestartWindowsService));
        OnPropertyChanged(nameof(IsPermissionRepairPending));
        OnPropertyChanged(nameof(PendingPermissionIdentity));
        OnPropertyChanged(nameof(PendingPermissionSid));
        OnPropertyChanged(nameof(PendingPermissionDirectory));
        OnPropertyChanged(nameof(PendingPermissionTargets));
        NotifyDriverDiagnosticPropertiesChanged();
    }

    private void NotifyDriverDiagnosticPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsDriverDiagnosticsAvailable));
        OnPropertyChanged(nameof(HasPendingDriverDiagnostic));
        OnPropertyChanged(nameof(HasDriverDiagnosticResult));
        OnPropertyChanged(nameof(PendingDriverDiagnosticText));
        OnPropertyChanged(nameof(PendingDriverDiagnosticContactsHardware));
        OnPropertyChanged(nameof(PendingDriverDiagnosticTool));
        OnPropertyChanged(nameof(PendingDriverDiagnosticUpsName));
        OnPropertyChanged(nameof(PendingDriverDiagnosticPort));
        OnPropertyChanged(nameof(PendingDriverDiagnosticHardwareText));
        OnPropertyChanged(nameof(NutServiceStateForDriverDiagnostic));
        OnPropertyChanged(nameof(CanRefreshDriverDiagnostics));
        OnPropertyChanged(nameof(CanRefreshRemoteDeviceInspection));
        OnPropertyChanged(nameof(CanRefreshDeviceInspection));
        OnPropertyChanged(nameof(AreActiveDiagnosticsAvailable));
        OnPropertyChanged(nameof(CanSelectConfiguredDriver));
        OnPropertyChanged(nameof(HasSelectedDriverPortState));
        OnPropertyChanged(nameof(SelectedDriverPortStateText));
        OnPropertyChanged(nameof(CanPrepareDriverDiagnostic));
        OnPropertyChanged(nameof(CanExecuteDriverDiagnostic));
        OnPropertyChanged(nameof(IsDriverDiagnosticCritical));
    }

    private static string ToDriverDiagnosticText(NutDriverDiagnosticKind kind) => kind switch
    {
        NutDriverDiagnosticKind.UpsdrvctlHelp => "Ajuda do upsdrvctl",
        NutDriverDiagnosticKind.UpsdrvctlList => "Listar drivers NUT",
        NutDriverDiagnosticKind.UpsdrvctlStatus => "Consultar status dos drivers",
        NutDriverDiagnosticKind.UpsdrvctlDryRunStart => "Validar configuração do driver (simulação)",
        NutDriverDiagnosticKind.DriverHelp => "Ajuda do driver",
        NutDriverDiagnosticKind.DriverVersion => "Versão do driver",
        NutDriverDiagnosticKind.DriverVariableList => "Listar variáveis do driver",
        NutDriverDiagnosticKind.DriverDataDump => "Coletar diagnóstico do dispositivo",
        _ => "Diagnóstico do NUT"
    };

    private static IReadOnlyList<NutConfigurationFileItemViewModel> CreateFileItems(ManagedNutConfigurationFiles? managedFiles)
    {
        var enabled = managedFiles ?? ManagedNutConfigurationFiles.All;
        return AllFileItems(enabled);
    }

    private static IReadOnlyList<NutConfigurationFileItemViewModel> AllFileItems(ManagedNutConfigurationFiles enabled) =>
    [
        new("Geral", "nut.conf", "nut.conf", NutConfigurationFileKind.NutConf, enabled.Contains(NutConfigurationFileKind.NutConf)),
        new("UPS e drivers", "ups.conf", "ups.conf", NutConfigurationFileKind.UpsConf, enabled.Contains(NutConfigurationFileKind.UpsConf)),
        new("Servidor", "upsd.conf", "upsd.conf", NutConfigurationFileKind.UpsdConf, enabled.Contains(NutConfigurationFileKind.UpsdConf)),
        new("Usuários", "upsd.users", "upsd.users", NutConfigurationFileKind.UpsdUsers, enabled.Contains(NutConfigurationFileKind.UpsdUsers)),
        new("Monitoramento", "upsmon.conf", "upsmon.conf", NutConfigurationFileKind.UpsmonConf, enabled.Contains(NutConfigurationFileKind.UpsmonConf))
    ];

    private static string ToEncodingText(NutConfigurationTextEncoding encoding) => encoding switch
    {
        NutConfigurationTextEncoding.Utf8 => "UTF-8",
        NutConfigurationTextEncoding.Utf8Bom => "UTF-8 com BOM",
        NutConfigurationTextEncoding.Utf16LittleEndian => "UTF-16 LE",
        NutConfigurationTextEncoding.Utf16BigEndian => "UTF-16 BE",
        _ => UnavailableText
    };

    partial void OnSelectedFileChanged(NutConfigurationFileItemViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedFileName));
        OnPropertyChanged(nameof(SelectedFileStatusText));
        OnPropertyChanged(nameof(CanReload));
    }

    partial void OnIsBusyChanged(bool value) => NotifyWorkflowPropertiesChanged();

    partial void OnIsLoadingFileChanged(bool value) => NotifyWorkflowPropertiesChanged();

    partial void OnIsDetectingInstallationChanged(bool value) => NotifyWorkflowPropertiesChanged();

    partial void OnIsPreviewConfirmedChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    partial void OnIsAdministrativeActionConfirmedChanged(bool value) => OnPropertyChanged(nameof(CanExecuteAdministrativeAction));

    partial void OnSelectedWindowsServiceChanged(NutServiceInfo? value)
    {
        InvalidateAdministrativeAction();
        OnPropertyChanged(nameof(HasSelectedWindowsService));
        OnPropertyChanged(nameof(SelectedWindowsServiceStateText));
        OnPropertyChanged(nameof(SelectedWindowsServiceStartModeText));
        OnPropertyChanged(nameof(IsWindowsServiceRunning));
        OnPropertyChanged(nameof(IsWindowsServiceStopped));
        OnPropertyChanged(nameof(IsWindowsServiceFailed));
        OnPropertyChanged(nameof(IsWindowsServiceTransitioning));
        OnPropertyChanged(nameof(IsWindowsServiceUnknown));
        OnPropertyChanged(nameof(IsSelectedWindowsServiceControllable));
        OnPropertyChanged(nameof(IsSelectedWindowsServiceUnverified));
    }

    partial void OnWindowsServicesChanged(IReadOnlyList<NutServiceInfo> value)
    {
        OnPropertyChanged(nameof(HasWindowsServices));
        OnPropertyChanged(nameof(HasNoWindowsServices));
    }

    partial void OnWindowsProcessesChanged(IReadOnlyList<NutProcessInfo> value) => OnPropertyChanged(nameof(HasNoWindowsProcesses));

    partial void OnWindowsEventsChanged(IReadOnlyList<NutEventLogEntry> value)
    {
        OnPropertyChanged(nameof(HasNoWindowsEvents));
        OnPropertyChanged(nameof(WindowsEventRows));
    }

    partial void OnSelectedConfiguredDriverChanged(NutConfiguredDriver? value)
    {
        InvalidateDriverDiagnostic();
        OnPropertyChanged(nameof(HasSelectedConfiguredDriver));
        OnPropertyChanged(nameof(SelectedConfiguredDriverStateText));
        OnPropertyChanged(nameof(IsSelectedDriverPortPresent));
        OnPropertyChanged(nameof(HasSelectedDriverPortState));
        OnPropertyChanged(nameof(SelectedDriverPortStateText));
    }

    partial void OnIsConfiguredDriverListKnownChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoConfiguredDrivers));
        OnPropertyChanged(nameof(IsConfiguredDriverListUnknown));
    }

    partial void OnConfiguredDriversChanged(IReadOnlyList<NutConfiguredDriver> value)
    {
        OnPropertyChanged(nameof(HasConfiguredDrivers));
        OnPropertyChanged(nameof(HasNoConfiguredDrivers));
    }

    partial void OnComPortsChanged(IReadOnlyList<NutComPortInfo> value)
    {
        // The presentation list is rebuilt here rather than by every caller, so a port can never be
        // on screen with an identity line belonging to a previous reading.
        DetectedComPorts = value.Select(port => DetectedComPortPresentation.Create(port, Strings)).ToArray();
        OnPropertyChanged(nameof(HasNoComPorts));
    }

    partial void OnDetectedComPortsChanged(IReadOnlyList<DetectedComPortViewModel> value) =>
        OnPropertyChanged(nameof(HasNoComPorts));

    partial void OnIsComPortListKnownChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoComPorts));
        OnPropertyChanged(nameof(HasSelectedDriverPortState));
    }

    partial void OnDeviceInspectionSourceChanged(NutDeviceInspectionSource value)
    {
        OnPropertyChanged(nameof(IsDeviceInspectionAvailable));
        OnPropertyChanged(nameof(IsDeviceInspectionUnavailable));
        OnPropertyChanged(nameof(IsRemoteDeviceInspection));
        OnPropertyChanged(nameof(DeviceInspectionSourceText));
        OnPropertyChanged(nameof(AreActiveDiagnosticsAvailable));
        OnPropertyChanged(nameof(CanSelectConfiguredDriver));
        OnPropertyChanged(nameof(SelectedDriverPortStateText));
    }

    /// <summary>
    /// Discards a confirmation nobody answered.
    ///
    /// Stop and Restart ask before they act, and the question is presentation state: it belongs to the
    /// moment the operator was looking at that panel. Leaving the screen is an answer of a kind, and
    /// the safe reading of it is "not now" — so the question goes away rather than lying in wait to be
    /// re-asked, out of context, whenever they come back.
    ///
    /// This cannot cancel anything already sent. Both paths clear their pending state before reaching
    /// the agent or the service, so by the time an operation is in flight there is no confirmation
    /// left here to discard, and calling this affects nothing but the prompt.
    /// </summary>
    private void DiscardPendingConfirmations()
    {
        RemoteWindowsServiceControl?.CancelConfirmation();
        InvalidateAdministrativeAction();
    }

    public override void OnDeactivated() => DiscardPendingConfirmations();

    /// <summary>
    /// Takes the same rebuilt client the service monitor was just given, so the one read-only hardware
    /// operation this page performs travels the transport the profile now selects.
    ///
    /// Leaving it pointed at the previous client would make the devices screen and the service screen
    /// describe the same server over two different connections, and only one of them would be the one
    /// the operator chose. There is still exactly one client per profile and no fallback between
    /// transports.
    /// </summary>
    /// <summary>
    /// Applies a saved change of access mode without a restart.
    ///
    /// Only the access mode. Replacing the whole persisted profile here would make this page describe a
    /// transport the running session is not using — the configuration session was established at
    /// startup and still speaks whatever it was built for, so showing the newly saved one would be a
    /// confident lie.
    ///
    /// Capabilities are recomputed from the profile rather than edited, so the same derivation decides
    /// them here as at startup and there is no second place where an access mode turns into a set of
    /// permissions.
    ///
    /// Widening to Manage grants nothing on its own. Writing still requires the safe-write probe, and
    /// <see cref="RequiresRemoteWriteAuthorization"/> reports the capability as unverified until that
    /// probe has run — which is the T19 boundary, and the reason this is safe to apply live.
    /// </summary>
    public void ApplyAccessMode(ManagedNutServerAccessMode accessMode)
    {
        if (_profileContext is not { } context || context.Profile.AccessMode == accessMode) return;

        // AccessMode is get-only rather than an init-settable positional member, so the profile is
        // rebuilt the same way the update service rebuilds it. Everything else is carried across
        // unchanged, which is what keeps this to the one field.
        var profile = new ManagedNutServerProfile(
            context.Profile.Id,
            context.Profile.Name,
            context.Profile.Monitoring,
            context.Profile.Management,
            accessMode);
        _profileContext = context with
        {
            Profile = profile,
            Capabilities = ManagedServerCapabilities.FromProfile(profile)
        };

        // The section list is built from what the profile may do, so it is rebuilt rather than edited.
        // The selection is carried across by which section it is, not by object identity, because every
        // item in the list is new.
        var previous = SelectedAdministrationSection.Section;
        AdministrationSections = AdministrationPresentation.CreateSections(
            Strings,
            IsRemoteManagementProfile,
            accessMode != ManagedNutServerAccessMode.ReadOnly);
        OnPropertyChanged(nameof(AdministrationSections));

        SelectedAdministrationSection =
            AdministrationSections.FirstOrDefault(section => section.Section == previous)
            ?? AdministrationSections[0];

        OnPropertyChanged(nameof(AccessModeDisplayText));
        OnPropertyChanged(nameof(RemoteWriteAuthorizationUnavailableTooltip));
        OnPropertyChanged(nameof(RequiresRemoteWriteAuthorization));
        NotifyWorkflowPropertiesChanged();
    }

    public void RebindAgentClient(INutManagerAgentClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (ReferenceEquals(_agentClient, client)) return;

        _agentClient = client;

        // What the previous transport reported is no longer an answer about the current one.
        SetRemoteInspectionUnavailable(Strings.Get("Administration.Drivers.RemoteAgentUnavailable"));
    }

    partial void OnSelectedAdministrationSectionChanged(AdministrationSectionItemViewModel value)
    {
        // Moving between sections leaves the panel that asked, so the question goes with it.
        DiscardPendingConfirmations();

        OnPropertyChanged(nameof(IsNutConfigurationSectionSelected));
        OnPropertyChanged(nameof(IsWindowsServiceSectionSelected));
        OnPropertyChanged(nameof(IsDevicesDriversSectionSelected));
        OnPropertyChanged(nameof(IsRemoteAccessSectionSelected));
        OnPropertyChanged(nameof(AdministrationAvailabilityText));
    }

    partial void OnIsDriverDiagnosticConfirmedChanged(bool value) => OnPropertyChanged(nameof(CanExecuteDriverDiagnostic));

    partial void OnDriverDiagnosticResultChanged(NutDriverDiagnosticResult? value) => NotifyDriverDiagnosticPropertiesChanged();

    partial void OnBackupPathChanged(string? value) => OnPropertyChanged(nameof(HasBackupPath));

    partial void OnRecoveryPathChanged(string? value) => OnPropertyChanged(nameof(HasRecoveryPath));

    partial void OnTemporaryPathChanged(string? value) => OnPropertyChanged(nameof(HasTemporaryPath));

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnUpsConfigurationEditorChanged(UpsConfigurationEditorViewModel? value)
    {
        OnPropertyChanged(nameof(IsUpsConfigurationEditorVisible));
        OnPropertyChanged(nameof(IsLegacyConfigurationEditorVisible));
        NotifyWorkflowPropertiesChanged();
    }

    partial void OnNutGeneralConfigurationEditorChanged(NutGeneralConfigurationEditorViewModel? value)
    {
        OnPropertyChanged(nameof(IsNutGeneralConfigurationEditorVisible));
        OnPropertyChanged(nameof(IsLegacyConfigurationEditorVisible));
        NotifyWorkflowPropertiesChanged();
    }

    partial void OnUpsdConfigurationEditorChanged(UpsdConfigurationEditorViewModel? value)
    {
        OnPropertyChanged(nameof(IsUpsdConfigurationEditorVisible));
        OnPropertyChanged(nameof(IsUpsdUsersConfigurationEditorVisible));
        OnPropertyChanged(nameof(IsUpsmonConfigurationEditorVisible));
        OnPropertyChanged(nameof(IsLegacyConfigurationEditorVisible));
        NotifyWorkflowPropertiesChanged();
    }

    private void DisposeSemanticEditors()
    {
        if (UpsConfigurationEditor is not null)
        {
            UpsConfigurationEditor.Changed -= OnUpsConfigurationChanged;
            UpsConfigurationEditor.Dispose();
            UpsConfigurationEditor = null;
        }
        if (NutGeneralConfigurationEditor is not null)
        {
            NutGeneralConfigurationEditor.Changed -= OnSemanticConfigurationChanged;
            NutGeneralConfigurationEditor.Dispose();
            NutGeneralConfigurationEditor = null;
        }
        if (UpsdConfigurationEditor is not null)
        {
            UpsdConfigurationEditor.Changed -= OnSemanticConfigurationChanged;
            UpsdConfigurationEditor.Dispose();
            UpsdConfigurationEditor = null;
        }
        if (UpsdUsersConfigurationEditor is not null)
        {
            UpsdUsersConfigurationEditor.Changed -= OnSemanticConfigurationChanged;
            UpsdUsersConfigurationEditor.Dispose();
            UpsdUsersConfigurationEditor = null;
        }
        if (UpsmonConfigurationEditor is not null)
        {
            UpsmonConfigurationEditor.Changed -= OnSemanticConfigurationChanged;
            UpsmonConfigurationEditor.Dispose();
            UpsmonConfigurationEditor = null;
        }
    }

    private async void OnRemoteConfigurationContextChanged(
        INutConfigurationFilePipeline? pipeline,
        RemoteNutDirectoryValidationResult? validation,
        bool canWrite)
    {
        if (!IsRemoteManagementProfile)
        {
            return;
        }

        if (HasDraftChanges || HasPreview || IsBusy)
        {
            SetStatus("A sessão remota foi alterada, mas o editor atual foi preservado. Aplique ou descarte as alterações antes de atualizar o diretório remoto.");
            return;
        }

        var snapshot = _loadedSnapshot;
        var selectedFile = SelectedFile;
        var preservesLoadedFile = pipeline is not null &&
            validation?.IsValid == true &&
            snapshot is not null &&
            selectedFile?.FullPath is not null &&
            string.Equals(
                snapshot.TargetPath,
                GetRemoteFilePath(validation.Directory, selectedFile.FileName),
                StringComparison.OrdinalIgnoreCase);

        _configurationPipeline = pipeline;
        foreach (var file in ConfigurationFiles)
        {
            var present = validation?.PresentFileNames.Contains(file.FileName, StringComparer.OrdinalIgnoreCase) == true;
            file.ApplyRemoteInfo(
                validation?.IsValid == true ? GetRemoteFilePath(validation.Directory, file.FileName) : null,
                present);
        }

        OnPropertyChanged(nameof(IsConfigurationFileListEmpty));

        // The devices screen is filled before any configuration session exists, because the agent
        // answers without one. The driver list read at that moment therefore found no transport and
        // came back empty — not because ups.conf has no sections, but because nobody could open it.
        // The session arriving is the first moment that question can be answered, so it is answered
        // here rather than left waiting for the operator to press Refresh.
        //
        // The ports come from the inspection that already ran, so this costs no second agent call,
        // and passing null when the list is not an answer keeps a configured port reading as unknown
        // rather than as absent.
        await LoadRemoteConfiguredDriversAsync(
            IsComPortListKnown ? ComPorts : null, CancellationToken.None);

        if (preservesLoadedFile)
        {
            try
            {
                // A successful write authorization changes capability, not file contents. Rebuild
                // the in-memory editor against the snapshot already on screen so it becomes writable
                // immediately without a second remote read, a scroll reset, or a full application
                // restart. The candidate still carries the original T14 fingerprint.
                using var editors = await BuildEditorsAsync(snapshot!, CancellationToken.None);
                if (ReferenceEquals(_loadedSnapshot, snapshot) && ReferenceEquals(SelectedFile, selectedFile))
                {
                    PublishEditors(snapshot!, selectedFile!, editors);
                    OnPropertyChanged(nameof(SelectedFileEncodingText));
                    OnPropertyChanged(nameof(HasLoadedFile));
                    OnPropertyChanged(nameof(HasNoLoadedFile));
                }
            }
            catch (Exception)
            {
                SetStatus(Strings.Get("Administration.Configuration.CapabilityRefreshFailed"));
            }

            NotifyWorkflowPropertiesChanged();
            return;
        }

        _installationContextVersion++;
        ClearLoadedDocument(clearSelectedFile: true);

        NotifyWorkflowPropertiesChanged();
    }

    private string GetRemoteFilePath(string directory, string fileName) =>
        _remoteManagement?.CombineConfigurationFilePath(directory, fileName)
        ?? throw new InvalidOperationException("A remote configuration session is required to compose a configuration path.");

    private void OnRemoteManagementPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(RemoteManagementSessionViewModel.StatusMessage))
        {
            OnPropertyChanged(nameof(ManagementAvailabilityText));
        }

        if (eventArgs.PropertyName is nameof(RemoteManagementSessionViewModel.IsBusy) or
            nameof(RemoteManagementSessionViewModel.CanReadConfiguration) or
            nameof(RemoteManagementSessionViewModel.CanEditConfiguration) or
            nameof(RemoteManagementSessionViewModel.CanConnect) or
            nameof(RemoteManagementSessionViewModel.CanDisconnect) or
            nameof(RemoteManagementSessionViewModel.CanTrustHostKey) or
            nameof(RemoteManagementSessionViewModel.CanBrowse) or
            nameof(RemoteManagementSessionViewModel.CanChooseDirectory) or
            nameof(RemoteManagementSessionViewModel.CanValidateDirectory) or
            nameof(RemoteManagementSessionViewModel.CanUseCurrentDirectory) or
            nameof(RemoteManagementSessionViewModel.CanProbeWriteCapability))
        {
            NotifyWorkflowPropertiesChanged();
        }
    }
}

public enum NutConfigurationFileState
{
    NotLoaded,
    Available,
    Missing,
    AccessDenied,
    Loaded,
    Error
}

public sealed partial class NutConfigurationFileItemViewModel : ObservableObject
{
    public NutConfigurationFileItemViewModel(
        string category,
        string title,
        string fileName,
        NutConfigurationFileKind fileKind,
        bool isManaged = true)
    {
        Category = category;
        Title = title;
        FileName = fileName;
        FileKind = fileKind;
        _isManaged = isManaged;
        State = NutConfigurationFileState.Missing;
    }

    public string Category { get; }

    public string Title { get; }

    public string FileName { get; }

    public NutConfigurationFileKind FileKind { get; }

    /// <summary>Whether this profile authorizes NutManager to manage this file.</summary>
    [ObservableProperty]
    private bool _isManaged;

    // The rail stacks one icon per file and shows the matching one, which is the pattern the
    // Administration section list already uses. It avoids a value converter for what is a fixed,
    // five-way choice known at compile time.
    public bool IsNutConf => FileKind == NutConfigurationFileKind.NutConf;
    public bool IsUpsConf => FileKind == NutConfigurationFileKind.UpsConf;
    public bool IsUpsdConf => FileKind == NutConfigurationFileKind.UpsdConf;
    public bool IsUpsdUsers => FileKind == NutConfigurationFileKind.UpsdUsers;
    public bool IsUpsmonConf => FileKind == NutConfigurationFileKind.UpsmonConf;

    /// <summary>
    /// What a screen reader announces, and what the tooltip says when the rail is collapsed and
    /// only the icon is visible. It carries the invariant file name because that is the thing an
    /// administrator is actually looking for.
    /// </summary>
    public string AccessibleName => $"{Category} — {FileName}";

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Set by the page when a draft is pending, so the rail can mark the row.</summary>
    [ObservableProperty]
    private bool _hasPendingChanges;

    [ObservableProperty]
    private string? _fullPath;

    [ObservableProperty]
    private NutConfigurationFileState _state;

    public string StatusText => State switch
    {
        NutConfigurationFileState.Available => "Disponível",
        NutConfigurationFileState.Loaded => "Carregado",
        NutConfigurationFileState.AccessDenied => "Sem acesso",
        NutConfigurationFileState.Missing => "Ausente",
        NutConfigurationFileState.Error => "Erro",
        _ => "Não carregado"
    };

    public bool CanLoad => IsManaged && State is (NutConfigurationFileState.Available or NutConfigurationFileState.Loaded);

    partial void OnIsManagedChanged(bool value) => OnPropertyChanged(nameof(CanLoad));

    internal void ApplyInstallationInfo(NutConfigurationFileInfo? info)
    {
        FullPath = info?.FullPath;
        State = info switch
        {
            { Exists: true, IsReadable: true } => NutConfigurationFileState.Available,
            { Exists: true } => NutConfigurationFileState.AccessDenied,
            _ => NutConfigurationFileState.Missing
        };
    }

    internal void SetLoaded() => State = NutConfigurationFileState.Loaded;

    internal void ApplyRemoteInfo(string? fullPath, bool exists)
    {
        FullPath = fullPath;
        State = exists ? NutConfigurationFileState.Available : NutConfigurationFileState.Missing;
    }

    partial void OnStateChanged(NutConfigurationFileState value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanLoad));
    }
}

public sealed class NutConfigurationSectionViewModel
{
    public NutConfigurationSectionViewModel(string name)
    {
        Name = name;
        Entries = new ObservableCollection<NutConfigurationEntryViewModel>();
    }

    public string Name { get; }

    public ObservableCollection<NutConfigurationEntryViewModel> Entries { get; }

    public string RawContentSummary { get; private set; } = "Comentários e conteúdo avançado serão preservados.";

    internal void SetRawContentSummary(int rawNodeCount) => RawContentSummary = rawNodeCount == 0
        ? "Comentários e conteúdo avançado serão preservados."
        : $"{rawNodeCount} linhas de comentários/conteúdo avançado serão preservadas.";
}

public sealed partial class NutConfigurationEntryViewModel : ObservableObject
{
    private readonly string? _originalValue;
    private readonly string? _sectionName;
    private readonly bool _isAssignment;

    private NutConfigurationEntryViewModel(
        int nodeIndex,
        string name,
        string? sectionName,
        string originalValue,
        bool isAssignment,
        bool isSensitive)
    {
        NodeIndex = nodeIndex;
        Name = name;
        _sectionName = sectionName;
        _isAssignment = isAssignment;
        IsSensitive = isSensitive;
        _originalValue = isSensitive ? null : originalValue;
        DraftValue = isSensitive ? string.Empty : originalValue;
    }

    public int NodeIndex { get; }

    public int LineNumber => NodeIndex + 1;

    public string Name { get; }

    public string SectionName => _sectionName ?? "Geral";

    public string EntryTypeText => _isAssignment ? "Atribuição" : "Diretiva";

    public bool IsSensitive { get; }

    public bool IsNotSensitive => !IsSensitive;

    public string InputLabel => IsSensitive
        ? _isAssignment ? "Nova senha" : "Novos argumentos completos"
        : "Valor";

    public string SensitiveHint => IsSensitive
        ? _isAssignment
            ? "Valor sensível configurado. Deixe vazio para não alterar."
            : "Configuração sensível existente. A substituição abrange os argumentos completos da diretiva."
        : string.Empty;

    [ObservableProperty]
    private string _draftValue;

    public bool IsChanged => IsSensitive
        ? !string.IsNullOrEmpty(DraftValue)
        : !string.Equals(_originalValue, DraftValue, StringComparison.Ordinal);

    public static NutConfigurationEntryViewModel ForAssignment(int nodeIndex, NutConfigurationAssignmentNode assignment) =>
        new(nodeIndex, assignment.Name, assignment.SectionName, assignment.Value, isAssignment: true, assignment.IsSensitive);

    public static NutConfigurationEntryViewModel ForDirective(int nodeIndex, NutConfigurationDirectiveNode directive) =>
        new(nodeIndex, directive.Name, directive.SectionName, directive.Arguments, isAssignment: false, directive.IsSensitive);

    internal void ResetDraft() => DraftValue = IsSensitive ? string.Empty : _originalValue ?? string.Empty;

    internal bool TryApply(NutConfigurationNode node)
    {
        if (_isAssignment && node is NutConfigurationAssignmentNode assignment &&
            string.Equals(assignment.Name, Name, StringComparison.Ordinal) &&
            string.Equals(assignment.SectionName, _sectionName, StringComparison.Ordinal) &&
            assignment.IsSensitive == IsSensitive)
        {
            assignment.SetValue(DraftValue);
            return true;
        }

        if (!_isAssignment && node is NutConfigurationDirectiveNode directive &&
            string.Equals(directive.Name, Name, StringComparison.Ordinal) &&
            string.Equals(directive.SectionName, _sectionName, StringComparison.Ordinal) &&
            directive.IsSensitive == IsSensitive)
        {
            directive.SetArguments(DraftValue);
            return true;
        }

        return false;
    }

    partial void OnDraftValueChanged(string value)
    {
        OnPropertyChanged(nameof(IsChanged));
    }
}

public sealed class NutConfigurationPreviewLineViewModel
{
    public NutConfigurationPreviewLineViewModel(int lineNumber, string originalText, string candidateText, bool isRedacted)
    {
        LineNumber = lineNumber;
        OriginalText = originalText;
        CandidateText = candidateText;
        IsRedacted = isRedacted;
    }

    public int LineNumber { get; }

    public string OriginalText { get; }

    public string CandidateText { get; }

    public bool IsRedacted { get; }
}
