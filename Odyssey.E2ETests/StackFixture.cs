using System.Diagnostics;
using Microsoft.Playwright;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// Prepares the end-to-end environment: ensures the seeded application stack is reachable, that its
/// client can reach the API, and that the Playwright browser is installed. The stack is expected to
/// already be running (<c>docker compose up -d --build</c>); set <c>E2E_MANAGE_STACK=true</c> to have the
/// fixture bring it up and tear it down itself.
/// </summary>
/// <remarks>
/// If nothing is listening at the client address, or the browser cannot be installed, <see cref="Available"/>
/// is false and tests skip — unless <c>ODYSSEY_REQUIRE_TIER</c> names <c>e2e</c>, in which case they fail.
/// A stack that answers but is broken always fails the tier (issue #257; see <see cref="TestTierGate"/>).
/// </remarks>
public sealed class StackFixture : IAsyncLifetime
{
    // Override the client URL to drive; opt into the fixture managing the Compose stack itself.
    private const string BaseUrlEnvVar = "E2E_BASE_URL";
    private const string ManageStackEnvVar = "E2E_MANAGE_STACK";
    private const string DefaultBaseUrl = "http://localhost:5199";
    private const string BrowserName = "chromium";

    // The API as the Compose NGINX proxies it on the client's own origin.
    private const string SameOriginApiHealthPath = "/api/healthz";

    // How long the browser preflight watches the app's startup calls before calling it inconclusive.
    private static readonly TimeSpan PreflightBudget = TimeSpan.FromSeconds(30);

    private bool stackStartedByFixture;

    public string BaseUrl { get; } =
        Environment.GetEnvironmentVariable(BaseUrlEnvVar)?.TrimEnd('/') ?? DefaultBaseUrl;

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        // Parsed on every path, so a typo in ODYSSEY_REQUIRE_TIER fails even a healthy run.
        var required = TestTierGate.IsRequired(TestTierGate.E2E);

        try
        {
            await InitializeCoreAsync(required);
        }
        catch
        {
            // A fixture that started the stack and then failed must not leave it running, whether or not
            // the runner goes on to call DisposeAsync — TearDownAsync is idempotent either way.
            await TearDownAsync();
            throw;
        }
    }

    private async Task InitializeCoreAsync(bool required)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(ManageStackEnvVar), "true", StringComparison.OrdinalIgnoreCase))
        {
            stackStartedByFixture = true;
            var (exitCode, error) = await RunComposeAsync("up", "-d", "--build");
            if (exitCode != 0)
            {
                throw TestTierGate.Fault(TestTierGate.E2E, $"'docker compose up -d --build' exited {exitCode}: {error}");
            }
        }

        var timeout = StackProbe.ResolveReadyTimeout(stackStartedByFixture, required);
        using var client = new HttpClient { Timeout = StackProbe.HttpProbeTimeout };

        var shell = await StackProbe.PollAsync(client, new Uri(BaseUrl), timeout, StackProbe.SuccessStatus);
        SkipReason = TestTierGate.Resolve(
            TestTierGate.E2E,
            shell.Probe,
            shell.Probe == TierProbe.PrerequisiteMissing
                ? $"Application stack not reachable at {BaseUrl}: {shell.Detail}. Start it with 'docker compose up -d --build' or set {ManageStackEnvVar}=true."
                : $"The client at {BaseUrl} answers but is not serving the app: {shell.Detail}.");
        if (SkipReason is not null)
        {
            return;
        }

        // From here the stack is PRESENT, so nothing below may become a skip except a browser install
        // that cannot reach the network — a prerequisite of this machine, not of the stack.
        var sameOriginApi = await ProbeSameOriginApiAsync(client, timeout);

        var installExit = Microsoft.Playwright.Program.Main(["install", BrowserName]);
        if (installExit != 0)
        {
            SkipReason = TestTierGate.Resolve(
                TestTierGate.E2E, TierProbe.PrerequisiteMissing, $"Playwright browser install failed (exit {installExit}).");
            return;
        }

        if (sameOriginApi == SameOriginApi.Absent)
        {
            await AssertClientDoesNotCallMissingSameOriginApiAsync();
        }

        Available = true;
    }

    public Task DisposeAsync() => TearDownAsync();

    private async Task TearDownAsync()
    {
        if (stackStartedByFixture)
        {
            stackStartedByFixture = false;
            await RunComposeAsync("down");
        }
    }

    internal enum SameOriginApi
    {
        /// <summary><c>/api/healthz</c> on the client origin answered JSON — the Compose NGINX proxy.</summary>
        Proxied,

        /// <summary>
        /// Nothing proxies the API on the client origin: <c>/api/healthz</c> answered the SPA fallback
        /// (<c>2xx text/html</c>) or a definitive "no such route" (<c>404</c>/<c>405</c>). Fine for a Debug
        /// client (it calls <c>http://localhost:5188</c> directly); fatal for a Release one (it calls this
        /// path). Only the browser preflight can tell which.
        /// </summary>
        Absent,
    }

    /// <summary>
    /// Waits for the API as the client's own origin exposes it. Under Compose that is NGINX's
    /// <c>/api/</c> proxy, which answers <c>502</c> while the API is still migrating — so this waits it
    /// out (for the same window the shell got: 10 s unless the tier is required or managed) rather than
    /// letting the first sign-in time out, and fails if it never recovers.
    /// </summary>
    private async Task<SameOriginApi> ProbeSameOriginApiAsync(HttpClient client, TimeSpan timeout)
    {
        SameOriginApi? verdict = null;
        var outcome = await StackProbe.PollAsync(
            client,
            new Uri(BaseUrl + SameOriginApiHealthPath),
            timeout,
            response =>
            {
                verdict = ClassifySameOriginApi(response, out var detail);
                return Task.FromResult((verdict is null ? ProbeStep.Retry : ProbeStep.Ready, detail));
            });

        if (outcome.Probe == TierProbe.Ready && verdict is { } ready)
        {
            return ready;
        }

        // The shell answered a moment ago, so "not listening" now means it went away — still a fault.
        throw TestTierGate.Fault(
            TestTierGate.E2E,
            $"The client at {BaseUrl} is up but the API behind its same-origin /api/ path never became healthy: {outcome.Detail}.");
    }

    /// <summary>
    /// What <c>/api/healthz</c> on the client origin says about that origin, or <c>null</c> to retry.
    /// <list type="bullet">
    /// <item><c>2xx</c> JSON — <see cref="SameOriginApi.Proxied"/>.</item>
    /// <item><c>2xx text/html</c> (the SPA fallback) or <c>404</c>/<c>405</c> of any type (a host that
    /// simply has no such route) — <see cref="SameOriginApi.Absent"/>; the browser preflight decides.</item>
    /// <item>Anything else — retried. In particular NGINX's <c>502</c> while the API migrates, which is
    /// ALSO <c>text/html</c> (nginx.conf sets no <c>error_page</c> on <c>/api/</c>), so the status is
    /// checked before the content type.</item>
    /// </list>
    /// </summary>
    internal static SameOriginApi? ClassifySameOriginApi(HttpResponseMessage response, out string detail)
    {
        var status = (int)response.StatusCode;
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "no content type";

        if (response.IsSuccessStatusCode && StackProbe.IsJson(response))
        {
            detail = $"answered {status} JSON";
            return SameOriginApi.Proxied;
        }

        if (response.IsSuccessStatusCode && StackProbe.IsHtml(response))
        {
            detail = "answered text/html (SPA fallback: no API proxied on this origin)";
            return SameOriginApi.Absent;
        }

        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            detail = $"answered {status} ({mediaType}): no API route on this origin";
            return SameOriginApi.Absent;
        }

        detail = $"answered {status} {response.ReasonPhrase} ({mediaType})";
        return null;
    }

    /// <summary>
    /// The Release-under-Aspire check (issue #257, CLAUDE.md "Run the Aspire stack in Debug"). Loads the
    /// app once and watches the calls it makes on startup: if the app itself requests the same-origin
    /// <c>/api/</c> path and gets <c>text/html</c> back, the client is a Release build served where no
    /// NGINX proxies the API, and every test would die on a sign-in timeout. A Debug client under Aspire
    /// never requests that path (it calls <c>http://localhost:5188</c>), so it cannot trip this — which is
    /// why the decision rests on what the app requests rather than on what the path returns to us.
    /// </summary>
    private async Task AssertClientDoesNotCallMissingSameOriginApiAsync()
    {
        var origin = new Uri(BaseUrl).GetLeftPart(UriPartial.Authority);
        var htmlApiResponses = new List<string>();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.Response += (_, response) =>
        {
            if (IsSameOriginApiHtml(origin, response.Url, response.Status, response.Headers.GetValueOrDefault("content-type")))
            {
                lock (htmlApiResponses)
                {
                    htmlApiResponses.Add(response.Url);
                }
            }
        };

        try
        {
            await page.GotoAsync(BaseUrl + "/login", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = (float)PreflightBudget.TotalMilliseconds,
            });
            await page.GetByLabel("Username or Email").WaitForAsync(new LocatorWaitForOptions
            {
                Timeout = (float)PreflightBudget.TotalMilliseconds,
            });
        }
        catch (TimeoutException)
        {
            // Inconclusive on its own — a crashed app never renders the form, which is exactly the case
            // the recorded responses below identify. Anything else the tests will report themselves.
        }

        string[] offending;
        lock (htmlApiResponses)
        {
            offending = [.. htmlApiResponses];
        }

        if (offending.Length > 0)
        {
            throw TestTierGate.Fault(
                TestTierGate.E2E,
                $"The client at {BaseUrl} called its own origin's /api/ path and got text/html (the SPA's index.html) " +
                $"instead of JSON — e.g. {offending[0]}. This is a RELEASE build of Odyssey.Client served where no NGINX " +
                "proxies /api/ (typically `dotnet run --project Odyssey.AppHost -c Release`): Release resolves the API " +
                "to the same-origin /api/ path, which only the Compose stack serves. Run the Aspire stack in Debug (the " +
                "default — do not pass -c Release). See CLAUDE.md, \"Run the Aspire stack in Debug; only Compose may be Release\". " +
                "If this IS the Compose stack, its NGINX is letting /api/ fall through to index.html — check that " +
                "Odyssey.Client/nginx.conf's `location /api/` proxy block is present in the image being served.");
        }
    }

    /// <summary>
    /// True for a response the APP requested from its own origin under <c>/api/</c> that came back
    /// <c>2xx text/html</c> — the SPA fallback answering an API call. A non-2xx HTML body (NGINX's own
    /// <c>502</c> page while the API starts) is an error page, not the fallback, and is excluded.
    /// </summary>
    internal static bool IsSameOriginApiHtml(string origin, string url, int status, string? contentType) =>
        status is >= 200 and < 300
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)
        && contentType is not null
        && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);

    private static async Task<(int ExitCode, string Error)> RunComposeAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("compose");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start 'docker compose'.");

        // Drain both streams while waiting: a redirected stream nobody reads fills its pipe and stalls
        // a chatty `compose up --build`.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        var errorText = await error;
        return (process.ExitCode, errorText.Length > 2000 ? errorText[^2000..] : errorText);
    }
}

[CollectionDefinition(Name)]
public sealed class StackCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "Stack";
}
