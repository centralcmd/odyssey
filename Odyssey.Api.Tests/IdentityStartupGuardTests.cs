using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Odyssey.Api;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The two Identity startup guards, exercised against the endpoint set a <b>real</b>
/// <c>MapIdentityApi</c> produces rather than against a stand-in convention builder.
/// </summary>
/// <remarks>
/// <para>
/// This file exists because of a defect the convention-level tests could not see. Both guards used to
/// accumulate route matches in an <c>IEndpointConventionBuilder.Add</c> convention and report the
/// misses from a <c>Finally</c> one, on the reading that <c>Finally</c> runs after the whole group has
/// been walked. <c>RouteEndpointDataSource</c> instead applies a group's conventions and then its
/// finally conventions <em>per endpoint</em>, so both reports ran against <c>/register</c> — the first
/// endpoint built — found nothing matched yet, and logged
/// <c>fail: Identity mail endpoints … were not found</c> and <c>fail: Identity route /resetPassword
/// was not found</c> on every boot of every deployment. The tagging and the filter were being applied
/// correctly the whole time, so the protections were never actually missing; the guard was.
/// </para>
/// <para>
/// The cost was not cosmetic. Both messages tell an operator that an auth-surface control has silently
/// degraded, and both were false. A guard that fires on every boot is one operators learn to skip,
/// which is precisely the state in which the real framework rename would go unnoticed — so these
/// assert the quiet case as carefully as the noisy one.
/// </para>
/// </remarks>
public class IdentityStartupGuardTests
{
    [Fact]
    public void ACleanBoot_ReportsNoMissingIdentityRoutes()
    {
        using var factory = new ApiFactory();

        _ = factory.Services;

        Assert.Empty(GuardErrors(factory));
    }

    [Fact]
    public void TheMailRoutes_AreTaggedOnTheRealIdentityGroup()
    {
        using var factory = new ApiFactory();

        var tagged = RouteEndpoints(factory)
            .Where(endpoint => endpoint.Metadata.GetMetadata<MailEndpointMetadata>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();

        Assert.Equal(
            IdentityRateLimiting.MailEndpointRoutes.OrderBy(route => route, StringComparer.OrdinalIgnoreCase),
            tagged.OrderBy(route => route, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The completion filter is folded into the request delegate and leaves nothing on the endpoint,
    /// so <see cref="PasswordResetLoggingMetadata"/> is what makes the attachment observable. That it
    /// lands on <c>/resetPassword</c> and on nothing else is the same property
    /// <c>PasswordResetLoggingConventionTests</c> asserts about the filter itself.
    /// </summary>
    [Fact]
    public void OnlyTheResetRoute_IsMarkedForCompletionLogging()
    {
        using var factory = new ApiFactory();

        var marked = RouteEndpoints(factory)
            .Where(endpoint => endpoint.Metadata.GetMetadata<PasswordResetLoggingMetadata>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();

        Assert.Equal([PasswordResetLogging.ResetRoute], marked);
    }

    private static IEnumerable<RouteEndpoint> RouteEndpoints(ApiFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    private static IEnumerable<CapturingLoggerProvider.Entry> GuardErrors(ApiFactory factory) =>
        factory.Logs.Entries.Where(entry =>
            entry.Level >= LogLevel.Error
            && (entry.Category == typeof(IdentityRateLimiting).FullName
                || entry.Category == typeof(PasswordResetLogging).FullName));

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"IdentityStartupGuardTests-{Guid.NewGuid()}";

        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["UseInMemoryDatabase"] = "true" }));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options =>
                    options.UseInMemoryDatabase(databaseName));
                services.AddSingleton<ILoggerProvider>(Logs);
            });
        }
    }
}
