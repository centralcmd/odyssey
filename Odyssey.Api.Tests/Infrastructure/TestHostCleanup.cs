using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Lets a disposed test host be collected. Without it each <see cref="OdysseyApiFactory"/> host stayed in
/// memory for the rest of the run: about 5 MB each, a few thousand factories, which filled the GC heap
/// (~10.8 GB) and got CI's runner killed for memory. Two things held them; both matter only because a
/// test process builds thousands of hosts, so production is unchanged.
///
/// <list type="bullet">
/// <item><description>
/// <b>The rate limiter's timer.</b> ASP.NET Core's rate-limiting middleware builds a partitioned limiter
/// for the endpoint policies and keeps <see cref="RateLimiterOptions.GlobalLimiter"/>. Each runs a 100 ms
/// heartbeat on the process-wide timer queue, and the middleware never disposes either (it is not
/// <see cref="IDisposable"/> and has no lifetime hook). The endpoint limiter's partitioner captures the
/// middleware, so the timer roots the whole request pipeline, and through it the host.
/// </description></item>
/// <item><description>
/// <b>The built-in logging providers.</b> The console provider starts a thread per host that captures the
/// startup's execution context, which carries the host builder; the dead thread keeps it until the
/// runtime reaps it. The event-source provider ties each host's filter options to the process-wide
/// <c>LoggingEventSource</c>.
/// </description></item>
/// </list>
///
/// <para>
/// Only <see cref="OdysseyApiFactory"/> applies this. The few standalone
/// <c>WebApplicationFactory&lt;Program&gt;</c> subclasses (for example <c>EmailConfirmationTests</c>) still
/// leak their hosts; they are a small share of the run and can call these two methods if that changes.
/// </para>
/// </summary>
internal static class TestHostCleanup
{
    /// <summary>The middleware <c>UseRateLimiter</c> adds. Internal, so it is found by name.</summary>
    private const string RateLimitingMiddlewareTypeName = "Microsoft.AspNetCore.RateLimiting.RateLimitingMiddleware";

    private static readonly string[] BuiltInLoggingNamespaces =
    [
        "Microsoft.Extensions.Logging.Console",
        "Microsoft.Extensions.Logging.Debug",
        "Microsoft.Extensions.Logging.EventSource",
        "Microsoft.Extensions.Logging.EventLog",
    ];

    /// <summary>
    /// The event-source provider's filter hooks, which tie each host's filter options to the process-wide
    /// <c>LoggingEventSource</c>. They are internal and sit in the root logging namespace, so they are named.
    /// </summary>
    internal static readonly string[] EventSourceFilterHookTypeNames =
    [
        "Microsoft.Extensions.Logging.EventLogFiltersConfigureOptions",
        "Microsoft.Extensions.Logging.EventLogFiltersConfigureOptionsChangeSource",
    ];

    /// <summary>
    /// Removes the providers <c>WebApplication.CreateBuilder</c> adds by default, matched by namespace, and the
    /// event-source filter hooks, so a test's own capturing <see cref="Microsoft.Extensions.Logging.ILoggerProvider"/>
    /// keeps working wherever it is registered. No test reads console, debug or event-source output.
    /// </summary>
    public static void RemoveBuiltInLoggingProviders(this IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var type = services[i].ImplementationType;
            if (type is not null
                && (BuiltInLoggingNamespaces.Contains(type.Namespace) || EventSourceFilterHookTypeNames.Contains(type.FullName)))
            {
                services.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Disposes the rate-limiting middleware's limiters when the host stops. A startup filter put first in
    /// the pipeline sees the rest of it once built, and passes it through untouched.
    /// </summary>
    public static void DisposeRateLimitersOnStop(this IServiceCollection services) =>
        services.AddSingleton<IStartupFilter, RateLimiterDisposalFilter>();

    /// <summary>
    /// Finds every rate-limiting middleware reachable from <paramref name="pipeline"/> and disposes its
    /// limiters. Returns how many limiters it disposed, so a test can pin that the walk still finds them.
    /// </summary>
    internal static int DisposeRateLimiters(RequestDelegate pipeline)
    {
        var disposed = 0;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        pending.Push(pipeline);

        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node))
            {
                continue;
            }

            if (node is Delegate @delegate)
            {
                if (@delegate.Target is { } target)
                {
                    pending.Push(target);
                }

                continue;
            }

            var isRateLimiting = node.GetType().FullName == RateLimitingMiddlewareTypeName;
            foreach (var field in InstanceFields(node.GetType()))
            {
                var value = field.GetValue(node);
                // The next link is a RequestDelegate, or, through UseMiddleware's binder, the middleware
                // instance held as an object.
                if (value is RequestDelegate || (value is not null && field.FieldType == typeof(object)))
                {
                    pending.Push(value);
                }
                else if (isRateLimiting && value is PartitionedRateLimiter<HttpContext> limiter)
                {
                    limiter.Dispose();
                    disposed++;
                }
            }
        }

        return disposed;
    }

    // A private field of a base class is not returned for the derived type: AuthorizationMiddlewareInternal
    // keeps its next link on AuthorizationMiddleware.
    private static IEnumerable<FieldInfo> InstanceFields(Type? type)
    {
        for (; type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                yield return field;
            }
        }
    }

    private sealed class RateLimiterDisposalFilter(IHostApplicationLifetime lifetime) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(rest =>
            {
                lifetime.ApplicationStopped.Register(() => DisposeRateLimiters(rest));
                return rest;
            });
            next(app);
        };
    }
}
