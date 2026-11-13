using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Pins <see cref="TestHostCleanup"/>: a disposed <see cref="OdysseyApiFactory"/> host leaves no rate-limiter
/// timer running and carries no built-in logging provider, while a test's own provider survives.
/// </summary>
public class TestHostCleanupTests
{
    private static bool IsBuiltInLogging(Type type) =>
        type.Namespace is "Microsoft.Extensions.Logging.Console"
            or "Microsoft.Extensions.Logging.Debug"
            or "Microsoft.Extensions.Logging.EventSource"
            or "Microsoft.Extensions.Logging.EventLog";

    [Fact]
    public async Task Disposing_the_factory_disposes_the_global_rate_limiter()
    {
        var factory = new OdysseyApiFactory();
        using (factory.CreateClient())
        {
        }

        var limiter = factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter;
        Assert.NotNull(limiter);

        await factory.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => limiter.AttemptAcquire(new DefaultHttpContext()));
    }

    [Fact]
    public async Task The_pipeline_walk_finds_both_of_the_middlewares_limiters()
    {
        // If ASP.NET Core renames the middleware or its fields, the walk silently finds nothing and every
        // host leaks again; this fails instead.
        RequestDelegate? pipeline = null;
        await using var factory = new OdysseyApiFactory(configureServices: services =>
            services.AddSingleton<IStartupFilter>(new CapturePipeline(rest => pipeline = rest)));
        using var client = factory.CreateClient();

        Assert.NotNull(pipeline);
        Assert.Equal(2, TestHostCleanup.DisposeRateLimiters(pipeline));
    }

    [Fact]
    public async Task The_shared_factory_resolves_no_built_in_logging_provider()
    {
        await using var factory = new OdysseyApiFactory();

        Assert.DoesNotContain(factory.Services.GetServices<ILoggerProvider>(), p => IsBuiltInLogging(p.GetType()));
        Assert.DoesNotContain(
            factory.Services.GetServices<IOptionsChangeTokenSource<LoggerFilterOptions>>(),
            s => IsBuiltInLogging(s.GetType()));
    }

    [Fact]
    public async Task A_tests_own_logger_provider_survives_the_strip()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new OdysseyApiFactory(
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Contains(logs, factory.Services.GetServices<ILoggerProvider>());
    }

    private sealed class CapturePipeline(Action<RequestDelegate> capture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(rest =>
            {
                capture(rest);
                return rest;
            });
            next(app);
        };
    }
}
