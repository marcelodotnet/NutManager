using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using NutManager.Core.Agent;

namespace NutManager.Infrastructure.Agent;

/// <summary>
/// Reaches the agent over HTTPS, authenticated by Negotiate.
///
/// This is the transport that exists for the case the named pipe cannot serve. A pipe reached across
/// machines rides SMB and therefore needs a Windows session the client may not have; Negotiate over
/// HTTPS can be handed an explicit credential instead, which is what makes a non-domain client
/// usable against a domain server without anyone establishing a session outside the product.
///
/// The credential is given to the handler and never to the protocol. Nothing about the account
/// travels in the request body — the agent's contract has no field for it — so the password is known
/// only to the handler and to Windows, and a captured request contains no secret at all.
///
/// Certificate validation is the platform default, deliberately. There is no callback here, and a
/// test refuses this file if one ever appears: a transport that controls a service is the last place
/// to accept a certificate nobody checked.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHttpsNutAgentClient : INutManagerAgentClient, IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private bool _disposed;

    /// <summary>Uses the account NutManager already runs as. No secret is held or prompted for.</summary>
    public WindowsHttpsNutAgentClient(string endpoint, TimeSpan? timeout = null)
        : this(endpoint, CredentialCache.DefaultNetworkCredentials, timeout)
    {
    }

    /// <summary>
    /// Uses an explicit Windows account. The credential reaches Negotiate through the handler, which
    /// is the supported route: no LogonUser, no process-wide impersonation, and nothing that would
    /// change the identity of anything else this application is doing.
    /// </summary>
    public WindowsHttpsNutAgentClient(string endpoint, NetworkCredential credential, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        _endpoint = BuildEndpoint(endpoint);

        var handler = new HttpClientHandler
        {
            Credentials = credential,
            PreAuthenticate = true,
            AllowAutoRedirect = false,
            UseProxy = false
        };

        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// The agent's single route, built from the configured origin. The path is fixed here rather
    /// than taken from configuration so a profile cannot point the client at another endpoint on
    /// the same host.
    /// </summary>
    public static Uri BuildEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The agent endpoint must be an absolute https URI.", nameof(endpoint));
        }

        return new Uri(new Uri(parsed.GetLeftPart(UriPartial.Authority)), NutAgentHttpsProtocol.Path);
    }

    public Task<NutAgentClientResult<NutAgentHandshake>> HandshakeAsync(string host, CancellationToken cancellationToken) =>
        ExchangeAsync(NutAgentOperation.Handshake, Guid.NewGuid(), response => response.Handshake, cancellationToken);

    public Task<NutAgentClientResult<NutAgentServiceStatus>> GetStatusAsync(string host, CancellationToken cancellationToken) =>
        ExchangeAsync(NutAgentOperation.GetStatus, Guid.NewGuid(), response => response.Status, cancellationToken);

    public Task<NutAgentClientResult<NutAgentHardwareSnapshot>> GetHardwareSnapshotAsync(string host, CancellationToken cancellationToken) =>
        ExchangeAsync(NutAgentOperation.GetHardwareSnapshot, Guid.NewGuid(), response => response.Hardware, cancellationToken);

    public Task<NutAgentClientResult<NutAgentOperationResult>> StartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
        ExchangeAsync(NutAgentOperation.Start, operationId, response => response.Result, cancellationToken);

    public Task<NutAgentClientResult<NutAgentOperationResult>> StopAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
        ExchangeAsync(NutAgentOperation.Stop, operationId, response => response.Result, cancellationToken);

    public Task<NutAgentClientResult<NutAgentOperationResult>> RestartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
        ExchangeAsync(NutAgentOperation.Restart, operationId, response => response.Result, cancellationToken);

    private async Task<NutAgentClientResult<T>> ExchangeAsync<T>(
        NutAgentOperation operation,
        Guid operationId,
        Func<NutAgentResponse, T?> select,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var content = new ByteArrayContent(NutAgentWireCodec.Serialize(NutAgentRequest.For(operation, operationId)));
            content.Headers.ContentType = new MediaTypeHeaderValue(NutAgentHttpsProtocol.ContentType) { CharSet = "utf-8" };

            using var response = await _client.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Windows authenticated the channel and the agent refused the account, or Negotiate
                // itself failed. Both are authorization facts, never a statement about NUT.
                return NutAgentClientResult<T>.Failure(NutAgentClientStatus.AccessDenied, response.StatusCode.ToString());
            }

            var payload = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                return NutAgentClientResult<T>.Failure(NutAgentClientStatus.ProtocolFailure, "The response exceeded the permitted size.");
            }

            if (!NutAgentWireCodec.TryReadResponse(payload, out var parsed, out var failure))
            {
                return NutAgentClientResult<T>.Failure(NutAgentClientStatus.ProtocolFailure, failure.ToString(), code: failure);
            }

            var value = select(parsed!);
            return value is null
                ? NutAgentClientResult<T>.Failure(NutAgentClientStatus.ProtocolFailure, parsed!.Detail, code: parsed.Code)
                : NutAgentClientResult<T>.Ok(value, parsed!.Code);
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
            var status = MapFailure(exception, out var code);
            return NutAgentClientResult<T>.Failure(status, exception.GetType().Name, code);
        }
    }

    /// <summary>
    /// Maps a failed exchange. A TLS problem is its own answer: reporting it as an unreachable host
    /// would send an operator to look at the network for a certificate that expired.
    /// </summary>
    public static NutAgentClientStatus MapFailure(Exception exception, out int? win32ErrorCode)
    {
        ArgumentNullException.ThrowIfNull(exception);
        win32ErrorCode = null;

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case System.Security.Authentication.AuthenticationException:
                    // Includes a certificate the machine does not trust and a name that does not match.
                    return NutAgentClientStatus.ProtocolFailure;

                // Before Win32Exception, which it derives from: a refused connection is the network
                // being unreachable, not the caller being unauthorized.
                case System.Net.Sockets.SocketException socket:
                    win32ErrorCode = socket.ErrorCode;
                    return NutAgentClientStatus.HostUnreachable;

                case System.ComponentModel.Win32Exception win32:
                    win32ErrorCode = win32.NativeErrorCode;
                    return NutAgentClientStatus.AccessDenied;

                case TimeoutException:
                    return NutAgentClientStatus.TimedOut;
            }
        }

        return exception is HttpRequestException
            ? NutAgentClientStatus.AgentUnavailable
            : NutAgentClientStatus.Failed;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > NutAgentHttpsProtocol.MaxResponseBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[NutAgentHttpsProtocol.MaxResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        return total > NutAgentHttpsProtocol.MaxResponseBytes ? null : buffer[..total];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
