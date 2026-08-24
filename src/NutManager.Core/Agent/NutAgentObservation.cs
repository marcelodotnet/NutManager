namespace NutManager.Core.Agent;

/// <summary>
/// One reading of an agent: whether it answered, what it says it can do, and what it reports about
/// the Windows service.
///
/// Three facts, kept apart on purpose. The agent's reachability, the Windows service's state and
/// NUT's own protocol health are independent, and this record holds only the first two — nothing here
/// describes upsd, because the agent has no opinion about it. An agent that cannot be reached on a
/// server whose NUT is answering normally is an administrative gap, and collapsing the two is exactly
/// the mistake T34 was built to avoid.
/// </summary>
public sealed record NutAgentObservation(
    string Host,
    NutAgentClientStatus AgentStatus,
    NutAgentHandshake? Handshake,
    NutAgentServiceStatus? Service,
    NutAgentResultCode? Code,
    int? Win32ErrorCode,
    string? Detail,
    DateTimeOffset ObservedAt)
{
    /// <summary>The agent answered. It says nothing about the service or about NUT.</summary>
    public bool AgentReachable => AgentStatus == NutAgentClientStatus.Success;

    public bool HasService => Service is not null;

    /// <summary>
    /// Whether the agent is currently willing to control anything. False when the operators group,
    /// the audit sink or the NUT target is missing, and the reason travels in the handshake.
    /// </summary>
    public bool ControlAvailable => AgentReachable && Handshake is { ControlAvailable: true };

    /// <summary>
    /// Whether the agent advertised this operation at all, independent of whether it can control
    /// anything.
    ///
    /// This is the check a read-only operation has to use. An agent whose operators group resolved
    /// but whose NUT service could not be pinned reports control as unavailable and still enumerates
    /// serial devices perfectly well — asking <see cref="Supports"/> about it would hide a capability
    /// the agent is plainly offering.
    /// </summary>
    public bool Advertises(NutAgentOperation operation) =>
        AgentReachable && Handshake is not null && Handshake.Capabilities.Contains(operation);

    /// <summary>
    /// Whether the agent advertised this control operation. Capabilities are read from the handshake
    /// rather than inferred from a version number: an agent whose audit sink is unusable runs a
    /// perfectly current build and still cannot start anything.
    /// </summary>
    public bool Supports(NutAgentOperation operation) => ControlAvailable && Advertises(operation);

    /// <summary>The agent could not be reached, or reached and refused. Never a statement about NUT.</summary>
    public static NutAgentObservation Unreachable(
        string host,
        NutAgentClientStatus status,
        DateTimeOffset observedAt,
        NutAgentResultCode? code = null,
        int? win32ErrorCode = null,
        string? detail = null) =>
        new(host, status, null, null, code, win32ErrorCode, detail, observedAt);

    /// <summary>
    /// Builds the reading from the two exchanges the monitor performs. Pure, so what the panel will
    /// show for any combination of answers can be asserted without a pipe.
    /// </summary>
    public static NutAgentObservation From(
        string host,
        NutAgentClientResult<NutAgentHandshake> handshake,
        NutAgentClientResult<NutAgentServiceStatus>? status,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(handshake);

        if (!handshake.Succeeded)
        {
            return Unreachable(host, handshake.Status, observedAt, handshake.Code, handshake.Win32ErrorCode, handshake.Detail);
        }

        if (status is null)
        {
            return new NutAgentObservation(
                host, NutAgentClientStatus.Success, handshake.Value, null, handshake.Code, null, null, observedAt);
        }

        // The handshake succeeded and the status did not. The agent is still reachable — losing that
        // distinction would report a readable agent as an absent one.
        return status.Succeeded
            ? new NutAgentObservation(
                host, NutAgentClientStatus.Success, handshake.Value, status.Value, status.Code, null, null, observedAt)
            : new NutAgentObservation(
                host, status.Status, handshake.Value, null, status.Code, status.Win32ErrorCode, status.Detail, observedAt);
    }
}
