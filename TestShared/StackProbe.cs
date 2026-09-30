// Linked into Odyssey.E2ETests and Odyssey.E2ETests.Api — the two fixtures that probe an already-running
// stack over HTTP. Internal: each test assembly compiles its own copy.

using System.Net.Http;
using System.Net.Sockets;

namespace Odyssey.Testing;

/// <summary>What one readiness probe made of one answer from the stack.</summary>
internal enum ProbeStep
{
    /// <summary>The stack is ready.</summary>
    Ready,

    /// <summary>The stack answered but is not ready yet — keep polling until the deadline.</summary>
    Retry,

    /// <summary>The stack answered with something that can never become ready — stop and fail now.</summary>
    Fatal,
}

internal sealed record ProbeOutcome(TierProbe Probe, string Detail);

/// <summary>
/// Polls an HTTP endpoint of a running stack and classifies the result for <see cref="TestTierGate"/>
/// (issue #257).
/// </summary>
/// <remarks>
/// <para>
/// The one skippable observation is "nothing is listening": a refused connection, an unresolvable host,
/// or a TCP connect that is never accepted. Once a connection IS accepted, anything short of ready is a
/// fault — a stack that answers <c>502</c> until the deadline, or accepts connections and never sends an
/// HTTP response, is present and broken, and must not turn into a green job that ran nothing.
/// </para>
/// <para>
/// The deadline is short (10 s) when nothing asked for the tier, so a local run with no stack still
/// skips quickly, and long (120 s) when the tier is required, so a CI stack still running its migrations
/// is waited for rather than failed. <c>E2E_READY_TIMEOUT_SECONDS</c> overrides both.
/// </para>
/// </remarks>
internal static class StackProbe
{
    public const string ReadyTimeoutEnvVar = "E2E_READY_TIMEOUT_SECONDS";

    /// <summary>How long a TCP connect may take before the port counts as not listening.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long a connected server may take to answer one HTTP request.</summary>
    public static readonly TimeSpan HttpProbeTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>An already-running stack should answer fast; nobody asked for the tier, so don't wait.</summary>
    public static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The tier is required: a stack still migrating is waited for, not failed.</summary>
    public static readonly TimeSpan RequiredTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The fixture is building and starting the stack itself.</summary>
    public static readonly TimeSpan ManagedTimeout = TimeSpan.FromSeconds(180);

    public static TimeSpan ResolveReadyTimeout(bool managed, bool required) =>
        ResolveReadyTimeout(managed, required, Environment.GetEnvironmentVariable(ReadyTimeoutEnvVar));

    public static TimeSpan ResolveReadyTimeout(bool managed, bool required, string? overrideSeconds)
    {
        if (!string.IsNullOrWhiteSpace(overrideSeconds))
        {
            return int.TryParse(overrideSeconds, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : throw new TestTierUnavailableException(
                    $"{ReadyTimeoutEnvVar}='{overrideSeconds}' is not a positive whole number of seconds.");
        }

        return managed ? ManagedTimeout : required ? RequiredTimeout : QuickTimeout;
    }

    /// <summary>
    /// True when <paramref name="exception"/> means nothing answered at all — the only probe failure that
    /// may become a skip. A reset connection, a malformed response or a TLS failure means something IS
    /// listening, so they are not this.
    /// </summary>
    /// <remarks>
    /// A timeout is deliberately NOT here. <see cref="PollAsync"/> connects at the TCP level first, so a
    /// connect that times out is already classified as absent; an HTTP timeout after that means the port
    /// accepted the connection and never answered — present and broken.
    /// </remarks>
    public static bool IsNotListening(Exception exception) => exception switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError } => true,
        HttpRequestException { InnerException: SocketException socket } => socket.SocketErrorCode is
            SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.HostUnreachable
            or SocketError.NetworkUnreachable or SocketError.TimedOut or SocketError.TryAgain,
        _ => false,
    };

    /// <summary>
    /// Polls <paramref name="url"/> until <paramref name="classify"/> says ready or fatal, or the deadline
    /// passes. The LAST observation decides the outcome at the deadline: still not listening is
    /// <see cref="TierProbe.PrerequisiteMissing"/>; answering-but-not-ready is <see cref="TierProbe.Faulted"/>.
    /// </summary>
    public static async Task<ProbeOutcome> PollAsync(
        HttpClient client,
        Uri url,
        TimeSpan timeout,
        Func<HttpResponseMessage, Task<(ProbeStep Step, string Detail)>> classify,
        TimeSpan? pollInterval = null,
        Func<Uri, Task<string?>>? connect = null)
    {
        connect ??= TryConnectAsync;
        var deadline = DateTime.UtcNow + timeout;
        ProbeOutcome last = new(TierProbe.PrerequisiteMissing, $"nothing answered at {url}");

        while (true)
        {
            // TCP first: refused, unresolvable or a connect timeout is the one skippable observation.
            var connectFailure = await connect(url);
            if (connectFailure is not null)
            {
                last = new ProbeOutcome(TierProbe.PrerequisiteMissing, $"nothing is listening at {url} ({connectFailure})");
                if (DateTime.UtcNow >= deadline)
                {
                    return last;
                }

                await Task.Delay(pollInterval ?? PollInterval);
                continue;
            }

            try
            {
                using var response = await client.GetAsync(url);
                var (step, detail) = await classify(response);
                switch (step)
                {
                    case ProbeStep.Ready:
                        return new ProbeOutcome(TierProbe.Ready, detail);
                    case ProbeStep.Fatal:
                        return new ProbeOutcome(TierProbe.Faulted, $"{url} {detail}");
                    default:
                        last = new ProbeOutcome(TierProbe.Faulted, $"{url} still {detail} after {timeout.TotalSeconds:0} s");
                        break;
                }
            }
            catch (Exception ex) when (IsNotListening(ex))
            {
                last = new ProbeOutcome(TierProbe.PrerequisiteMissing, $"nothing is listening at {url} ({ex.Message})");
            }
            catch (Exception ex) when (ex is TaskCanceledException or TimeoutException)
            {
                // The TCP connect succeeded a moment ago; no HTTP answer inside the window is a hung
                // server, not an absent one.
                last = new ProbeOutcome(
                    TierProbe.Faulted,
                    $"{url} accepted the connection but sent no HTTP response within {client.Timeout.TotalSeconds:0} s");
            }
            catch (HttpRequestException ex)
            {
                // Something accepted the connection and then misbehaved — present, not absent.
                last = new ProbeOutcome(TierProbe.Faulted, $"{url} answered but the exchange failed: {ex.Message}");
            }

            if (DateTime.UtcNow >= deadline)
            {
                return last;
            }

            await Task.Delay(pollInterval ?? PollInterval);
        }
    }

    /// <summary>
    /// Opens (and closes) a TCP connection to <paramref name="url"/>'s host and port. Returns <c>null</c>
    /// when something accepted it, otherwise why not — refused, unresolvable, or no accept within
    /// <see cref="ConnectTimeout"/>.
    /// </summary>
    public static async Task<string?> TryConnectAsync(Uri url)
    {
        using var socket = new TcpClient();
        using var cancel = new CancellationTokenSource(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(url.Host, url.Port, cancel.Token);
            return null;
        }
        catch (OperationCanceledException)
        {
            return $"no TCP accept within {ConnectTimeout.TotalSeconds:0} s";
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode.ToString();
        }
    }

    /// <summary>Ready on any 2xx; anything else is retried until the deadline.</summary>
    public static Task<(ProbeStep Step, string Detail)> SuccessStatus(HttpResponseMessage response) =>
        Task.FromResult(response.IsSuccessStatusCode
            ? (ProbeStep.Ready, $"answered {(int)response.StatusCode}")
            : (ProbeStep.Retry, $"answered {(int)response.StatusCode} {response.ReasonPhrase}"));

    public static bool IsHtml(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase);

    public static bool IsJson(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType is { } mediaType
        && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
}
