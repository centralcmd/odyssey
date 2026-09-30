using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Auth;
using Odyssey.ApiClient.Contracts;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// Drives the real, running API over HTTP (no browser) — the API sibling of the Playwright E2E
/// suite. It reuses the same already-running, seeded stack (the migration service seeds it), and
/// authenticates through the real <c>/login</c> cookie flow rather than injecting claims, so the
/// tests exercise authentication, permission enforcement, status codes and contracts end to end.
///
/// The stack is expected to be running (<c>docker compose up -d --build</c>, or Aspire); set
/// <c>E2E_MANAGE_STACK=true</c> to have the fixture bring Compose up/down itself. If nothing is
/// listening at the API address, <see cref="Available"/> is false and tests skip — unless
/// <c>ODYSSEY_REQUIRE_TIER</c> names <c>e2e-api</c>, in which case they fail. An API that answers but
/// never becomes healthy always fails the tier (issue #257; see <see cref="TestTierGate"/>).
/// </summary>
public sealed class ApiStackFixture : IAsyncLifetime
{
    private const string BaseUrlEnvVar = "E2E_API_BASE_URL";
    private const string ManageStackEnvVar = "E2E_MANAGE_STACK";
    private const string DefaultBaseUrl = "http://localhost:5188";
    private const string HealthPath = "/healthz";

    private bool stackStartedByFixture;

    // Providers own the authenticated HttpClients handed to tests; held so they are not collected
    // mid-run and are disposed once with the fixture.
    private readonly List<ServiceProvider> providers = [];

    // One signed-in session per demo login, reused across every test in the collection.
    // A plain dictionary is safe here: all three test classes share ApiStackCollection, so xUnit
    // runs them serially on one thread.
    private readonly Dictionary<string, ServiceProvider> sessions = new(StringComparer.OrdinalIgnoreCase);

    public string BaseUrl { get; } =
        Environment.GetEnvironmentVariable(BaseUrlEnvVar)?.TrimEnd('/') ?? DefaultBaseUrl;

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        // Parsed on every path, so a typo in ODYSSEY_REQUIRE_TIER fails even a healthy run.
        var required = TestTierGate.IsRequired(TestTierGate.E2EApi);

        try
        {
            await InitializeCoreAsync(required);
        }
        catch
        {
            // A fixture that started the stack and then failed must not leave it running, whether or not
            // the runner goes on to call DisposeAsync — TearDownStackAsync is idempotent either way.
            await TearDownStackAsync();
            throw;
        }
    }

    private async Task InitializeCoreAsync(bool required)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(ManageStackEnvVar), "true", StringComparison.OrdinalIgnoreCase))
        {
            stackStartedByFixture = true;
            var exitCode = await RunComposeAsync("up", "-d", "--build");
            if (exitCode != 0)
            {
                throw TestTierGate.Fault(TestTierGate.E2EApi, $"'docker compose up -d --build' exited {exitCode}.");
            }
        }

        // Long when required (a CI stack may still be migrating behind a live port), short otherwise so a
        // local run with no stack still skips quickly.
        var timeout = StackProbe.ResolveReadyTimeout(stackStartedByFixture, required);
        using var client = new HttpClient { Timeout = StackProbe.HttpProbeTimeout };
        var outcome = await StackProbe.PollAsync(client, new Uri(BaseUrl + HealthPath), timeout, ClassifyHealth);

        SkipReason = TestTierGate.Resolve(
            TestTierGate.E2EApi,
            outcome.Probe,
            outcome.Probe == TierProbe.PrerequisiteMissing
                ? $"API not reachable at {BaseUrl}: {outcome.Detail}. Start the stack (docker compose up -d --build) or set {ManageStackEnvVar}=true."
                : $"API at {BaseUrl} is not healthy: {outcome.Detail}.");
        Available = SkipReason is null;
    }

    /// <summary>
    /// <c>/healthz</c> answers JSON. An HTML answer is the Blazor client's SPA fallback — the base URL
    /// names the client, not the API — and waiting will never change that.
    /// </summary>
    internal static Task<(ProbeStep Step, string Detail)> ClassifyHealth(HttpResponseMessage response)
    {
        if (StackProbe.IsHtml(response))
        {
            return Task.FromResult((ProbeStep.Fatal,
                $"answered text/html, not JSON — {BaseUrlEnvVar} points at the client (or another HTML server), not the API"));
        }

        return StackProbe.SuccessStatus(response);
    }

    public async Task DisposeAsync()
    {
        foreach (var provider in providers)
        {
            await provider.DisposeAsync();
        }

        await TearDownStackAsync();
    }

    private async Task TearDownStackAsync()
    {
        if (stackStartedByFixture)
        {
            stackStartedByFixture = false;
            await RunComposeAsync("down");
        }
    }

    /// <summary>
    /// An unauthenticated client — used to assert the 401 challenge. It still carries the antiforgery
    /// pipeline, so a write reaches the authentication check instead of stopping at the 400 the
    /// antiforgery gate returns for a tokenless request; what it lacks is an <b>auth</b> cookie.
    /// </summary>
    public HttpClient CreateAnonymousClient()
    {
        var provider = BuildProvider();
        providers.Add(provider);
        return provider.GetRequiredService<HttpClient>();
    }

    /// <summary>
    /// A bare client with <b>no</b> antiforgery handler, for the tests that assert the antiforgery gate
    /// itself fires on a tokenless write. Every other caller wants
    /// <see cref="CreateAnonymousClient"/>, which behaves like a real signed-out browser.
    /// </summary>
    public HttpClient CreateTokenlessClient() => new() { BaseAddress = new Uri(BaseUrl) };

    /// <summary>
    /// Builds a service provider wired exactly the way a non-browser consumer of
    /// <c>Odyssey.ApiClient</c> is meant to be: an <see cref="HttpClient"/> whose cookie container
    /// holds the session (the browser's job in the Blazor app), the library's
    /// <see cref="AntiforgeryHandler"/> beneath it, and every typed client registered by
    /// <c>AddOdysseyApiClient()</c>.
    /// </summary>
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddOdysseyApiClient();
        services.AddScoped(sp =>
        {
            var antiforgery = sp.GetRequiredService<AntiforgeryHandler>();
            antiforgery.InnerHandler = new HttpClientHandler
            {
                CookieContainer = new System.Net.CookieContainer(),
                UseCookies = true,
            };

            return new HttpClient(antiforgery) { BaseAddress = new Uri(BaseUrl) };
        });

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Returns a provider whose <see cref="HttpClient"/> and typed clients carry the auth cookie for
    /// <paramref name="email"/>, signing in via the real <c>/login</c> cookie flow the first time each
    /// login is asked for and reusing that session afterwards. The fixture owns and disposes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reuse is what keeps this suite inside the API's own defences. <c>/login</c> is rate-limited
    /// to 30 requests per minute per IP (<c>RateLimiting:Identity</c>, issue #382), and signing in
    /// afresh per test spent 20 of that budget in a three-second burst — enough that the Playwright
    /// suite running alongside it in the same <c>dotnet test Odyssey.sln</c> run had its own logins
    /// answered with a <c>429</c>, and failed with what looked like a credentials error. One session
    /// per demo login costs four.
    /// </para>
    /// <para>
    /// Sharing is safe because a session is only ever a set of cookies: these tests probe status codes
    /// and read contracts, and nothing here mutates the signed-in user (no password change, no sign-out,
    /// no role edit). A test that needs a pristine session should sign in through
    /// <see cref="BuildProvider"/> directly rather than weaken this one — and it should account for the
    /// budget above.
    /// </para>
    /// </remarks>
    public async Task<ServiceProvider> CreateAuthenticatedProviderAsync(string email, string password)
    {
        if (sessions.TryGetValue(email, out var existing))
        {
            return existing;
        }

        var provider = BuildProvider();
        providers.Add(provider);

        // The same AuthApiClient the Blazor app uses; the antiforgery token /login requires is
        // fetched and attached by the library's handler, not by anything in this fixture.
        var outcome = await provider.GetRequiredService<AuthApiClient>()
            .LoginAsync(new LoginRequest { Email = email, Password = password });

        if (outcome != LoginOutcome.Success)
        {
            throw new InvalidOperationException($"Login failed for '{email}': {outcome}.");
        }

        sessions[email] = provider;
        return provider;
    }

    /// <summary>
    /// A client carrying the auth cookie for <paramref name="email"/>. Shared per login (see
    /// <see cref="CreateAuthenticatedProviderAsync"/>) and disposed with the fixture, so callers must
    /// <b>not</b> wrap it in <c>using</c>.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(string email, string password) =>
        (await CreateAuthenticatedProviderAsync(email, password)).GetRequiredService<HttpClient>();

    /// <summary>
    /// POSTs <paramref name="body"/> to <paramref name="path"/>. The antiforgery token (and its paired
    /// secret cookie) is attached by <see cref="AntiforgeryHandler"/> in the client's pipeline, exactly
    /// as it is for the real browser client — this no longer hand-rolls the handshake.
    /// </summary>
    public Task<HttpResponseMessage> PostWithAntiforgeryAsync(HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(path, body);

    /// <summary>
    /// DELETEs <paramref name="path"/>, with the antiforgery token attached by the client pipeline, so
    /// an authorized write reaches the handler instead of stopping at the 400 the gate returns for a
    /// tokenless write.
    /// </summary>
    public Task<HttpResponseMessage> DeleteWithAntiforgeryAsync(HttpClient client, string path) =>
        client.DeleteAsync(path);

    /// <summary>
    /// POSTs a prepared <paramref name="content"/> — a multipart upload, where
    /// <see cref="PostWithAntiforgeryAsync"/>'s JSON body does not fit. The token comes from the same
    /// client pipeline, so this is the JSON helper's shape with the serialization left to the caller.
    /// </summary>
    public Task<HttpResponseMessage> PostContentWithAntiforgeryAsync(
        HttpClient client, string path, HttpContent content) =>
        client.PostAsync(path, content);

    private static async Task<int> RunComposeAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("docker") { UseShellExecute = false };
        startInfo.ArgumentList.Add("compose");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start 'docker compose'.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}

[CollectionDefinition(Name)]
public sealed class ApiStackCollection : ICollectionFixture<ApiStackFixture>
{
    public const string Name = "ApiStack";
}
