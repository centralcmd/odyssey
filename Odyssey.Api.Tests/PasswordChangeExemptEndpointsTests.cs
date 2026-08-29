using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The guarantee behind the must-change-password gate (issue #406 §5.6). The per-module sample in
/// <see cref="PasswordChangeRequiredGateTests"/> is a smoke check; this is what proves no endpoint was
/// missed, and that no endpoint gained an exemption nobody reviewed.
/// </summary>
public class PasswordChangeExemptEndpointsTests
{
    [Fact]
    public async Task EveryEndpointIsEitherAnonymousOrBlockedByDefault()
    {
        // Deny-by-default is the polarity: protection derives from an endpoint already carrying
        // authorization metadata, so a controller written next year is covered the day it is written.
        // Nothing to assert per endpoint, then — this states the invariant the rule depends on, that the
        // two categories are exhaustive.
        await using var factory = new OdysseyApiFactory([]);

        var uncategorised = Endpoints(factory)
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<IAuthorizeData>() is null
                && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
                && endpoint.Metadata.GetMetadata<IPasswordChangeExemptMetadata>() is not null)
            .Select(Describe)
            .ToList();

        Assert.Empty(uncategorised);
    }

    /// <summary>
    /// The exempt set is asserted per route + HTTP method rather than per controller, so a third action
    /// added to <c>AuthController</c> cannot inherit an exemption, and a new one cannot land without a
    /// reviewer seeing this test change.
    /// </summary>
    [Fact]
    public async Task TheExemptSetIsExactlyTheFiveExpectedRouteAndMethodPairs()
    {
        await using var factory = new OdysseyApiFactory([]);

        var exempt = Endpoints(factory)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IPasswordChangeExemptMetadata>() is not null)
            .SelectMany(PasswordChangeExemptRoutes.Describe)
            .OrderBy(endpoint => endpoint.ToString(), StringComparer.Ordinal)
            .ToList();

        var expected = PasswordChangeExemptRoutes.Expected
            .OrderBy(endpoint => endpoint.ToString(), StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, exempt);
    }

    /// <summary>
    /// The invariant the "no <c>IAuthorizeData</c> → anonymous" rule leans on. An endpoint carrying both
    /// would be anonymous in practice but blocked by the middleware, so this fails at test time rather
    /// than becoming a silent 403 for a page nobody thought was gated.
    /// </summary>
    [Fact]
    public async Task NoEndpointCarriesBothAuthorizeDataAndAllowAnonymous_WithoutBeingUnderstood()
    {
        await using var factory = new OdysseyApiFactory([]);

        // The blanket MapControllers().RequireAuthorization() puts IAuthorizeData on every controller
        // endpoint, so an [AllowAnonymous] action necessarily carries both. That combination is exactly
        // why the middleware checks IAllowAnonymous FIRST — these are the known, reviewed members.
        var expected = new[]
        {
            "GET /api/legal/license",
            "GET /api/legal/terms-of-service/current",
        };

        var both = Endpoints(factory)
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null
                && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(Describe)
            .OrderBy(description => description, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, both);
    }

    /// <summary>
    /// Mode 1 of the startup check: an exemption removed from one of the actions. A missing exemption is
    /// not a hole, it is a lockout — a gated user with no reachable change-password endpoint can never
    /// recover — so the app must refuse to start rather than serve traffic in that state.
    /// </summary>
    [Fact]
    public void AMissingAttributeExemption_FailsTheStartupCheck()
    {
        var endpoints = ExemptEndpoints(PasswordChangeExemptRoutes.Expected
            .Where(endpoint => endpoint.Route != "/api/account/password"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => PasswordChangeExemptRoutes.ValidateExemptEndpoints(endpoints));

        Assert.Contains("/api/account/password", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mode 2, structurally different: the route text drifts rather than the attribute disappearing. There
    /// is nothing to "remove" here — the endpoint is still exempt, it simply answers a different path — and
    /// this is the mode a match-and-log-if-missing convention would silently degrade on.
    /// </summary>
    [Fact]
    public void ADriftedRoute_FailsTheStartupCheckToo()
    {
        var endpoints = ExemptEndpoints(PasswordChangeExemptRoutes.Expected
            .Select(endpoint => endpoint.Route == "/logout"
                ? new PasswordChangeExemptEndpoint(endpoint.Method, "/signout")
                : endpoint));

        var exception = Assert.Throws<InvalidOperationException>(
            () => PasswordChangeExemptRoutes.ValidateExemptEndpoints(endpoints));

        Assert.Contains("/logout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCompleteExemptSet_PassesTheStartupCheck()
    {
        // The positive control: without it, a check that threw unconditionally would satisfy both tests above.
        PasswordChangeExemptRoutes.ValidateExemptEndpoints(ExemptEndpoints(PasswordChangeExemptRoutes.Expected));
    }

    [Fact]
    public async Task TheRealApplication_PassesTheStartupCheck()
    {
        // Program.cs runs this at boot, so a host that starts has already passed — asserting it here names
        // the guarantee rather than leaving it implied by every other test's setup.
        await using var factory = new OdysseyApiFactory([]);

        PasswordChangeExemptRoutes.ValidateExemptEndpoints(Endpoints(factory));
    }

    private static IReadOnlyList<Endpoint> Endpoints(OdysseyApiFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    private static string Describe(Endpoint endpoint) =>
        string.Join(" ", PasswordChangeExemptRoutes.Describe(endpoint).Select(described => described.ToString()));

    /// <summary>Synthetic endpoints carrying the exemption, for the startup check's own tests.</summary>
    private static IReadOnlyList<Endpoint> ExemptEndpoints(IEnumerable<PasswordChangeExemptEndpoint> exempt) =>
        exempt.Select(endpoint => (Endpoint)new RouteEndpoint(
                _ => Task.CompletedTask,
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(endpoint.Route),
                order: 0,
                new EndpointMetadataCollection(
                    new HttpMethodMetadata([endpoint.Method]),
                    new StubExemption()),
                displayName: endpoint.ToString()))
            .ToList();

    private sealed class StubExemption : IPasswordChangeExemptMetadata;
}
