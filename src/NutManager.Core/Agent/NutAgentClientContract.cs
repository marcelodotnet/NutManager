namespace NutManager.Core.Agent;

/// <summary>
/// How a client's attempt to reach the agent ended.
///
/// These are transport outcomes and they are kept apart from <see cref="NutAgentResultCode"/> on
/// purpose: "the agent did not answer" and "the agent answered that you may not do that" are
/// different facts and an operator needs to be told which. Above all, none of them says anything
/// about NUT. An agent that is not installed on a server whose upsd is answering perfectly is an
/// administrative gap, not an outage — the same separation T34 was built around.
/// </summary>
public enum NutAgentClientStatus
{
    Success,

    /// <summary>
    /// Nothing accepted a connection on that machine: no agent installed, not running, or never
    /// answering. Deliberately one state — the transport cannot tell those apart, and inventing a
    /// distinction it cannot support would be worse than naming the one thing it does know.
    /// </summary>
    AgentUnavailable,

    /// <summary>The machine refused the connection or the caller is not an operator there.</summary>
    AccessDenied,

    /// <summary>The host could not be reached at all.</summary>
    HostUnreachable,

    /// <summary>The agent accepted the connection but did not answer in time.</summary>
    TimedOut,

    /// <summary>Something answered, but not something speaking this protocol.</summary>
    ProtocolFailure,

    Failed
}

/// <summary>One exchange with the agent: how it went, and what came back if it went well.</summary>
public sealed record NutAgentClientResult<T>(
    NutAgentClientStatus Status,
    T? Value = default,
    NutAgentResultCode? Code = null,
    int? Win32ErrorCode = null,
    string? Detail = null)
    where T : class
{
    public bool Succeeded => Status == NutAgentClientStatus.Success && Value is not null;

    public static NutAgentClientResult<T> Ok(T value, NutAgentResultCode code) =>
        new(NutAgentClientStatus.Success, value, code);

    public static NutAgentClientResult<T> Failure(
        NutAgentClientStatus status, string? detail = null, int? win32ErrorCode = null, NutAgentResultCode? code = null) =>
        new(status, null, code, win32ErrorCode, detail);
}

/// <summary>
/// Talks to an agent on a named machine.
///
/// Every method names the host, and nothing else: there is no service name, path or command to pass,
/// because the agent decides for itself what it controls. The client cannot widen what the agent
/// will do, which is the same property the wire contract has and for the same reason.
///
/// A caller never sees a frame, a length prefix or a pipe. Those belong to the implementation, so a
/// view model consuming this cannot accidentally acquire an opinion about the transport.
/// </summary>
public interface INutManagerAgentClient
{
    Task<NutAgentClientResult<NutAgentHandshake>> HandshakeAsync(string host, CancellationToken cancellationToken);

    Task<NutAgentClientResult<NutAgentServiceStatus>> GetStatusAsync(string host, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the serial devices the agent's machine already exposes. Read-only and passive on both
    /// sides: the request names no port, so there is nothing here a caller could open.
    /// </summary>
    Task<NutAgentClientResult<NutAgentHardwareSnapshot>> GetHardwareSnapshotAsync(string host, CancellationToken cancellationToken);

    Task<NutAgentClientResult<NutAgentOperationResult>> StartAsync(string host, Guid operationId, CancellationToken cancellationToken);

    Task<NutAgentClientResult<NutAgentOperationResult>> StopAsync(string host, Guid operationId, CancellationToken cancellationToken);

    Task<NutAgentClientResult<NutAgentOperationResult>> RestartAsync(string host, Guid operationId, CancellationToken cancellationToken);
}
