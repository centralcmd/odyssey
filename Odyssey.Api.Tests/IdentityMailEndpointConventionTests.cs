using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Odyssey.Api;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// <c>MapIdentityApi</c> maps its endpoints as one group, so the two mail-sending routes can only be
/// singled out by route text (issue #393). That makes the tighter limit hostage to a framework
/// rename, so the convention reports one — these tests pin both the match and the report.
/// </summary>
public class IdentityMailEndpointConventionTests
{
    [Fact]
    public void TheMailRoutes_AreTaggedAndNothingElseIs()
    {
        var logger = new CapturingLogger<IdentityMailEndpointConventionTests>();
        var builder = new FakeConventionBuilder();
        builder.RequireMailEndpointRateLimiting(logger);

        var endpoints = builder.Apply("/login", "/register", "/forgotPassword", "/resendConfirmationEmail");

        Assert.False(IsTagged(endpoints, "/login"));
        Assert.False(IsTagged(endpoints, "/register"));
        Assert.True(IsTagged(endpoints, "/forgotPassword"));
        Assert.True(IsTagged(endpoints, "/resendConfirmationEmail"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public void TheRoutesAreMatchedCaseInsensitively()
    {
        var logger = new CapturingLogger<IdentityMailEndpointConventionTests>();
        var builder = new FakeConventionBuilder();
        builder.RequireMailEndpointRateLimiting(logger);

        var endpoints = builder.Apply("/forgotpassword", "/RESENDCONFIRMATIONEMAIL");

        Assert.All(endpoints, endpoint =>
            Assert.Contains(endpoint.Metadata, metadata => metadata is MailEndpointMetadata));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public void ARenamedRoute_IsReportedAsAnError()
    {
        // The failure mode this guards: a future ASP.NET version renames the route, the metadata is
        // never applied, and the tighter limit silently stops covering the endpoint.
        var logger = new CapturingLogger<IdentityMailEndpointConventionTests>();
        var builder = new FakeConventionBuilder();
        builder.RequireMailEndpointRateLimiting(logger);

        builder.Apply("/login", "/forgotPassword", "/resendConfirmation");

        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("/resendConfirmationEmail", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/forgotPassword,", error.Message, StringComparison.Ordinal);
    }

    private static bool IsTagged(IReadOnlyList<RouteEndpointBuilder> endpoints, string route) =>
        endpoints
            .Single(endpoint => endpoint.RoutePattern.RawText == route)
            .Metadata.Any(metadata => metadata is MailEndpointMetadata);
}
