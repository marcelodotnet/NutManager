using System.Collections.Concurrent;
using NutManager.Core.Administration;

namespace NutManager.Core.Agent;

/// <summary>
/// Every rule the agent enforces, in one class, shared by every transport.
///
/// The named pipe and the HTTPS listener are adapters: they authenticate, they authorize, and then
/// they call in here. Nothing about starting, stopping or auditing a service exists on either side of
/// them, because two copies of a security decision is two chances to get it wrong and one of them
/// will be the copy nobody reviewed.
///
/// The order of checks inside a mutation is deliberate and is the substance of the task:
///
///   authorization → audit readiness → deduplication → exclusive gate → target revalidation → act
///
/// Authorization first, so an unauthorized caller cannot make the agent do work. Audit readiness
/// before the gate, so the agent never performs a privileged action it cannot account for.
/// Deduplication before acting, so a transport retry cannot stop a service twice. And revalidation
/// inside the gate, immediately before the call, because the window between "we checked" and "we
/// acted" is exactly where a swapped service binary would slip in.
/// </summary>
public sealed class NutAgentApplicationService
{
    private readonly INutServiceTargetResolver _resolver;
    private readonly INutServiceController _controller;
    private readonly INutAgentAuditSink _audit;
    private readonly INutAgentAuthorization _authorization;
    private readonly TimeProvider _time;
    private readonly NutAgentOptions _options;

    // Optional, and its absence is the capability answer: an agent assembled without an inspector
    // simply does not advertise the operation, so a client learns that from the handshake instead of
    // from a refused request.
    private readonly INutAgentHardwareInspector? _hardware;

    // One mutation at a time. Start, Stop and Restart may never interleave: a Stop landing between a
    // Restart's own stop and start would leave the service down while the restart reported success.
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    // Bounded, expiring memory of operations already performed, so a transport retry of the same
    // request returns the first answer instead of running the action again.
    private readonly ConcurrentDictionary<Guid, CompletedOperation> _completed = new();

    private NutServiceTarget? _target;
    private string? _targetFailure;

    public NutAgentApplicationService(
        INutServiceTargetResolver resolver,
        INutServiceController controller,
        INutAgentAuditSink audit,
        INutAgentAuthorization authorization,
        TimeProvider? timeProvider = null,
        NutAgentOptions? options = null,
        INutAgentHardwareInspector? hardwareInspector = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new NutAgentOptions();
        _hardware = hardwareInspector;
    }

    public string MachineName => _options.MachineName;

    /// <summary>Resolves the one service this agent may control. Runs once, at startup.</summary>
    public async Task<NutServiceTargetResolution> InitializeAsync(CancellationToken cancellationToken)
    {
        var resolution = await _resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        _target = resolution.IsResolved ? resolution.Target : null;
        _targetFailure = resolution.IsResolved ? null : Describe(resolution);

        if (!_authorization.IsConfigured)
        {
            await _audit.WriteAsync(
                new NutAgentAuditEntry(
                    NutAgentAuditKind.SecurityStartupFailure, _time.GetUtcNow(), Guid.Empty, "-", "-",
                    MachineName, NutAgentOperation.Handshake, _target?.ServiceName,
                    NutServiceState.Unknown, NutServiceState.Unknown, NutAgentResultCode.Unauthorized,
                    null, TimeSpan.Zero, _authorization.ConfigurationFailure),
                cancellationToken).ConfigureAwait(false);
        }

        return resolution;
    }

    /// <summary>
    /// What this agent will do right now. Control is advertised only when the operators group
    /// resolved, the audit sink is usable and a target is pinned — a client should be told that
    /// control is unavailable rather than discovering it by having a command refused.
    /// </summary>
    public async Task<NutAgentHandshake> HandshakeAsync(CancellationToken cancellationToken)
    {
        var auditReady = await _audit.IsReadyAsync(cancellationToken).ConfigureAwait(false);
        var reason = !_authorization.IsConfigured ? _authorization.ConfigurationFailure ?? "operators group unavailable"
            : _target is null ? _targetFailure ?? "NUT service not identified"
            : !auditReady ? "audit sink unavailable"
            : null;

        var controlAvailable = reason is null;
        List<NutAgentOperation> capabilities = [NutAgentOperation.Handshake, NutAgentOperation.GetStatus];
        if (controlAvailable)
        {
            capabilities.AddRange([NutAgentOperation.Start, NutAgentOperation.Stop, NutAgentOperation.Restart]);
        }

        // Deliberately outside the control gate. Serial devices exist whether or not this machine has
        // a NUT service the agent could pin, an operators group it could resolve for control, or a
        // usable audit sink — and refusing to describe hardware because control is off would report a
        // perfectly readable machine as having no ports. The transport still decided who may ask.
        if (_hardware is not null)
        {
            capabilities.Add(NutAgentOperation.GetHardwareSnapshot);
        }

        return new NutAgentHandshake(
            NutAgentOptions.ProtocolVersion, _options.AgentVersion, MachineName, capabilities, controlAvailable, reason);
    }

    /// <summary>
    /// Reads the current state. Not gated behind the mutation lock: a status poll during a restart
    /// must keep answering, and it changes nothing. Not audited either, because a polling client
    /// would bury the control record the Event Log exists to preserve.
    /// </summary>
    public async Task<NutAgentServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_target is null) return NutAgentServiceStatus.Unavailable(MachineName, _time.GetUtcNow());
        return await _controller.GetStatusAsync(_target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports the machine's serial devices.
    ///
    /// Outside the mutation gate and unaudited, for the same reasons <see cref="GetStatusAsync"/> is:
    /// it changes nothing, it must keep answering while a restart holds the gate, and an operator
    /// pressing Refresh must not be able to bury a control record under enumeration noise in the
    /// Event Log. The mutation audit policy is untouched by this.
    ///
    /// An inspector that throws is reported as an enumeration failure rather than being allowed to
    /// take the connection with it. The distinction that survives is the one that matters: "this
    /// machine has no serial ports" and "this machine could not be asked" stay different answers.
    /// </summary>
    public async Task<NutAgentHardwareSnapshot> GetHardwareSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_hardware is null)
        {
            return NutAgentHardwareSnapshot.Unavailable(
                MachineName, _time.GetUtcNow(), "This agent does not provide hardware inspection.");
        }

        try
        {
            var snapshot = await _hardware.InspectAsync(cancellationToken).ConfigureAwait(false);
            return Cap(snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return NutAgentHardwareSnapshot.Unavailable(MachineName, _time.GetUtcNow(), exception.GetType().Name);
        }
    }

    /// <summary>
    /// Keeps the snapshot inside the response ceiling both transports enforce.
    ///
    /// The ports that survive are real ports and are reported as such; the note says how many were
    /// left out. Letting an oversized frame go out instead would surface to the operator as a
    /// protocol failure, which says nothing about their hardware and sends them to the wrong place.
    /// </summary>
    private static NutAgentHardwareSnapshot Cap(NutAgentHardwareSnapshot snapshot)
    {
        if (snapshot.ComPorts.Count <= NutAgentHardware.MaxReportedPorts) return snapshot;

        var detected = snapshot.ComPorts.Count;
        return snapshot with
        {
            ComPorts = snapshot.ComPorts.Take(NutAgentHardware.MaxReportedPorts).ToArray(),
            Detail = snapshot.Detail ?? $"Reported {NutAgentHardware.MaxReportedPorts} of {detected} detected serial ports."
        };
    }

    public Task<NutAgentOperationResult> StartAsync(Guid operationId, NutAgentCallerContext caller, CancellationToken cancellationToken) =>
        MutateAsync(operationId, NutAgentOperation.Start, caller, cancellationToken);

    public Task<NutAgentOperationResult> StopAsync(Guid operationId, NutAgentCallerContext caller, CancellationToken cancellationToken) =>
        MutateAsync(operationId, NutAgentOperation.Stop, caller, cancellationToken);

    public Task<NutAgentOperationResult> RestartAsync(Guid operationId, NutAgentCallerContext caller, CancellationToken cancellationToken) =>
        MutateAsync(operationId, NutAgentOperation.Restart, caller, cancellationToken);

    private async Task<NutAgentOperationResult> MutateAsync(
        Guid operationId,
        NutAgentOperation operation,
        NutAgentCallerContext caller,
        CancellationToken cancellationToken)
    {
        var started = _time.GetUtcNow();

        if (!await IsCallerAuthorizedAsync(caller, cancellationToken).ConfigureAwait(false))
        {
            var refused = NutAgentOperationResult.Refused(operationId, operation, NutAgentResultCode.Unauthorized);
            await AuditAsync(NutAgentAuditKind.UnauthorizedAttempt, operationId, caller, operation, refused, cancellationToken)
                .ConfigureAwait(false);
            return refused;
        }

        // A privileged action nobody can account for is worse than no action, so this is checked
        // before the agent commits to anything, not after the service has already moved.
        if (!await _audit.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return NutAgentOperationResult.Refused(
                operationId, operation, NutAgentResultCode.AuditUnavailable, "The audit sink is not usable.");
        }

        if (_target is null)
        {
            return NutAgentOperationResult.Refused(
                operationId, operation, NutAgentResultCode.TargetUnavailable, _targetFailure);
        }

        PruneCompleted();
        if (_completed.TryGetValue(operationId, out var earlier))
        {
            // The same operation id arriving twice is a transport retry, not a second intention.
            return earlier.Result;
        }

        if (!await _mutationGate.WaitAsync(_options.GateWaitTimeout, cancellationToken).ConfigureAwait(false))
        {
            return NutAgentOperationResult.Refused(operationId, operation, NutAgentResultCode.Busy);
        }

        try
        {
            if (_completed.TryGetValue(operationId, out earlier)) return earlier.Result;

            var revalidation = await _resolver.RevalidateAsync(_target, cancellationToken).ConfigureAwait(false);
            if (!revalidation.IsResolved ||
                !string.Equals(revalidation.Target!.ServiceName, _target.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                var refused = NutAgentOperationResult.Refused(
                    operationId, operation, NutAgentResultCode.TargetRevalidationFailed, Describe(revalidation));
                await AuditAsync(NutAgentAuditKind.TargetRevalidationFailure, operationId, caller, operation, refused, cancellationToken)
                    .ConfigureAwait(false);
                return refused;
            }

            await AuditAsync(NutAgentAuditKind.OperationRequested, operationId, caller, operation,
                NutAgentOperationResult.Refused(operationId, operation, NutAgentResultCode.Success), cancellationToken)
                .ConfigureAwait(false);

            var result = operation switch
            {
                NutAgentOperation.Start => await RunStartAsync(operationId, cancellationToken).ConfigureAwait(false),
                NutAgentOperation.Stop => await RunStopAsync(operationId, cancellationToken).ConfigureAwait(false),
                NutAgentOperation.Restart => await RunRestartAsync(operationId, cancellationToken).ConfigureAwait(false),
                _ => NutAgentOperationResult.Refused(operationId, operation, NutAgentResultCode.MalformedRequest)
            };

            result = result with { Duration = _time.GetUtcNow() - started };

            var written = await AuditAsync(
                result.Succeeded ? NutAgentAuditKind.OperationSucceeded : NutAgentAuditKind.OperationFailed,
                operationId, caller, operation, result, cancellationToken).ConfigureAwait(false);

            // The action already happened. Saying so, and saying that the record failed, is the only
            // honest report — pretending otherwise, or trying to undo it, would both be worse.
            if (!written && result.Succeeded)
            {
                result = result with { Code = NutAgentResultCode.CompletedWithAuditFailure };
            }

            _completed[operationId] = new CompletedOperation(result, _time.GetUtcNow());
            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Fail-closed, including when the lookup itself breaks. A group query can throw — the domain is
    /// unreachable, the SID no longer resolves — and an exception escaping here would either crash the
    /// request or, worse, be caught somewhere upstream that treats "no answer" as "no objection".
    /// The answer to a question we could not ask is no.
    /// </summary>
    private async Task<bool> IsCallerAuthorizedAsync(NutAgentCallerContext caller, CancellationToken cancellationToken)
    {
        if (!caller.IsAuthorized || !_authorization.IsConfigured) return false;

        try
        {
            return await _authorization.IsAuthorizedAsync(caller.Identity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<NutAgentOperationResult> RunStartAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var initial = await _controller.GetStatusAsync(_target!, cancellationToken).ConfigureAwait(false);
        if (initial.ServiceState == NutServiceState.Running)
        {
            return Result(operationId, NutAgentOperation.Start, NutAgentResultCode.AlreadyInRequestedState,
                initial.ServiceState, initial.ServiceState);
        }

        var outcome = await _controller.StartAsync(_target!, _options.ServiceStateTimeout, cancellationToken).ConfigureAwait(false);
        return Result(operationId, NutAgentOperation.Start, outcome.Code, initial.ServiceState, outcome.FinalState,
            win32: outcome.Win32ErrorCode, detail: outcome.Detail);
    }

    private async Task<NutAgentOperationResult> RunStopAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var initial = await _controller.GetStatusAsync(_target!, cancellationToken).ConfigureAwait(false);
        if (initial.ServiceState == NutServiceState.Stopped)
        {
            return Result(operationId, NutAgentOperation.Stop, NutAgentResultCode.AlreadyInRequestedState,
                initial.ServiceState, initial.ServiceState);
        }

        var outcome = await _controller.StopAsync(_target!, _options.ServiceStateTimeout, cancellationToken).ConfigureAwait(false);
        return Result(operationId, NutAgentOperation.Stop, outcome.Code, initial.ServiceState, outcome.FinalState,
            win32: outcome.Win32ErrorCode, detail: outcome.Detail);
    }

    /// <summary>
    /// Stop then start, without releasing the gate between them. A client-side restart would leave a
    /// window in which another request could start the service the client was about to start, or in
    /// which a crash leaves the service down with nobody responsible for raising it.
    /// </summary>
    private async Task<NutAgentOperationResult> RunRestartAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var initial = await _controller.GetStatusAsync(_target!, cancellationToken).ConfigureAwait(false);
        var stopCode = NutAgentResultCode.AlreadyInRequestedState;
        var state = initial.ServiceState;
        int? win32 = null;
        string? detail = null;

        if (state != NutServiceState.Stopped)
        {
            var stop = await _controller.StopAsync(_target!, _options.ServiceStateTimeout, cancellationToken).ConfigureAwait(false);
            stopCode = stop.Code;
            state = stop.FinalState;
            win32 = stop.Win32ErrorCode;
            detail = stop.Detail;

            if (stop.Code is not (NutAgentResultCode.Success or NutAgentResultCode.AlreadyInRequestedState))
            {
                // The start phase is not attempted: restarting a service we could not stop would mean
                // starting a second instance or reporting a success that never happened.
                return Result(operationId, NutAgentOperation.Restart, stop.Code, initial.ServiceState, state,
                    NutAgentRestartPhase.Stop, stopCode, null, win32, detail);
            }
        }

        var start = await _controller.StartAsync(_target!, _options.ServiceStateTimeout, cancellationToken).ConfigureAwait(false);
        var failedPhase = start.Code is NutAgentResultCode.Success or NutAgentResultCode.AlreadyInRequestedState
            ? NutAgentRestartPhase.None
            : NutAgentRestartPhase.Start;

        return Result(operationId, NutAgentOperation.Restart, start.Code, initial.ServiceState, start.FinalState,
            failedPhase, stopCode, start.Code, start.Win32ErrorCode, start.Detail);
    }

    private NutAgentOperationResult Result(
        Guid operationId,
        NutAgentOperation operation,
        NutAgentResultCode code,
        NutServiceState initial,
        NutServiceState final,
        NutAgentRestartPhase failedPhase = NutAgentRestartPhase.None,
        NutAgentResultCode? stopPhase = null,
        NutAgentResultCode? startPhase = null,
        int? win32 = null,
        string? detail = null) =>
        new(operationId, operation, code, initial, final, failedPhase, stopPhase, startPhase, win32, TimeSpan.Zero, detail);

    private async Task<bool> AuditAsync(
        NutAgentAuditKind kind,
        Guid operationId,
        NutAgentCallerContext caller,
        NutAgentOperation operation,
        NutAgentOperationResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _audit.WriteAsync(
                new NutAgentAuditEntry(
                    kind, _time.GetUtcNow(), operationId, caller.Identity, caller.Transport, MachineName,
                    operation, _target?.ServiceName, result.InitialState, result.FinalState, result.Code,
                    result.Win32ErrorCode, result.Duration, result.Detail),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A sink that throws is a sink that is not ready; it must not take the operation with it.
            return false;
        }
    }

    /// <summary>Keeps the retry memory bounded, so a long-lived agent cannot grow it without limit.</summary>
    private void PruneCompleted()
    {
        if (_completed.Count < _options.CompletedOperationCapacity) return;

        var cutoff = _time.GetUtcNow() - _options.CompletedOperationRetention;
        foreach (var pair in _completed)
        {
            if (pair.Value.CompletedAt <= cutoff) _completed.TryRemove(pair.Key, out _);
        }

        // Still full of recent entries: drop the oldest rather than let the map grow unbounded.
        while (_completed.Count >= _options.CompletedOperationCapacity)
        {
            var oldest = _completed.OrderBy(pair => pair.Value.CompletedAt).FirstOrDefault();
            if (oldest.Key == Guid.Empty && oldest.Value is null) break;
            if (!_completed.TryRemove(oldest.Key, out _)) break;
        }
    }

    private static string Describe(NutServiceTargetResolution resolution) => resolution.Status switch
    {
        NutServiceTargetStatus.NotFound => "No NUT service was identified on this machine.",
        NutServiceTargetStatus.Ambiguous => resolution.Candidates is { Count: > 0 } candidates
            ? $"More than one candidate NUT service: {string.Join(", ", candidates)}."
            : "More than one candidate NUT service.",
        NutServiceTargetStatus.ValidationFailed => resolution.Detail ?? "The service no longer validates as NUT.",
        NutServiceTargetStatus.QueryFailed => resolution.Detail ?? "The local SCM could not be queried.",
        _ => resolution.Detail
    } ?? "Unavailable.";

    private sealed record CompletedOperation(NutAgentOperationResult Result, DateTimeOffset CompletedAt);
}

/// <summary>Tunables with conservative defaults. None of them can widen a security decision.</summary>
public sealed class NutAgentOptions
{
    public const int ProtocolVersion = 1;

    public string MachineName { get; init; } = Environment.MachineName;

    public string AgentVersion { get; init; } = "1.0.0";

    /// <summary>How long to wait for the service to reach the expected state before giving up.</summary>
    public TimeSpan ServiceStateTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a request waits for the mutation gate before reporting Busy.</summary>
    public TimeSpan GateWaitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int CompletedOperationCapacity { get; init; } = 128;

    public TimeSpan CompletedOperationRetention { get; init; } = TimeSpan.FromMinutes(10);
}
