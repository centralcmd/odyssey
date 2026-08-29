using System.Diagnostics;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// Prepares the end-to-end environment: ensures the seeded application stack is reachable and
/// the Playwright browser is installed. The stack is expected to already be running
/// (<c>docker compose up -d --build</c>); set <c>E2E_MANAGE_STACK=true</c> to have the fixture
/// bring it up and tear it down itself. If the stack is unreachable or the browser cannot be
/// installed (e.g. no Docker / no network), <see cref="Available"/> is false and tests skip.
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    // Override the client URL to drive; opt into the fixture managing the Compose stack itself.
    private const string BaseUrlEnvVar = "E2E_BASE_URL";
    private const string ManageStackEnvVar = "E2E_MANAGE_STACK";
    private const string DefaultBaseUrl = "http://localhost:5199";
    private const string BrowserName = "chromium";

    private static readonly TimeSpan HttpProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromSeconds(2);
    // Building/starting the stack ourselves takes a while; an already-running one should answer fast.
    private static readonly TimeSpan ReadyTimeoutWhenManaged = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan ReadyTimeoutWhenExisting = TimeSpan.FromSeconds(10);

    private bool stackStartedByFixture;

    public string BaseUrl { get; } =
        Environment.GetEnvironmentVariable(BaseUrlEnvVar)?.TrimEnd('/') ?? DefaultBaseUrl;

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            if (string.Equals(Environment.GetEnvironmentVariable(ManageStackEnvVar), "true", StringComparison.OrdinalIgnoreCase))
            {
                await StartStackAsync();
            }

            if (!await WaitForReadyAsync())
            {
                SkipReason = $"Application stack not reachable at {BaseUrl}. Start it with 'docker compose up -d --build' or set {ManageStackEnvVar}=true.";
                return;
            }

            var exitCode = Microsoft.Playwright.Program.Main(["install", BrowserName]);
            if (exitCode != 0)
            {
                SkipReason = $"Playwright browser install failed (exit {exitCode}).";
                return;
            }

            Available = true;
        }
        catch (Exception ex)
        {
            SkipReason = $"E2E environment unavailable: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (stackStartedByFixture)
        {
            await RunComposeAsync("down");
        }
    }

    private async Task StartStackAsync()
    {
        await RunComposeAsync("up", "-d", "--build");
        stackStartedByFixture = true;
    }

    private async Task<bool> WaitForReadyAsync()
    {
        using var client = new HttpClient { Timeout = HttpProbeTimeout };
        var readyTimeout = stackStartedByFixture ? ReadyTimeoutWhenManaged : ReadyTimeoutWhenExisting;
        var deadline = DateTime.UtcNow + readyTimeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await client.GetAsync(BaseUrl);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // Stack not up yet; keep polling until the deadline.
            }

            await Task.Delay(ReadyPollInterval);
        }

        return false;
    }

    private static async Task RunComposeAsync(params string[] arguments)
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
        await process.WaitForExitAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class StackCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "Stack";
}
