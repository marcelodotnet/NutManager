using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NutManager.App.Localization;
using NutManager.Core.Administration;
using NutManager.Core.Agent;
using NutManager.Core.Models;

namespace NutManager.App.ViewModels;

/// <summary>
/// Watches the Windows service running NUT on a remote host, and does nothing else.
///
/// There is no start, stop or restart here, and that is the point rather than an omission: the view
/// binds to this object, so a control the view could invoke would have to exist as a command on this
/// class. None does, which makes "monitoring cannot act on the server" checkable in one file, and
/// T35 keeps that true by putting control in a separate type rather than by adding commands here.
///
/// It reads through the agent now rather than through a remote SCM call, so the cross-machine
/// authentication T34 could not satisfy is off the path. What has not changed is the separation: the
/// agent's reachability and NUT's own health are different facts, and a failure here never touches
/// the connection state the rest of the shell shows.
/// </summary>
public sealed partial class RemoteWindowsServiceViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Conservative on purpose: this is a round trip to another machine, not a field read.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private INutManagerAgentClient _client;
    private readonly NutManagerLocalizer _strings;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private NutAgentClientResult<NutAgentHandshake>? _handshake;
    private Task? _inFlight;
    private CancellationTokenSource? _lifetime;
    private Task? _polling;
    private int _generation;
    private bool _disposed;

    public RemoteWindowsServiceViewModel(
        string host,
        INutManagerAgentClient client,
        UiLanguagePreference language = UiLanguagePreference.PtBr,
        TimeSpan? interval = null,
        NutAgentTransportKind transport = NutAgentTransportKind.NamedPipe,
        TimeProvider? timeProvider = null)
    {
        Host = string.IsNullOrWhiteSpace(host) ? string.Empty : host.Trim();
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _strings = new NutManagerLocalizer(language);
        _interval = interval ?? DefaultInterval;
        _time = timeProvider ?? TimeProvider.System;
        Transport = transport;
    }

    public string Host { get; }

    public NutManagerLocalizer Strings => _strings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServiceIdentityText))]
    [NotifyPropertyChangedFor(nameof(ServiceStateText))]
    [NotifyPropertyChangedFor(nameof(ProcessText))]
    [NotifyPropertyChangedFor(nameof(ProcessIdText))]
    [NotifyPropertyChangedFor(nameof(AgentStateText))]
    [NotifyPropertyChangedFor(nameof(IsServiceRunning))]
    [NotifyPropertyChangedFor(nameof(IsServiceStopped))]
    [NotifyPropertyChangedFor(nameof(IsServiceTransitioning))]
    [NotifyPropertyChangedFor(nameof(IsAgentUnavailable))]
    [NotifyPropertyChangedFor(nameof(IsAgentReachable))]
    [NotifyPropertyChangedFor(nameof(IsControlAvailable))]
    [NotifyPropertyChangedFor(nameof(HasObservation))]
    [NotifyPropertyChangedFor(nameof(IsShowingStaleReading))]
    [NotifyPropertyChangedFor(nameof(ObservedAtText))]
    [NotifyPropertyChangedFor(nameof(DiagnosticText))]
    [NotifyPropertyChangedFor(nameof(HasDiagnostic))]
    [NotifyPropertyChangedFor(nameof(ControlUnavailableText))]
    [NotifyPropertyChangedFor(nameof(HasControlUnavailableReason))]
    private NutAgentObservation? _observation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingStaleReading))]
    private bool _isRefreshing;

    public bool HasObservation => Observation is not null;

    /// <summary>
    /// A refresh in progress does not blank the panel, so the previous reading stays legible. It is
    /// labelled while it does, because an old state presented as current is worse than no state.
    /// </summary>
    public bool IsShowingStaleReading => IsRefreshing && Observation is not null;

    public string HostText => string.IsNullOrEmpty(Host) ? _strings.Get("Common.Unavailable") : Host;

    /// <summary>Which transport this profile uses to reach the agent, for the panel to name.</summary>
    public NutAgentTransportKind Transport { get; private set; }

    /// <summary>
    /// Points this monitor at a differently configured agent, without restarting the application.
    ///
    /// Changing a profile's agent transport used to require a restart, because the client was built
    /// once at startup and held for the process. Nothing about the client demands that: it carries a
    /// transport and a credential, and both are answerable at any moment.
    ///
    /// The generation counter is what makes the swap safe. A probe already in flight against the old
    /// client finishes against a generation that no longer matches, so its result is discarded rather
    /// than published as though it described the new endpoint — which is the failure worth preventing,
    /// since a stale success would show a working connection over a transport nobody is using.
    ///
    /// Deliberately narrow. Nothing here touches the NUT session, the configuration transport or the
    /// polling that feeds them. Only the agent's own client is replaced.
    /// </summary>
    public async Task RebindAsync(
        INutManagerAgentClient client,
        NutAgentTransportKind transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        INutManagerAgentClient previous;
        lock (_gate)
        {
            if (_disposed) return;
            if (ReferenceEquals(_client, client) && Transport == transport) return;

            // Anything still running belongs to the old client and must not publish.
            Interlocked.Increment(ref _generation);

            previous = _client;
            _client = client;
            Transport = transport;
            _handshake = null;
        }

        OnPropertyChanged(nameof(Transport));
        OnPropertyChanged(nameof(TransportText));

        // The old client is released after the swap, so nothing in flight is reading a disposed one.
        switch (previous)
        {
            case IAsyncDisposable asyncDisposable when !ReferenceEquals(previous, client):
                await asyncDisposable.DisposeAsync().ConfigureAwait(true);
                break;
            case IDisposable disposable when !ReferenceEquals(previous, client):
                disposable.Dispose();
                break;
        }

        // The screen must not keep showing what the previous transport reported.
        Observation = null;
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    public string TransportText => Transport == NutAgentTransportKind.Https
        ? _strings.Get("RemoteService.Transport.Https")
        : _strings.Get("RemoteService.Transport.NamedPipe");

    public string ServiceIdentityText => Observation?.Service?.DisplayName
        ?? Observation?.Service?.ServiceName
        ?? _strings.Get("Common.Unavailable");

    public string ServiceStateText => Observation?.Service is not { } service
        ? _strings.Get("Status.Unavailable")
        : service.ServiceState switch
        {
            NutServiceState.Running => _strings.Get("ServiceState.Running"),
            NutServiceState.Stopped => _strings.Get("ServiceState.Stopped"),
            NutServiceState.StartPending => _strings.Get("ServiceState.StartPending"),
            NutServiceState.StopPending => _strings.Get("ServiceState.StopPending"),
            NutServiceState.Paused => _strings.Get("ServiceState.Paused"),
            NutServiceState.PausePending => _strings.Get("ServiceState.PausePending"),
            NutServiceState.ContinuePending => _strings.Get("ServiceState.ContinuePending"),
            NutServiceState.Failed => _strings.Get("ServiceState.Failed"),
            _ => _strings.Get("Status.Unavailable")
        };

    /// <summary>The executable the agent reported. Absent stays absent rather than becoming a guess.</summary>
    public string ProcessText => Observation?.Service switch
    {
        { HasProcess: true, ExecutableName: { } executable } => executable,
        { HasProcess: true } => _strings.Get("RemoteService.Process.Unnamed"),
        { } => _strings.Get("RemoteService.Process.NotRunning"),
        _ => _strings.Get("Status.Unavailable")
    };

    public string ProcessIdText => Observation?.Service is { ProcessId: { } pid and > 0 }
        ? pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "—";

    /// <summary>
    /// What the agent itself is doing, which is not what NUT is doing. An unreachable agent is an
    /// administrative fact about this transport and says nothing about upsd.
    /// </summary>
    public string AgentStateText => Observation?.AgentStatus switch
    {
        NutAgentClientStatus.Success => _strings.Get("RemoteService.Agent.Connected"),
        NutAgentClientStatus.AgentUnavailable => _strings.Get("RemoteService.Agent.Unavailable"),
        NutAgentClientStatus.AccessDenied => _strings.Get("RemoteService.Agent.AccessDenied"),
        NutAgentClientStatus.HostUnreachable => _strings.Get("RemoteService.Agent.HostUnreachable"),
        NutAgentClientStatus.TimedOut => _strings.Get("RemoteService.Agent.TimedOut"),
        NutAgentClientStatus.ProtocolFailure => _strings.Get("RemoteService.Agent.ProtocolFailure"),
        NutAgentClientStatus.Failed => _strings.Get("RemoteService.Agent.Failed"),
        _ => _strings.Get("Status.Unavailable")
    };

    public bool IsAgentReachable => Observation is { AgentReachable: true };

    public bool IsAgentUnavailable => Observation is not null && !Observation.AgentReachable;

    /// <summary>Whether the agent says it can control anything right now. Read from the handshake.</summary>
    public bool IsControlAvailable => Observation is { ControlAvailable: true };

    public bool Supports(NutAgentOperation operation) => Observation?.Supports(operation) == true;

    public bool IsServiceRunning => Observation?.Service is { ServiceState: NutServiceState.Running };

    public bool IsServiceStopped => Observation?.Service is { ServiceState: NutServiceState.Stopped };

    public bool IsServiceTransitioning => Observation?.Service is { } transitioning &&
        transitioning.ServiceState is NutServiceState.StartPending or NutServiceState.StopPending
            or NutServiceState.PausePending or NutServiceState.ContinuePending or NutServiceState.Paused;

    /// <summary>
    /// Why control is off, in the agent's own words. An operator told only "control unavailable" has
    /// to go and read an Event Log to discover that a group is missing.
    /// </summary>
    public string? ControlUnavailableText => Observation switch
    {
        { AgentReachable: true, Handshake: { ControlAvailable: false, ControlUnavailableReason: { } reason } } => reason,
        { AgentReachable: true, Handshake: { ControlAvailable: false } } => _strings.Get("RemoteService.ControlUnavailable"),
        _ => null
    };

    public bool HasControlUnavailableReason => !string.IsNullOrWhiteSpace(ControlUnavailableText);

    /// <summary>The numeric Windows code, kept for diagnostics; never a localized message parsed back.</summary>
    public string? DiagnosticText => Observation switch
    {
        { Win32ErrorCode: { } code } => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _strings.Get("RemoteService.Query.Win32Code"),
            code),
        { AgentStatus: NutAgentClientStatus.ProtocolFailure, Code: { } protocolCode } => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _strings.Get("RemoteService.Agent.ProtocolDetail"),
            protocolCode),
        _ => null
    };

    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(DiagnosticText);

    public string ObservedAtText => Observation is { } observation
        ? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _strings.Get("RemoteService.ObservedAt"),
            observation.ObservedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture))
        : string.Empty;

    public string TitleText => _strings.Get("RemoteService.Title");
    public string HostLabel => _strings.Get("RemoteService.Host");
    public string ServiceLabel => _strings.Get("RemoteService.Service");
    public string StateLabel => _strings.Get("RemoteService.State");
    public string ProcessLabel => _strings.Get("RemoteService.Process");
    public string ProcessIdLabel => _strings.Get("RemoteService.ProcessId");
    public string AgentLabel => _strings.Get("RemoteService.Agent.Label");
    public string TransportLabel => _strings.Get("RemoteService.Transport.Label");
    public string RefreshText => _strings.Get("RemoteService.Refresh");
    public string RefreshingText => _strings.Get("RemoteService.Refreshing");

    /// <summary>
    /// Runs one probe, or joins the one already running. Never two: a blocked RPC can outlive its
    /// interval, and starting a second call each tick would pile threads onto a host that is already
    /// not answering.
    /// </summary>
    [RelayCommand]
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_inFlight is { IsCompleted: false }) return _inFlight;

            _inFlight = RunProbeAsync(Interlocked.Increment(ref _generation), cancellationToken);
            return _inFlight;
        }
    }

    /// <summary>
    /// One reading: the handshake when it is not already known, then the status.
    ///
    /// The handshake is cached because capabilities do not change between two ticks of a ten-second
    /// poll, and asking twice per tick would double the round trips for an answer that is almost
    /// always the same. It is dropped whenever the agent stops answering, so a reconnected agent is
    /// always re-interrogated rather than trusted from a previous run.
    /// </summary>
    private async Task RunProbeAsync(int generation, CancellationToken cancellationToken)
    {
        IsRefreshing = true;
        try
        {
            var handshake = _handshake ?? await _client.HandshakeAsync(Host, cancellationToken).ConfigureAwait(true);
            _handshake = handshake.Succeeded ? handshake : null;

            var status = handshake.Succeeded
                ? await _client.GetStatusAsync(Host, cancellationToken).ConfigureAwait(true)
                : null;

            if (status is { Succeeded: false }) _handshake = null;

            var result = NutAgentObservation.From(Host, handshake, status, _time.GetUtcNow());

            // Only the newest probe may publish. Stopping the monitor or disposing it bumps the
            // generation, so a call that returns after the surface is gone lands nowhere.
            if (generation == Volatile.Read(ref _generation)) Observation = result;
        }
        catch (OperationCanceledException)
        {
            // A cancelled probe publishes nothing. It does not clear the previous reading either:
            // navigating away is not evidence that the service changed.
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation)) IsRefreshing = false;
        }
    }

    /// <summary>
    /// Starts polling. Called when the surface becomes visible, so an unwatched panel costs nothing
    /// and no timer outlives the screen that needed it.
    /// </summary>
    public void StartMonitoring()
    {
        lock (_gate)
        {
            if (_disposed || _lifetime is not null) return;
            _lifetime = new CancellationTokenSource();
            _polling = PollAsync(_lifetime.Token);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(true);

            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task StopMonitoringAsync()
    {
        CancellationTokenSource? lifetime;
        Task? polling;
        lock (_gate)
        {
            lifetime = _lifetime;
            polling = _polling;
            _lifetime = null;
            _polling = null;
        }

        // Invalidated first and unconditionally. A probe already inside Win32 cannot be recalled, so
        // this guard is what keeps its late result from writing into a view model nobody is watching —
        // and it has to happen even when no polling loop was ever started, because a single manual
        // refresh can still be in flight when the surface goes away.
        Interlocked.Increment(ref _generation);

        if (lifetime is null)
        {
            IsRefreshing = false;
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);

        if (polling is not null)
        {
            try
            {
                await polling.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lifetime.Dispose();
        IsRefreshing = false;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        await StopMonitoringAsync().ConfigureAwait(false);
    }
}
