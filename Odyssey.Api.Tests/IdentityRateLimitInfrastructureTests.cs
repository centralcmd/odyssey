using System.Text.Json;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The identity rate limit is relaxed for the dev stacks and must stay relaxed ONLY there.
///
/// <para>
/// The E2E suites drive one stack from one address, which is one rate-limit partition key. The
/// <c>identity</c> policy covers the whole <c>MapIdentityApi</c> group, <c>/manage/*</c> included, and
/// the Blazor client calls <c>GET manage/info</c> to resolve auth state on sign-in and on navigation —
/// so a browser test costs many permits, not the one its single login suggests. Measured:
/// <c>Odyssey.E2ETests</c> alone spends the whole 30-permit window, <c>Odyssey.E2ETests.Api</c> another
/// six. Run together (or back to back inside one 60-second window, as the E2E workflow does) the second
/// suite is answered with <c>429</c>s.
/// </para>
///
/// <para>
/// <strong>The hazard this class exists for is the merge, not the number.</strong> Compose merges
/// environment maps, and the production overlay is always layered on top of the base file — so the
/// dev-only relaxation in <c>docker-compose.yml</c> reaches production unless the overlay explicitly
/// removes it. Getting that wrong makes the only unauthenticated write surface in the app ten times
/// cheaper to hammer, silently, as a side effect of a test fix. These assertions are cheap; that
/// failure would not be.
/// </para>
///
/// <para>
/// Note what is deliberately NOT asserted here: the limiter's behaviour. That it rejects, at what
/// count, and with what body belongs to <c>IdentityRateLimitingTests</c>,
/// <c>IdentityEmailRateLimitingTests</c> and <c>IdentityMailEndpointConventionTests</c>, which drive it
/// with their own configuration. This file only pins where the number is allowed to differ.
/// </para>
/// </summary>
public class IdentityRateLimitInfrastructureTests
{
    private const string PermitLimitVariable = "RateLimiting__Identity__PermitLimit";

    /// <summary>
    /// The shipped default — the value every deployment that overrides nothing actually runs — is
    /// still 30. Read from the JSON rather than matched as text, so reformatting cannot fake a pass.
    /// </summary>
    [Fact]
    public void TheShippedDefault_IsStillThirtyPerMinute()
    {
        using var appsettings = JsonDocument.Parse(RepositoryRoot.ReadAllText("Odyssey.Api/appsettings.json"));

        var identity = appsettings.RootElement.GetProperty("RateLimiting").GetProperty("Identity");

        Assert.Equal(30, identity.GetProperty("PermitLimit").GetInt32());
        Assert.Equal(60, identity.GetProperty("WindowSeconds").GetInt32());
    }

    /// <summary>
    /// The dev stack raises it, so the two E2E suites fit. Both dev surfaces carry it, because the
    /// suites run against either one: Compose is what the E2E workflow stands up, Aspire is what
    /// <c>dotnet run --project Odyssey.AppHost</c> gives a developer.
    /// </summary>
    [Fact]
    public void BothDevStacks_RaiseTheIdentityLimit()
    {
        Assert.Contains(
            $"{PermitLimitVariable}: ", RepositoryRoot.ReadAllText("docker-compose.yml"), StringComparison.Ordinal);

        Assert.Contains(
            $"WithEnvironment(\"{PermitLimitVariable}\"",
            RepositoryRoot.ReadAllText(System.IO.Path.Combine("Odyssey.AppHost", "AppHost.cs")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// …and the production overlay takes it back out. <c>!reset</c> drops the key entirely so the
    /// value falls back to <c>appsettings.json</c>, rather than pinning a literal 30 that would have
    /// to be kept in agreement with the file above.
    ///
    /// <para>
    /// Asserted as <c>!reset</c> specifically, not merely "the overlay mentions the key": an overlay
    /// that set the key to anything else — including a number someone believed was the default —
    /// would put the production limit in a second place and let the two drift.
    /// </para>
    /// </summary>
    [Fact]
    public void TheProductionOverlay_ResetsTheDevRelaxationBackOut()
    {
        var overlay = RepositoryRoot.ReadAllText("docker-compose.prod.yml");

        Assert.Contains($"{PermitLimitVariable}: !reset", overlay, StringComparison.Ordinal);

        // The only mention of the key in the overlay is that reset — no second, non-reset assignment.
        var assignments = overlay.Split(PermitLimitVariable).Length - 1;
        Assert.Equal(1, assignments);
    }

    /// <summary>
    /// The mail limit is NOT relaxed anywhere. It bounds the two routes that put a message on the
    /// wire, no E2E test touches them, and its cost of abuse is SMTP quota and the sending domain's
    /// reputation rather than CPU — so it is the one identity limit a test-stack convenience must
    /// never reach for.
    ///
    /// <para>
    /// Matched on the ASSIGNMENT shape — the key with its trailing <c>:</c>, and the
    /// <c>WithEnvironment(</c> call for Aspire — rather than on the bare key, the same way
    /// <c>SecretSettingsInfrastructureTests</c> matches its retired keys. Both stack files name this
    /// key in a comment saying why it is left alone, and a bare substring match would fail on the
    /// explanation for the rule it is enforcing.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.prod.yml")]
    public void TheMailLimit_IsNotRaisedByAnyStack(string composeFile)
    {
        Assert.DoesNotContain(
            "RateLimiting__IdentityEmail__PermitLimit: ",
            RepositoryRoot.ReadAllText(composeFile),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "WithEnvironment(\"RateLimiting__IdentityEmail",
            RepositoryRoot.ReadAllText(System.IO.Path.Combine("Odyssey.AppHost", "AppHost.cs")),
            StringComparison.Ordinal);
    }
}
