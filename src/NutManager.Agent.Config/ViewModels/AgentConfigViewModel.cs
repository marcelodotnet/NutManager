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
    private readonly IAgentCertificateImporter? _certificateImporter;
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

    public AgentConfigViewModel(
        IAgentConfigurationStore store,
        IAgentOperatorsGroupAdministration groups,
        IAgentServiceAdministration service,
        IAgentHttpsResourceAdministration resources,
        IAgentCertificateCatalog certificates,
        IAgentRuntimeInventory inventory,
        UiLanguagePreference? language = null,
        TimeProvider? timeProvider = null,
        IAgentCertificateImporter? certificateImporter = null)
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
        _time = timeProvider ?? TimeProvider.System;

        Strings = new AgentConfigStrings(language ?? AgentConfigStrings.DetectLanguage());
    }

    public AgentConfigStrings Strings { get; }

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

    // ---------------------------------------------------------------- which half is showing

    /// <summary>
    /// Whether the diagnostics list has replaced the configuration surface.
    ///
    /// One window, two views, and no navigation rail: there are exactly two things to look at, and a
    /// sidebar for two destinations is the desktop shell this utility is deliberately not.
    /// </summary>
    [ObservableProperty]
    private bool _showDiagnostics;

    partial void OnShowDiagnosticsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowConfiguration));
        OnPropertyChanged(nameof(ViewToggleText));
    }

    public bool ShowConfiguration => !ShowDiagnostics;

    /// <summary>
    /// The toggle's label names where it goes, not where you are. Localized text belongs on the view
    /// model rather than inside a converter, which would need a language of its own to reach.
    /// </summary>
    public string ViewToggleText => ShowDiagnostics ? Strings["Header.Configuration"] : Strings["Header.Diagnostics"];

    [RelayCommand]
    private void ToggleDiagnostics() => ShowDiagnostics = !ShowDiagnostics;

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
    public bool ShowCertificateFeedback =>
        HttpsEnabled &&
        !string.IsNullOrWhiteSpace(CertificateFeedbackMessage) &&
        (CertificateImportMessage is not null || (!HttpsHostHasError && !HttpsPortHasError));

    public string? CertificateFeedbackMessage => CertificateImportMessage ?? HttpsValidationMessage;

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
        RefreshHttpsValidation();
        RefreshDirty();
    }

    partial void OnSelectedCertificateChanged(AgentCertificateOption? value)
    {
        ClearImportFeedback();
        OnPropertyChanged(nameof(HasSelectedCertificate));
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
            var unavailable = AgentCertificateImportResult.From(AgentCertificateImportOutcome.Failed);
            SetImportFeedback(unavailable);
            return unavailable;
        }

        IsBusy = true;
        CertificateImportMessage = null;
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
            NotifyCertificateFeedbackChanged();
            return;
        }

        CertificateImportStateClass = "critical";
        CertificateImportMessage = result.Outcome switch
        {
            AgentCertificateImportOutcome.PasswordIncorrect => Strings["Https.Import.PasswordIncorrect"],
            AgentCertificateImportOutcome.UnsupportedFile => Strings["Https.Import.Unsupported"],
            AgentCertificateImportOutcome.InvalidFile => Strings["Https.Import.InvalidFile"],
            _ => Strings["Https.Import.Failed"],
        };
        NotifyCertificateFeedbackChanged();
    }

    private void ClearImportFeedback()
    {
        if (CertificateImportMessage is null) return;
        CertificateImportMessage = null;
        NotifyCertificateFeedbackChanged();
    }

    private void NotifyCertificateFeedbackChanged()
    {
        OnPropertyChanged(nameof(CertificateFeedbackMessage));
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
            return Strings.Format("Https.Cert.HostMismatch", host);
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

        OnPropertyChanged(nameof(ServiceState));
        OnPropertyChanged(nameof(ServiceIsRunning));
        OnPropertyChanged(nameof(ServiceIsInstalled));
        OnPropertyChanged(nameof(ServiceFooterText));
        OnPropertyChanged(nameof(ShowStartServiceAction));
        OnPropertyChanged(nameof(ShowStopServiceAction));
        RefreshCommandStates();
        RebuildDiagnostics();
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
        AgentConfigConfirmation.CreateGroupInDirectory => Strings["Operators.DirectoryConfirm"],
        AgentConfigConfirmation.DisableHttps => Strings["Cleanup.RemoveAndDisable"],
        AgentConfigConfirmation.RestartService => Strings["Service.Restart"],
        _ => Strings["Action.Confirm"],
    };

    public string? ConfirmationTitle => PendingConfirmation switch
    {
        AgentConfigConfirmation.CreateGroupInDirectory => Strings["Operators.DirectoryTitle"],
        AgentConfigConfirmation.DisableHttps => Strings["Cleanup.Title"],
        AgentConfigConfirmation.RestartService => Strings["Service.RestartTitle"],
        _ => null,
    };

    public string? ConfirmationMessage => PendingConfirmation switch
    {
        AgentConfigConfirmation.CreateGroupInDirectory => Strings["Operators.DirectoryWarning"],
        AgentConfigConfirmation.DisableHttps => Strings["Cleanup.Message"],
        AgentConfigConfirmation.RestartService => Strings["Service.RestartQuestion"],
        _ => null,
    };

    [RelayCommand]
    private void CancelConfirmation() => PendingConfirmation = AgentConfigConfirmation.None;

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
            case AgentConfigConfirmation.CreateGroupInDirectory:
                CreateGroupCore();
                return;

            case AgentConfigConfirmation.DisableHttps:
                await ApplyCoreAsync(
                    new AgentHttpsCleanupRequest(CleanupFirewallRule, CleanupSslBinding, CleanupUrlReservation),
                    cancellationToken).ConfigureAwait(true);
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

    public bool CanApply => IsDirty && !IsBusy && (!HttpsEnabled || HttpsIsValid);

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
        ApplyFailed = false;
        ApplyMessage = null;
        RefreshCommandStates();

        try
        {
            var notes = new List<string>();

            if (HttpsEnabled)
            {
                if (!HttpsIsValid)
                {
                    ApplyFailed = true;
                    ApplyMessage = HttpsValidationMessage;
                    return;
                }

                var binding = new AgentHttpsBinding(HttpsHost.Trim(), HttpsPort, CertificateThumbprint!);
                var applied = await Task.Run(() => _resources.Apply(binding), cancellationToken).ConfigureAwait(true);

                if (!applied.Succeeded)
                {
                    ApplyFailed = true;
                    ApplyMessage = applied.Failure;
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
                ApplyFailed = true;
                ApplyMessage = write.Failure;
                return;
            }

            _confirmed = document;
            RefreshDirty();
            RefreshResourceState();

            notes.Insert(0, Strings["Message.Saved"]);

            // Saving configuration never starts the agent and never restarts it silently. A running
            // service is offered a restart; a stopped one is left stopped and told that its new
            // configuration is waiting for whenever somebody starts it.
            if (_serviceState.IsRunning)
            {
                notes.Add(Strings["Service.RestartRequired"]);
                ApplyMessage = string.Join(" ", notes);
                PendingConfirmation = AgentConfigConfirmation.RestartService;
                return;
            }

            if (_serviceState.IsInstalled) notes.Add(Strings["Service.StoppedAfterApply"]);

            ApplyMessage = string.Join(" ", notes);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    /// <summary>Restores every field to the last saved document. Nothing on the machine is touched.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!IsDirty)
        {
            ApplyMessage = Strings["Message.NoChanges"];
            return;
        }

        LoadDocument(_confirmed);
        ApplyFailed = false;
        ApplyMessage = Strings["Message.Discarded"];
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
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanStartService));
        OnPropertyChanged(nameof(CanStopService));
        OnPropertyChanged(nameof(CanRestartService));
        OnPropertyChanged(nameof(CanImportCertificate));
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

        ApplyFailed = true;
        ApplyMessage = $"{Strings["Message.RefreshFailed"]} ({inner.GetType().Name}: {inner.Message})";
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

            ReloadCertificates(null, null, certificates);

            _confirmed = document;
            LoadDocument(document);

            ReloadGroup();
            ReloadService();
            RefreshResourceState();
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
        }
        else if (_appliedBinding is { } previous)
        {
            _resourceState = _resources.Describe(previous);
        }
        else
        {
            _resourceState = AgentHttpsResourceSnapshot.None;
        }

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

        ResourceStatus.Add(Describe(Strings["Resources.SslBinding"], _resourceState.SslBinding, "NutIconTls"));
        ResourceStatus.Add(Describe(Strings["Resources.UrlReservation"], _resourceState.UrlReservation, "NutIconRemote"));
        // The firewall row names its port when there is one to name, as the reference does.
        var firewall = HttpsEnabled
            ? Strings.Format("Resources.Firewall.Port", HttpsPort)
            : Strings["Resources.Firewall"];
        ResourceStatus.Add(Describe(firewall, _resourceState.FirewallRule, "NutIconShield"));
        ResourceStatus.Add(DescribeListener());
    }

    private AgentStatusItemViewModel DisabledResource(string label, string iconKey) =>
        AgentStatusItemViewModel.From(
            Strings, label, AgentDiagnosticState.NotConfigured, Strings["Diagnostics.Disabled"], iconKey);

    /// <summary>
    /// Whether the HTTPS listener is actually up.
    ///
    /// This is a composed answer, not a probe, and the difference is worth saying out loud: it reports
    /// that HTTPS is enabled, that its endpoint is valid, that the binding and the reservation are
    /// ours, and that the service is running — everything the listener needs. It does not open a
    /// socket, and it is not a claim that any client has ever authenticated over it. Those are separate
    /// facts, and the diagnostics view keeps them separate.
    /// </summary>
    private AgentStatusItemViewModel DescribeListener()
    {
        const string icon = "NutIconConnection";
        var label = Strings["Resources.Listener"];

        if (!HttpsEnabled)
        {
            return AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.NotConfigured, Strings["Diagnostics.Disabled"], icon);
        }

        if (!_serviceState.IsInstalled)
        {
            // Not installed is not stopped. Reporting a service that does not exist as merely stopped
            // sends an administrator to the wrong place, and it is exactly the conflation the
            // diagnostics rules forbid.
            return AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.Error, Strings["Resources.Listener.ServiceMissing"], icon);
        }

        if (!_serviceState.IsRunning)
        {
            // The configuration can be perfect and nothing is listening, because the service is not
            // running. Saying "ready" here would be the most misleading line on the screen.
            return AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.Attention, Strings["Resources.Listener.ServiceStopped"], icon);
        }

        if (!HttpsIsValid || !_resourceState.IsFullyConfigured)
        {
            return AgentStatusItemViewModel.From(
                Strings, label, AgentDiagnosticState.Attention, Strings["Resources.Listener.Incomplete"], icon);
        }

        return AgentStatusItemViewModel.From(
            Strings, label, AgentDiagnosticState.Ready, Strings.Format("Resources.Listener.Listening", HttpsEndpoint), icon);
    }

    private AgentStatusItemViewModel Describe(string label, AgentResourceState state, string iconKey)
    {
        var diagnostic = state.Ownership switch
        {
            AgentResourceOwnership.OwnedByNutManager => AgentDiagnosticState.Ready,
            // Present but somebody else's: not an error in NutManager, and not something to fix by
            // deleting it. Attention, with the detail saying whose it is.
            AgentResourceOwnership.ForeignOwner => AgentDiagnosticState.Attention,
            AgentResourceOwnership.Absent => HttpsEnabled ? AgentDiagnosticState.Error : AgentDiagnosticState.NotConfigured,
            _ => AgentDiagnosticState.Error,
        };

        var detail = state.Detail ?? state.Ownership switch
        {
            AgentResourceOwnership.Absent => Strings["Resources.Absent"],
            AgentResourceOwnership.ForeignOwner => Strings["Resources.Foreign"],
            AgentResourceOwnership.Unknown => Strings["Resources.Unknown"],
            _ => null,
        };

        return AgentStatusItemViewModel.From(Strings, label, diagnostic, detail, iconKey);
    }

    /// <summary>
    /// The diagnostics list.
    ///
    /// Each line is one fact and they are never merged: the agent being installed is not the agent
    /// running, NUT being installed is not NUT running, and a transport being enabled is not a client
    /// having authenticated over it. A screen that collapsed those would be easier to read and would
    /// answer the wrong question.
    /// </summary>
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
            _serviceState.IsInstalled ? AgentDiagnosticState.Ready : AgentDiagnosticState.Error,
            _serviceState.IsInstalled ? ServiceStateText : Strings["Diagnostics.NotInstalled"]));

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
