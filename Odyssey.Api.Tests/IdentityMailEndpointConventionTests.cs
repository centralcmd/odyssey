using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Odyssey.Api;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// <c>MapIdentityApi</c> maps its endpoints as one group, so the two mail-sending routes can only be
/// singled out by route text (issue #393). That makes the tighter limit hostage to a framework
/// rename, so a startup validator reports one — these tests pin both the tagging and the report.
///
/// <para>
/// The report is asserted against <em>built</em> endpoints, which is the point: it used to run from an
/// <c>IEndpointConventionBuilder.Finally</c> convention that was believed to fire once per group and
/// in fact fires once per endpoint, so it reported both routes missing on every boot while tagging
/// them correctly. <c>IdentityStartupGuardTests</c> pins the same properties against a real
/// <c>MapIdentityApi</c>, because a stand-in builder is what hid that.
/// </para>
/// </summary>
public class IdentityMailEndpointConventionTests
{
    [Fact]
    public void TheMailRoutes_AreTaggedAndNothingElseIs()
    {
        var builder = new FakeConventionBuilder();
        builder.RequireMailEndpointRateLimiting();

        var endpoints = builder.Apply("/login", "/register", "/forgotPassword", "/resendConfirmationEmail");

        Assert.False(IsTagged(endpoints, "/login"));
        Assert.False(IsTagged(endpoints, "/register"));
        Assert.True(IsTagged(endpoints, "/forgotPassword"));
        Assert.True(IsTagged(endpoints, "/resendConfirmationEmail"));
    }

    [Fact]
    public void TheRoutesAreMatchedCaseInsensitively()
    {
        var builder = new FakeConventionBuilder();
        builder.RequireMailEndpointRateLimiting();

        var endpoints = builder.Apply("/forgotpassword", "/RESENDCONFIRMATIONEMAIL");

        Assert.All(endpoints, endpoint =>
            Assert.Contains(endpoint.Metadata, metadata => metadata is MailEndpointMetadata));
    }

    [Fact]
    public void ARenamedRoute_IsReportedAsAnError()
    {
        // The failure mode this guards: a future ASP.NET version renames the route, the metadata is
        // never applied, and the tighter limit silently stops covering the endpoint.
        var logger = new CapturingLogger<IdentityMailEndpointConventionTests>();
        var builder = new FakeConventionBuilder();
        builder.RequireMailEndpointRateLimiting();

        var endpoints = builder.ApplyAndBuild("/login", "/forgotPassword", "/resendConfirmation");
        IdentityRateLimiting.ValidateMailEndpointRateLimiting(endpoints, logger);

        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("/resendConfirmationEmail", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/forgotPassword,", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The regression that shipped: with both routes present the validator must say nothing at all.
    /// Under the old <c>Finally</c>-based report this failed — it ran against <c>/login</c>, the first
    /// endpoint built, and named both routes as missing.
    /// </summary>
    [Fact]
    public void WithBothRoutesPresent_NothingIsReported()
    {
        var logger = new CapturingLogger<IdentityMailEndpointConventionTests>();
        var builder = new FakeConventionBuilder();
        builder.RequireMailEndpointRateLimiting();

        var endpoints = builder.ApplyAndBuild(
            "/register", "/login", "/resendConfirmationEmail", "/forgotPassword", "/resetPassword");
        IdentityRateLimiting.ValidateMailEndpointRateLimiting(endpoints, logger);

        Assert.Empty(logger.Entries);
    }

    private static bool IsTagged(IReadOnlyList<RouteEndpointBuilder> endpoints, string route) =>
        endpoints
            .Single(endpoint => endpoint.RoutePattern.RawText == route)
            .Metadata.Any(metadata => metadata is MailEndpointMetadata);
}
