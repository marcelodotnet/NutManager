using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using NutManager.Core.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// Opens a connection to the agent's HTTPS endpoint, and closes it.
///
/// The smallest question that answers the one being asked: is anything accepting connections where
/// the agent says it listens. A completed TCP connect proves the prefix is open; a refusal, a reset
/// or a silence proves it is not. Whether what answered is <em>ours</em> is a different question, and
/// it is already answered beside this row by the binding, the reservation and the firewall, each of
/// which is classified by ownership.
///
/// Deliberately stops short of the TLS handshake. Completing one would mean this window validating a
/// certificate chain every second, on the UI's behalf, against a name it does not control - and a
/// listener that is up with an expired certificate would then report as down, which is a different
/// fault reported in the wrong row. The certificate has its own row and its own rules.
///
/// The addresses of the host are attempted together rather than in turn, and the first one to answer
/// settles it. That is not an optimisation: an agent host normally resolves to both an IPv6 and an
/// IPv4 address, and a connection to a loopback or link-local IPv6 address with nothing behind it was
/// measured on Windows to hang for the whole budget rather than being refused. Attempting them in
/// order therefore spends the entire timeout on the first address and never reaches the one that
/// works, which reports a healthy listener as unreachable - the false negative this row exists to
/// avoid producing.
///
/// The timeout is short on purpose. This runs on a one-second cadence, so a probe that could wait as
/// long as the named-pipe client does would spend most of its life waiting; and it is long enough
/// that a listener which is genuinely up is never reported down, since a connect that succeeds on
/// loopback or a local network completes in single-digit milliseconds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentHttpsListenerProbe : IAgentHttpsListenerProbe
{
    /// <summary>Well under the polling period, and far above what a live endpoint needs.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMilliseconds(1500);

    private readonly TimeSpan _timeout;

    public WindowsAgentHttpsListenerProbe(TimeSpan? timeout = null) => _timeout = timeout ?? DefaultTimeout;

    public async Task<AgentListenerObservation> ProbeAsync(
        AgentHttpsBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);

        try
        {
            // A literal address is returned as itself, so this costs a lookup only for a name.
            var addresses = await Dns.GetHostAddressesAsync(binding.Host, budget.Token).ConfigureAwait(false);

            if (addresses.Length == 0)
            {
                return AgentListenerObservation.Unreachable(
                    $"{binding.Host} did not resolve to an address.");
            }

            return await RaceAsync(addresses, binding.Port, budget.Token).ConfigureAwait(false)
                ?? Answer(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The window is closing, or a newer observation superseded this one. That is not a verdict
            // about the endpoint, so it is propagated rather than recorded as a failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            return NoAnswer();
        }
        catch (SocketException exception)
        {
            // A name that does not resolve arrives here, and it is a real reason the endpoint cannot
            // be reached rather than an internal fault.
            return AgentListenerObservation.Unreachable(Describe(exception));
        }
        catch (Exception exception)
        {
            return AgentListenerObservation.Unreachable($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Connects to every address at once and settles on the first that answers.
    ///
    /// Returns the observation when one is decided, or null when every attempt was still outstanding
    /// when the budget ran out - which the caller turns into either a timeout or a cancellation,
    /// because those are not the same event and must not report the same thing.
    /// </summary>
    private static async Task<AgentListenerObservation?> RaceAsync(
        IPAddress[] addresses, int port, CancellationToken budget)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(budget);

        var pending = addresses
            .Select(address => TryConnectAsync(address, port, race.Token))
            .ToList();

        Exception? lastFailure = null;

        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);

                var failure = await finished.ConfigureAwait(false);

                if (failure is null) return AgentListenerObservation.Listening;

                lastFailure = failure;
            }
        }
        finally
        {
            // Whatever is still trying is abandoned here, and awaited so that nothing is left running
            // past the answer and nothing carries an exception nobody looked at.
            await race.CancelAsync().ConfigureAwait(false);
            foreach (var attempt in pending) await attempt.ConfigureAwait(false);
        }

        return lastFailure switch
        {
            null => null,
            OperationCanceledException => null,
            SocketException socket => AgentListenerObservation.Unreachable(Describe(socket)),
            var other => AgentListenerObservation.Unreachable($"{other.GetType().Name}: {other.Message}"),
        };
    }

    /// <summary>The failure, or null when the connection was accepted. Never throws.</summary>
    private static async Task<Exception?> TryConnectAsync(
        IPAddress address, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = new TcpClient(address.AddressFamily);
            await connection.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// What an exhausted budget means, which depends on who exhausted it.
    ///
    /// Cancelled by the caller is not an answer at all and is thrown; cancelled by the timeout is the
    /// endpoint declining to answer, which is one.
    /// </summary>
    private AgentListenerObservation Answer(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return NoAnswer();
    }

    private AgentListenerObservation NoAnswer() =>
        AgentListenerObservation.Unreachable($"No answer within {_timeout.TotalMilliseconds:F0} ms.");

    /// <summary>
    /// The socket error is the useful part: refused and unresolvable are both "not listening" on the
    /// card and two very different places to go next in the tooltip.
    /// </summary>
    private static string Describe(SocketException exception) =>
        $"{exception.SocketErrorCode}: {exception.Message}";
}
