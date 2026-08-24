using NutManager.Core.Administration;

namespace NutManager.Core.Agent;

/// <summary>
/// Resolves and re-checks the one service the agent is allowed to touch.
///
/// Two verbs, and the difference matters. <see cref="ResolveAsync"/> runs at startup and decides
/// whether this agent has a NUT service at all. <see cref="RevalidateAsync"/> runs immediately before
/// every mutation, because a service can be reconfigured between two requests: an administrator
/// repointing the binary must not turn the agent into a controller for something else.
/// </summary>
public interface INutServiceTargetResolver
{
    Task<NutServiceTargetResolution> ResolveAsync(CancellationToken cancellationToken);

    Task<NutServiceTargetResolution> RevalidateAsync(NutServiceTarget target, CancellationToken cancellationToken);
}

/// <summary>The service the agent has pinned itself to.</summary>
/// <param name="ServiceName">The SCM name, decided locally and never accepted from a caller.</param>
/// <param name="DisplayName">The SCM display name.</param>
/// <param name="BinaryPath">The image path the association was verified against.</param>
/// <param name="Confidence">How the association was established.</param>
public sealed record NutServiceTarget(
    string ServiceName,
    string DisplayName,
    string? BinaryPath,
    NutAssociationConfidence Confidence);

public enum NutServiceTargetStatus
{
    /// <summary>Exactly one NUT service was identified and validated.</summary>
    Resolved,

    /// <summary>No service on this machine looks like NUT.</summary>
    NotFound,

    /// <summary>More than one plausible candidate, so none is chosen.</summary>
    Ambiguous,

    /// <summary>A service was found but no longer satisfies the association rules.</summary>
    ValidationFailed,

    /// <summary>The SCM could not be queried.</summary>
    QueryFailed
}

public sealed record NutServiceTargetResolution(
    NutServiceTargetStatus Status,
    NutServiceTarget? Target,
    string? Detail = null,
    IReadOnlyList<string>? Candidates = null)
{
    public bool IsResolved => Status == NutServiceTargetStatus.Resolved && Target is not null;
}

/// <summary>
/// The local service-control primitives, kept behind an interface so every rule above them can be
/// tested without a Windows service to break. Implementations talk to the local SCM and nothing else:
/// no process termination, no configuration change, no installation.
/// </summary>
public interface INutServiceController
{
    Task<NutAgentServiceStatus> GetStatusAsync(NutServiceTarget target, CancellationToken cancellationToken);

    Task<NutServiceControlOutcome> StartAsync(NutServiceTarget target, TimeSpan timeout, CancellationToken cancellationToken);

    Task<NutServiceControlOutcome> StopAsync(NutServiceTarget target, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>One SCM call and what came of it. Failure is a value, never an escaping exception.</summary>
public sealed record NutServiceControlOutcome(
    NutAgentResultCode Code,
    NutServiceState FinalState,
    int? Win32ErrorCode = null,
    string? Detail = null);

/// <summary>
/// Reads the serial devices the operating system already publishes on the agent's machine.
///
/// One verb and no arguments, and that is the security property rather than a convenience. There is
/// no port to name, no speed to set and no payload to send, so no request reaching this interface can
/// make the agent open a device — the port a running NUT driver is talking on is untouched by
/// definition, not by a check somebody has to remember to write.
///
/// Implementations enumerate and nothing more. Failure is a value on the snapshot rather than an
/// escaping exception: a machine that could not be asked has to be reported as not asked, because
/// reporting it as an empty list would tell an operator their adapter had disappeared.
/// </summary>
public interface INutAgentHardwareInspector
{
    Task<NutAgentHardwareSnapshot> InspectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Where control operations are recorded.
///
/// <see cref="IsReadyAsync"/> exists so the agent can refuse a mutation it would not be able to
/// account for. Acting first and discovering afterwards that nothing was written is the failure mode
/// this interface is shaped to prevent: privileged control without a record is worse than no control.
/// </summary>
public interface INutAgentAuditSink
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);

    Task<bool> WriteAsync(NutAgentAuditEntry entry, CancellationToken cancellationToken);
}

public enum NutAgentAuditKind
{
    SecurityStartupFailure,
    UnauthorizedAttempt,
    OperationRequested,
    OperationSucceeded,
    OperationFailed,
    TargetRevalidationFailure
}

/// <summary>
/// One audit record. Every field here is either an identity, a state or an outcome — there is no
/// field a secret could travel in, which is the point rather than an accident.
/// </summary>
public sealed record NutAgentAuditEntry(
    NutAgentAuditKind Kind,
    DateTimeOffset Timestamp,
    Guid OperationId,
    string CallerIdentity,
    string Transport,
    string MachineName,
    NutAgentOperation Operation,
    string? ServiceName,
    NutServiceState InitialState,
    NutServiceState FinalState,
    NutAgentResultCode Code,
    int? Win32ErrorCode,
    TimeSpan Duration,
    string? Detail);

/// <summary>
/// Decides whether a caller may perform control operations, by membership of the operators group.
///
/// Fail-closed is the whole contract: if the group cannot be resolved, or the lookup itself fails,
/// the answer is no. A missing group must never widen into "then let administrators through", which
/// is how a deployment mistake becomes an open door.
/// </summary>
public interface INutAgentAuthorization
{
    /// <summary>Whether the operators group was resolved at startup. False disables all control.</summary>
    bool IsConfigured { get; }

    string? ConfigurationFailure { get; }

    Task<bool> IsAuthorizedAsync(string identity, CancellationToken cancellationToken);
}
