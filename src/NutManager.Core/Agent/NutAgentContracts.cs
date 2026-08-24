namespace NutManager.Core.Agent;

/// <summary>
/// The operations the agent exposes. This is the complete list, and it is an enum rather than a
/// string so that "the client asked for something we do not implement" is a parse failure instead of
/// a lookup that might one day find something.
///
/// Note what is absent: nothing here names a service, a path, or a command. The agent controls the
/// NUT service it validated for itself at startup, and there is no field through which a caller could
/// redirect that. A generic <c>StartService(string)</c> would make this a remote executor wearing a
/// UPS monitor's name.
/// </summary>
public enum NutAgentOperation
{
    Handshake,
    GetStatus,
    Start,
    Stop,
    Restart,

    /// <summary>
    /// Reports the serial devices Windows already knows about on the agent's own machine. Appended
    /// rather than inserted, so an agent built before it existed keeps the numbering it was compiled
    /// against.
    ///
    /// Passive in the strongest sense the word has here: it reads what the operating system has
    /// already published and opens nothing. There is no port to name in the request, so there is no
    /// field through which a caller could make the agent touch a device — which is what keeps this
    /// from becoming a way to interrupt a running driver's conversation with a UPS.
    /// </summary>
    GetHardwareSnapshot
}

/// <summary>Outcome of an agent request. Errors are values here, never exceptions on the wire.</summary>
public enum NutAgentResultCode
{
    Success,

    /// <summary>The service was already in the state the operation would have produced.</summary>
    AlreadyInRequestedState,

    /// <summary>The caller authenticated but is not a member of the operators group.</summary>
    Unauthorized,

    /// <summary>The agent could not identify its NUT service, so it holds no authority to act.</summary>
    TargetUnavailable,

    /// <summary>The target stopped matching the NUT service between startup and this request.</summary>
    TargetRevalidationFailed,

    /// <summary>Mutations are refused because the mandatory audit sink is not usable.</summary>
    AuditUnavailable,

    /// <summary>Another mutation holds the exclusive gate.</summary>
    Busy,

    /// <summary>The service did not reach the expected state within its budget.</summary>
    TimedOut,

    /// <summary>The service control call itself failed.</summary>
    ServiceControlFailed,

    /// <summary>The request was well-formed but the protocol version is not supported.</summary>
    IncompatibleProtocol,

    /// <summary>The request could not be understood.</summary>
    MalformedRequest,

    /// <summary>The operation ran but the result could not be written to the audit log.</summary>
    CompletedWithAuditFailure,

    Failed
}

/// <summary>Which half of a restart a per-phase outcome describes.</summary>
public enum NutAgentRestartPhase
{
    None,
    Stop,
    Start
}

/// <summary>
/// Who is asking, as established by the transport. The transport authenticates — Windows does that
/// work, through the pipe ACL or through Negotiate — and hands the answer here. The application
/// service still enforces the decision, so authorization is applied in one place no matter which
/// door the request came through.
/// </summary>
/// <param name="Identity">The caller's Windows account, for the audit record.</param>
/// <param name="IsAuthorized">Whether the caller belongs to the operators group.</param>
/// <param name="Transport">Which listener accepted the request, for the audit record.</param>
public sealed record NutAgentCallerContext(string Identity, bool IsAuthorized, string Transport)
{
    public static NutAgentCallerContext Denied(string identity, string transport) =>
        new(identity, false, transport);
}

/// <summary>What the agent is and what it can do, exchanged before anything else happens.</summary>
/// <param name="ProtocolVersion">The wire contract the agent speaks.</param>
/// <param name="AgentVersion">The agent build, for diagnosis.</param>
/// <param name="MachineName">Taken from the agent's own machine, never from the client.</param>
/// <param name="Capabilities">Operations this agent will actually perform right now.</param>
/// <param name="ControlAvailable">False when the operators group or the audit sink is missing.</param>
/// <param name="ControlUnavailableReason">Why control is off, so the client can say so precisely.</param>
public sealed record NutAgentHandshake(
    int ProtocolVersion,
    string AgentVersion,
    string MachineName,
    IReadOnlyList<NutAgentOperation> Capabilities,
    bool ControlAvailable,
    string? ControlUnavailableReason);

/// <summary>
/// What the agent knows about its NUT service right now.
///
/// Deliberately shaped like T34's remote snapshot: the service state, the process and the identity are
/// the same facts, whether they were read across the network or locally. Nothing here reports the NUT
/// protocol, because the agent has no opinion about it — a stopped service and an unreachable upsd are
/// different findings and the product keeps them apart.
/// </summary>
public sealed record NutAgentServiceStatus(
    string MachineName,
    string? ServiceName,
    string? DisplayName,
    Administration.NutServiceState ServiceState,
    int? ProcessId,
    string? ExecutableName,
    bool TargetValidated,
    DateTimeOffset ObservedAt)
{
    public bool HasProcess => ProcessId is > 0;

    public static NutAgentServiceStatus Unavailable(string machineName, DateTimeOffset observedAt) =>
        new(machineName, null, null, Administration.NutServiceState.Unknown, null, null, false, observedAt);
}

/// <summary>
/// The result of one control operation.
///
/// <paramref name="FinalState"/> is not decoration. A restart whose stop worked and whose start failed
/// leaves the service down, and reporting only "restart failed" would hide the single most important
/// fact an operator needs at that moment. So the phase outcomes and the final state travel with the
/// verdict, always.
/// </summary>
public sealed record NutAgentOperationResult(
    Guid OperationId,
    NutAgentOperation Operation,
    NutAgentResultCode Code,
    Administration.NutServiceState InitialState,
    Administration.NutServiceState FinalState,
    NutAgentRestartPhase FailedPhase,
    NutAgentResultCode? StopPhase,
    NutAgentResultCode? StartPhase,
    int? Win32ErrorCode,
    TimeSpan Duration,
    string? Detail)
{
    public bool Succeeded => Code is NutAgentResultCode.Success
        or NutAgentResultCode.AlreadyInRequestedState
        or NutAgentResultCode.CompletedWithAuditFailure;

    public static NutAgentOperationResult Refused(
        Guid operationId,
        NutAgentOperation operation,
        NutAgentResultCode code,
        string? detail = null) =>
        new(operationId, operation, code, Administration.NutServiceState.Unknown,
            Administration.NutServiceState.Unknown, NutAgentRestartPhase.None, null, null, null,
            TimeSpan.Zero, detail);
}

/// <summary>
/// What the agent's own machine reports about its serial devices, at one instant.
///
/// It carries <see cref="Administration.NutComPortInfo"/> rather than a parallel wire type, because
/// the fields are the same fields: the local path already models a Windows serial device this way,
/// and a second record differing only by namespace would guarantee the two descriptions eventually
/// drift. The record is platform-neutral and holds no secret — a port name, a friendly name, a
/// manufacturer, a PnP identifier and a status code, all of them already public on the machine.
///
/// Nothing here describes NUT. A COM port that exists is not a UPS that answers, and this snapshot is
/// deliberately incapable of claiming otherwise.
/// </summary>
/// <param name="MachineName">Taken from the agent's own machine, never from the caller.</param>
/// <param name="ComPorts">What the operating system currently exposes. Empty is a valid answer.</param>
/// <param name="EnumerationSucceeded">
/// False when the machine could not be asked at all. It is kept apart from an empty list on purpose:
/// "there are no serial ports" and "we could not find out" are different findings, and reporting the
/// second as the first would tell an operator their adapter had vanished.
/// </param>
/// <param name="Detail">Why enumeration failed, or a note about a snapshot that had to be capped.</param>
public sealed record NutAgentHardwareSnapshot(
    string MachineName,
    IReadOnlyList<Administration.NutComPortInfo> ComPorts,
    bool EnumerationSucceeded,
    string? Detail,
    DateTimeOffset ObservedAt)
{
    public bool HasPorts => ComPorts.Count > 0;

    public static NutAgentHardwareSnapshot Unavailable(string machineName, DateTimeOffset observedAt, string? detail = null) =>
        new(machineName, Array.Empty<Administration.NutComPortInfo>(), false, detail, observedAt);
}

/// <summary>Bounds for the hardware snapshot, so one machine cannot produce a frame the wire refuses.</summary>
public static class NutAgentHardware
{
    /// <summary>
    /// Far more serial ports than a UPS server has, and still small enough that the serialized
    /// snapshot stays well inside the response ceiling both transports enforce. A machine with more
    /// is reported truncated rather than as a protocol failure, because a frame the client rejects
    /// for being too large tells an operator nothing about their hardware.
    /// </summary>
    public const int MaxReportedPorts = 64;
}
