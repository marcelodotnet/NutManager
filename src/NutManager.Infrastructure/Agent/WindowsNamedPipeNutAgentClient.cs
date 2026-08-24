using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using NutManager.Core.Agent;
using NutManager.Infrastructure.Platform.Windows;

namespace NutManager.Infrastructure.Agent;

/// <summary>
/// Reaches the agent over its named pipe, locally or across the network.
///
/// A remote named pipe is carried by SMB and authenticated by Windows, which means the same thing it
/// meant in T34: the caller needs an identity the server recognises. A machine that is not joined to
/// the server's domain has none by default, and the honest report for that is <see
/// cref="NutAgentClientStatus.AccessDenied"/> — an administrative fact about authentication, never a
/// statement that NUT is unavailable. The failure mapping below exists to keep those apart.
///
/// One exchange per connection. Requests are small and infrequent, a pipe connection is cheap, and a
/// connection held open between them is a connection that has to be revalidated, kept alive and
/// recovered — complexity bought for no benefit a UPS console can feel.
/// </summary>
public sealed class WindowsNamedPipeNutAgentClient : INutManagerAgentClient
{
    // Windows error numbers, not Windows APIs: they stay on the platform-neutral side so the mapping
    // that reads them compiles without a platform guard.
    public const int ErrorFileNotFound = 2;
    public const int ErrorAccessDenied = 5;
    public const int ErrorBadNetworkPath = 53;
    public const int ErrorNetworkUnreachable = 1231;
    public const int ErrorLogonFailure = 1326;

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public WindowsNamedPipeNutAgentClient(TimeSpan? connectTimeout = null)
        : this(NutAgentNamedPipe.PipeName, connectTimeout)
    {
    }

    /// <summary>
    /// The pipe name is settable so the protocol can be exercised against a loopback server in a
    /// test. It is a client-side detail: no name a caller supplies here can widen what the agent on
    /// the other end is willing to do.
    /// </summary>
    public WindowsNamedPipeNutAgentClient(string pipeName, TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
    }

    public Task<NutAgentClientResult<NutAgentHandshake>> HandshakeAsync(string host, CancellationToken cancellationToken) =>
        ExchangeAsync(host, NutAgentOperation.Handshake, Guid.NewGuid(), response => response.Handshake, cancellationToken);

    public Task<NutAgentClientResult<NutAgentServiceStatus>> GetStatusAsync(string host, CancellationToken cancellationToken) =>
        ExchangeAsync(host, NutAgentOperation.GetStatus, Guid.NewGuid(), response => response.Status, cancellationToken);

    public Task<NutAgentClientResult<NutAgentHardwareSnapshot>> GetHardwareSnapshotAsync(string host, CancellationToken cancellationToken) =>
        ExchangeAsync(host, NutAgentOperation.GetHardwareSnapshot, Guid.NewGuid(), response => response.Hardware, cancellationToken);

    public Task<NutAgentClientResult<NutAgentOperationResult>> StartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
        ExchangeAsync(host, NutAgentOperation.Start, operationId, response => response.Result, cancellationToken);

    public Task<NutAgentClientResult<NutAgentOperationResult>> StopAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
        ExchangeAsync(host, NutAgentOperation.Stop, operationId, response => response.Result, cancellationToken);

    public Task<NutAgentClientResult<NutAgentOperationResult>> RestartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
        ExchangeAsync(host, NutAgentOperation.Restart, operationId, response => response.Result, cancellationToken);

    private Task<NutAgentClientResult<T>> ExchangeAsync<T>(
        string host,
        NutAgentOperation operation,
        Guid operationId,
        Func<NutAgentResponse, T?> select,
        CancellationToken cancellationToken)
        where T : class
    {
        var target = WindowsRemoteNutServiceProbe.NormalizeHost(host);
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(target))
        {
            return Task.FromResult(NutAgentClientResult<T>.Failure(
                NutAgentClientStatus.Failed, "The agent transport requires Windows and a host name."));
        }

        return WindowsAgentPipeExchange.RunAsync(target, _pipeName, _connectTimeout, operation, operationId, select, cancellationToken);
    }

    /// <summary>
    /// Maps a connection failure by its numeric code rather than its message, which is localized on
    /// the machine running NutManager and would otherwise make the mapping depend on the operator's
    /// language.
    /// </summary>
    public static NutAgentClientStatus MapFailure(Exception exception, out int? win32ErrorCode)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is TimeoutException)
        {
            win32ErrorCode = null;
            return NutAgentClientStatus.TimedOut;
        }

        var win32 = exception as Win32Exception ?? exception.InnerException as Win32Exception;
        win32ErrorCode = win32?.NativeErrorCode;

        return win32?.NativeErrorCode switch
        {
            // The pipe is not there: the agent is not installed on that machine, or not running.
            ErrorFileNotFound => NutAgentClientStatus.AgentUnavailable,
            ErrorAccessDenied or ErrorLogonFailure => NutAgentClientStatus.AccessDenied,
            ErrorBadNetworkPath or ErrorNetworkUnreachable => NutAgentClientStatus.HostUnreachable,
            _ => exception switch
            {
                // The managed layer reports a missing pipe this way before Win32 is consulted.
                FileNotFoundException => NutAgentClientStatus.AgentUnavailable,
                UnauthorizedAccessException => NutAgentClientStatus.AccessDenied,
                IOException => NutAgentClientStatus.HostUnreachable,
                _ => NutAgentClientStatus.Failed
            }
        };
    }
}

/// <summary>The Windows-typed half of the client, behind one annotation.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAgentPipeExchange
{
    internal static Task<NutAgentClientResult<T>> RunAsync<T>(
        string host,
        string pipeName,
        TimeSpan connectTimeout,
        NutAgentOperation operation,
        Guid operationId,
        Func<NutAgentResponse, T?> select,
        CancellationToken cancellationToken)
        where T : class =>
        Task.Run(() => ExchangeAsync(host, pipeName, connectTimeout, operation, operationId, select, cancellationToken), cancellationToken);

    private static async Task<NutAgentClientResult<T>> ExchangeAsync<T>(
        string host,
        string pipeName,
        TimeSpan connectTimeout,
        NutAgentOperation operation,
        Guid operationId,
        Func<NutAgentResponse, T?> select,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            // Impersonation, because the agent asks Windows whether the connected token belongs to
            // the operators group, and that question cannot be put to an identification-level token.
            using var pipe = new NamedPipeClientStream(
                host, pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);

            try
            {
                await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Connecting to a named pipe waits for an instance to become available, so a host
                // with no agent at all and a host whose agent never accepted look identical from
                // here. Both are "nothing accepted a connection", and reporting that as a timeout
                // would suggest a hung agent where there may be no agent installed.
                return NutAgentClientResult<T>.Failure(
                    NutAgentClientStatus.AgentUnavailable, "No agent accepted a connection on this host.");
            }

            var payload = NutAgentWireCodec.Serialize(NutAgentRequest.For(operation, operationId));
            await NutAgentFraming.WriteFrameAsync(pipe, payload, NutAgentFraming.MaxRequestBytes, cancellationToken).ConfigureAwait(false);

            var frame = await NutAgentFraming.ReadFrameAsync(pipe, NutAgentFraming.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            if (frame.Status != NutAgentFrameStatus.Success)
            {
                return NutAgentClientResult<T>.Failure(
                    frame.Status == NutAgentFrameStatus.Closed ? NutAgentClientStatus.AgentUnavailable : NutAgentClientStatus.ProtocolFailure,
                    frame.Status.ToString());
            }

            if (!NutAgentWireCodec.TryReadResponse(frame.Payload, out var response, out var failure))
            {
                return NutAgentClientResult<T>.Failure(NutAgentClientStatus.ProtocolFailure, failure.ToString(), code: failure);
            }

            var value = select(response!);

            // A refused operation still arrives as a payload — the agent's result record carries the
            // Unauthorized or Busy verdict. A payload that is missing altogether means the agent
            // rejected the request before dispatching it, which is a protocol answer, and its code
            // travels with it so the reason survives to the screen.
            return value is null
                ? NutAgentClientResult<T>.Failure(NutAgentClientStatus.ProtocolFailure, response!.Detail, code: response.Code)
                : NutAgentClientResult<T>.Ok(value, response!.Code);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NutAgentClientResult<T>.Failure(NutAgentClientStatus.TimedOut, "The agent did not answer in time.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var status = WindowsNamedPipeNutAgentClient.MapFailure(exception, out var code);
            return NutAgentClientResult<T>.Failure(status, exception.GetType().Name, code);
        }
    }
}
