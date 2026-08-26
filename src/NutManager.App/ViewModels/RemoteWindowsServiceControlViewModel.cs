using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NutManager.App.Localization;
using NutManager.Core.Agent;
using NutManager.Core.Models;

namespace NutManager.App.ViewModels;

/// <summary>
/// Starts, stops and restarts the NUT service on the managed server, through the agent.
///
/// It is a separate type from <see cref="RemoteWindowsServiceViewModel"/> and that separation is
/// load-bearing rather than tidy: T34 established that the monitor holds no way to act, and a test
/// asserts it by reflecting over the monitor's commands. Adding control here keeps that assertion
/// true and keeps the two capabilities reviewable apart.
///
/// It observes rather than polls. The monitor is the single source of the current reading, so this
/// class reads state from it and asks it to refresh after an operation — a second polling loop would
/// double the round trips and let the two disagree about what the service is doing.
/// </summary>
public sealed partial class RemoteWindowsServiceControlViewModel : ObservableObject
{
    private readonly RemoteWindowsServiceViewModel _monitor;
    private INutManagerAgentClient _client;
    private readonly NutManagerLocalizer _strings;

    public RemoteWindowsServiceControlViewModel(
        RemoteWindowsServiceViewModel monitor,
        INutManagerAgentClient client,
        UiLanguagePreference language = UiLanguagePreference.PtBr)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _strings = new NutManagerLocalizer(language);

        // The buttons follow the reading, so the panel re-evaluates whenever the monitor publishes.
        _monitor.PropertyChanged += (_, _) => RaiseAvailability();
    }

    public NutManagerLocalizer Strings => _strings;

    public string Host => _monitor.Host;

    /// <summary>True while a mutation is in flight. Status polling is deliberately not blocked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(CanRestart))]
    private bool _isBusy;

    /// <summary>
    /// The action waiting for the operator to confirm it, or null.
    ///
    /// Confirmation is modelled as state rather than as a dialog service: the view renders it, and a
    /// test can assert that nothing reached the agent before it was answered without opening a
    /// window. Stop and Restart both pass through here; Start does not, because starting a service
    /// that should be running is not the action anyone needs protecting from.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    [NotifyPropertyChangedFor(nameof(ConfirmationText))]
    private NutAgentOperation? _pendingConfirmation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultText;

    public bool IsConfirming => PendingConfirmation is not null;

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);

    /// <summary>
    /// Names the host, the action and the service. "Are you sure?" tells an operator with three
    /// servers open nothing at all about which one is about to lose its UPS monitoring.
    /// </summary>
    public string? ConfirmationText => PendingConfirmation switch
    {
        NutAgentOperation.Stop => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _strings.Get("RemoteService.Control.ConfirmStop"),
            HostText,
            ServiceText),
        NutAgentOperation.Restart => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _strings.Get("RemoteService.Control.ConfirmRestart"),
            HostText,
            ServiceText),
        _ => null
    };

    private string HostText => string.IsNullOrWhiteSpace(Host) ? _strings.Get("Common.Unavailable") : Host;

    private string ServiceText => _monitor.ServiceIdentityText;

    public bool CanStart => IsAvailable(NutAgentOperation.Start) && !_monitor.IsServiceRunning;

    public bool CanStop => IsAvailable(NutAgentOperation.Stop) && !_monitor.IsServiceStopped;

    public bool CanRestart => IsAvailable(NutAgentOperation.Restart);

    /// <summary>
    /// An action is offered only when the agent advertised it and nothing else is running. The
    /// service's own state is never enough on its own: an agent whose audit sink is unusable reports
    /// a perfectly healthy service and still refuses to touch it.
    /// </summary>
    private bool IsAvailable(NutAgentOperation operation) =>
        !IsBusy && !IsConfirming && _monitor.IsControlAvailable && _monitor.Supports(operation);

    public string StartText => _strings.Get("RemoteService.Control.Start");
    public string StopText => _strings.Get("RemoteService.Control.Stop");
    public string RestartText => _strings.Get("RemoteService.Control.Restart");
    public string ConfirmText => _strings.Get("RemoteService.Control.Confirm");
    public string CancelText => _strings.Get("RemoteService.Control.Cancel");
    public string BusyText => _strings.Get("RemoteService.Control.Busy");

    /// <summary>Start needs no confirmation, so it runs directly.</summary>
    [RelayCommand]
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        CanStart ? ExecuteAsync(NutAgentOperation.Start, cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Asks first. Nothing reaches the agent until <see cref="ConfirmAsync"/> runs, which is the
    /// property the confirmation tests pin down.
    /// </summary>
    [RelayCommand]
    public void Stop()
    {
        if (CanStop) PendingConfirmation = NutAgentOperation.Stop;
    }

    [RelayCommand]
    public void Restart()
    {
        if (CanRestart) PendingConfirmation = NutAgentOperation.Restart;
    }

    [RelayCommand]
    public void CancelConfirmation() => PendingConfirmation = null;

    /// <summary>
    /// Points control operations at the same client the monitor was just rebound to.
    ///
    /// The two must never disagree. A monitor reporting over HTTPS while Stop still travels the named
    /// pipe would show an operator one transport and use another, and the audit entry would name the
    /// wrong one.
    ///
    /// Any confirmation waiting at the time is discarded rather than carried across. It was asked
    /// about a connection that no longer exists, and answering it would send an operation somewhere
    /// the operator never agreed to.
    /// </summary>
    public void Rebind(INutManagerAgentClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (ReferenceEquals(_client, client)) return;

        _client = client;
        PendingConfirmation = null;
    }

    [RelayCommand]
    public async Task ConfirmAsync(CancellationToken cancellationToken = default)
    {
        if (PendingConfirmation is not { } operation) return;

        PendingConfirmation = null;
        await ExecuteAsync(operation, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs one operation under one identifier.
    ///
    /// The id is generated once, here, and never regenerated: it is what lets the agent recognise a
    /// resent request as the same intention rather than a second one. There is no automatic retry —
    /// a stop that failed is reported, and whether to try again is the operator's decision.
    /// </summary>
    private async Task ExecuteAsync(NutAgentOperation operation, CancellationToken cancellationToken)
    {
        if (IsBusy) return;

        IsBusy = true;
        ResultText = null;
        var operationId = Guid.NewGuid();

        try
        {
            var result = operation switch
            {
                NutAgentOperation.Start => await _client.StartAsync(Host, operationId, cancellationToken).ConfigureAwait(true),
                NutAgentOperation.Stop => await _client.StopAsync(Host, operationId, cancellationToken).ConfigureAwait(true),
                NutAgentOperation.Restart => await _client.RestartAsync(Host, operationId, cancellationToken).ConfigureAwait(true),
                _ => NutAgentClientResult<NutAgentOperationResult>.Failure(NutAgentClientStatus.Failed)
            };

            ResultText = Describe(operation, result);
        }
        catch (OperationCanceledException)
        {
            // The surface went away mid-operation. The agent still finished what it was told to do,
            // and the next reading will show it.
        }
        finally
        {
            IsBusy = false;
        }

        // One refresh, through the monitor, so there is still only ever one probe in flight.
        await _monitor.RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Turns the outcome into something an operator can act on. A restart whose stop worked and whose
    /// start failed leaves the service down, and that is the sentence that matters most.
    /// </summary>
    private string Describe(NutAgentOperation operation, NutAgentClientResult<NutAgentOperationResult> result)
    {
        if (result.Status != NutAgentClientStatus.Success)
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _strings.Get("RemoteService.Control.TransportFailure"),
                TransportFailureText(result.Status));
        }

        if (result.Value is not { } outcome)
        {
            return _strings.Get("RemoteService.Control.Failed");
        }

        if (outcome is { Operation: NutAgentOperation.Restart, FailedPhase: NutAgentRestartPhase.Start })
        {
            return _strings.Get("RemoteService.Control.RestartLeftStopped");
        }

        return outcome.Code switch
        {
            NutAgentResultCode.Success => _strings.Get(SuccessKey(operation)),
            NutAgentResultCode.AlreadyInRequestedState => _strings.Get("RemoteService.Control.AlreadyInState"),
            NutAgentResultCode.CompletedWithAuditFailure => _strings.Get("RemoteService.Control.CompletedWithAuditFailure"),
            NutAgentResultCode.Unauthorized => _strings.Get("RemoteService.Control.Unauthorized"),
            NutAgentResultCode.AuditUnavailable => _strings.Get("RemoteService.Control.AuditUnavailable"),
            NutAgentResultCode.TargetUnavailable => _strings.Get("RemoteService.Control.TargetUnavailable"),
            NutAgentResultCode.TargetRevalidationFailed => _strings.Get("RemoteService.Control.TargetRevalidationFailed"),
            NutAgentResultCode.Busy => _strings.Get("RemoteService.Control.Busy.Refused"),
            NutAgentResultCode.TimedOut => _strings.Get("RemoteService.Control.TimedOut"),
            NutAgentResultCode.ServiceControlFailed => _strings.Get("RemoteService.Control.ServiceControlFailed"),
            _ => _strings.Get("RemoteService.Control.Failed")
        };
    }

    private static string SuccessKey(NutAgentOperation operation) => operation switch
    {
        NutAgentOperation.Start => "RemoteService.Control.Started",
        NutAgentOperation.Stop => "RemoteService.Control.Stopped",
        _ => "RemoteService.Control.Restarted"
    };

    private string TransportFailureText(NutAgentClientStatus status) => status switch
    {
        NutAgentClientStatus.AgentUnavailable => _strings.Get("RemoteService.Agent.Unavailable"),
        NutAgentClientStatus.AccessDenied => _strings.Get("RemoteService.Agent.AccessDenied"),
        NutAgentClientStatus.HostUnreachable => _strings.Get("RemoteService.Agent.HostUnreachable"),
        NutAgentClientStatus.TimedOut => _strings.Get("RemoteService.Agent.TimedOut"),
        NutAgentClientStatus.ProtocolFailure => _strings.Get("RemoteService.Agent.ProtocolFailure"),
        _ => _strings.Get("RemoteService.Agent.Failed")
    };

    private void RaiseAvailability()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(ConfirmationText));
    }
}
