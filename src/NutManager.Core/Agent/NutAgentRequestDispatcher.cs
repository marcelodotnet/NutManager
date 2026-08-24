namespace NutManager.Core.Agent;

/// <summary>
/// Maps a decoded request onto the application service.
///
/// This is the whole of what a transport has to do once it has authenticated its caller, and it lives
/// here rather than in the pipe server so that the HTTPS listener cannot grow a second, subtly
/// different opinion about which operation means what. A transport reads a frame, decodes it, calls
/// this, and writes the answer back; it makes no security decision of its own beyond establishing who
/// is asking.
/// </summary>
public sealed class NutAgentRequestDispatcher
{
    private readonly NutAgentApplicationService _service;

    public NutAgentRequestDispatcher(NutAgentApplicationService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public async Task<NutAgentResponse> DispatchAsync(
        NutAgentRequest request,
        NutAgentCallerContext caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        switch (request.Operation)
        {
            case NutAgentOperation.Handshake:
                return new NutAgentResponse(
                    NutAgentOptions.ProtocolVersion,
                    NutAgentResultCode.Success,
                    Handshake: await _service.HandshakeAsync(cancellationToken).ConfigureAwait(false));

            case NutAgentOperation.GetStatus:
                // Not gated behind the operators group: reaching this transport already required
                // membership, and a status read changes nothing.
                return new NutAgentResponse(
                    NutAgentOptions.ProtocolVersion,
                    NutAgentResultCode.Success,
                    Status: await _service.GetStatusAsync(cancellationToken).ConfigureAwait(false));

            case NutAgentOperation.GetHardwareSnapshot:
                // Same gate as GetStatus, and for the same two reasons: the transport already
                // established that the caller is an operator on that machine, and enumerating devices
                // Windows has already published changes nothing. Adding it here rather than in either
                // listener is what keeps the pipe and HTTPS answering identically.
                return new NutAgentResponse(
                    NutAgentOptions.ProtocolVersion,
                    NutAgentResultCode.Success,
                    Hardware: await _service.GetHardwareSnapshotAsync(cancellationToken).ConfigureAwait(false));

            case NutAgentOperation.Start:
                return Completed(await _service.StartAsync(request.OperationId, caller, cancellationToken).ConfigureAwait(false));

            case NutAgentOperation.Stop:
                return Completed(await _service.StopAsync(request.OperationId, caller, cancellationToken).ConfigureAwait(false));

            case NutAgentOperation.Restart:
                return Completed(await _service.RestartAsync(request.OperationId, caller, cancellationToken).ConfigureAwait(false));

            default:
                return NutAgentResponse.Refused(NutAgentResultCode.MalformedRequest, "Unsupported operation.");
        }
    }

    private static NutAgentResponse Completed(NutAgentOperationResult result) =>
        new(NutAgentOptions.ProtocolVersion, result.Code, Result: result, Detail: result.Detail);
}
