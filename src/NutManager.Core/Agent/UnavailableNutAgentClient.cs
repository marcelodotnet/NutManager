namespace NutManager.Core.Agent;

/// <summary>
/// The client for a transport this build cannot provide.
///
/// It exists because a profile can select a transport the application does not implement — HTTPS is
/// persisted and validated before its listener exists — and the two dishonest answers were both
/// worse. Silently using the named pipe instead would mean an operator who configured HTTPS is told
/// their agent is reachable over a transport they did not choose, and it is exactly the kind of
/// quiet fallback this task forbids. Reporting the server as unreachable would blame the server for
/// a decision made here.
///
/// So it refuses, says which transport is missing, and never touches the network.
/// </summary>
public sealed class UnavailableNutAgentClient : INutManagerAgentClient
{
    private readonly string _detail;

    public UnavailableNutAgentClient(string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        _detail = detail;
    }

    public Task<NutAgentClientResult<NutAgentHandshake>> HandshakeAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult(Refuse<NutAgentHandshake>());

    public Task<NutAgentClientResult<NutAgentServiceStatus>> GetStatusAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult(Refuse<NutAgentServiceStatus>());

    public Task<NutAgentClientResult<NutAgentHardwareSnapshot>> GetHardwareSnapshotAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult(Refuse<NutAgentHardwareSnapshot>());

    public Task<NutAgentClientResult<NutAgentOperationResult>> StartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult(Refuse<NutAgentOperationResult>());

    public Task<NutAgentClientResult<NutAgentOperationResult>> StopAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult(Refuse<NutAgentOperationResult>());

    public Task<NutAgentClientResult<NutAgentOperationResult>> RestartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult(Refuse<NutAgentOperationResult>());

    private NutAgentClientResult<T> Refuse<T>() where T : class =>
        NutAgentClientResult<T>.Failure(NutAgentClientStatus.Failed, _detail);
}
