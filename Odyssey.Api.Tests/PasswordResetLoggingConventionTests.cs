using Microsoft.Extensions.Logging;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The completion log has to be attached to one route inside a group Odyssey does not map (issue
/// #405). <c>MapIdentityApi&lt;TUser&gt;()</c> hands back a single convention builder for all of its
/// routes, so the obvious <c>AddEndpointFilter</c> would log "password reset completed" on every
/// successful <c>/login</c> and <c>/register</c> too — a logging-integrity regression inside the
/// change meant to harden logging. These pin the narrower attachment and its self-check.
/// </summary>
public class PasswordResetLoggingConventionTests
{
    [Fact]
    public void OnlyTheResetRoute_GetsTheFilter()
    {
        var logger = new CapturingLogger<PasswordResetLoggingConventionTests>();
        var builder = new FakeConventionBuilder();
        builder.LogPasswordResetCompletion(logger);

        var endpoints = builder.Apply("/login", "/register", "/manage/info", "/forgotPassword", "/resetPassword");

        Assert.Single(Filters(endpoints, "/resetPassword"));
        Assert.Empty(Filters(endpoints, "/login"));
        Assert.Empty(Filters(endpoints, "/register"));
        Assert.Empty(Filters(endpoints, "/manage/info"));
        Assert.Empty(Filters(endpoints, "/forgotPassword"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public void TheRouteIsMatchedCaseInsensitively()
    {
        var logger = new CapturingLogger<PasswordResetLoggingConventionTests>();
        var builder = new FakeConventionBuilder();
        builder.LogPasswordResetCompletion(logger);

        var endpoints = builder.Apply("/resetpassword");

        Assert.Single(Filters(endpoints, "/resetpassword"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public void ARenamedRoute_IsReportedAsAnError()
    {
        // The failure mode: a future ASP.NET version renames the route, the filter is never attached,
        // and completed resets stop being logged with nothing to notice it by.
        var logger = new CapturingLogger<PasswordResetLoggingConventionTests>();
        var builder = new FakeConventionBuilder();
        builder.LogPasswordResetCompletion(logger);

        builder.Apply("/login", "/forgotPassword", "/resetPasswordV2");

        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(PasswordResetLogging.ResetRoute, error.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<object> Filters(
        IEnumerable<Microsoft.AspNetCore.Routing.RouteEndpointBuilder> endpoints, string route) =>
        endpoints.Single(endpoint => endpoint.RoutePattern.RawText == route).FilterFactories;
}
