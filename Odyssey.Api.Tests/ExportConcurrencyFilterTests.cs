using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Api.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Testing.Logging;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Direct coverage of <see cref="ExportConcurrencyFilter"/> — the global 4-permit ceiling shared by every
/// bulk export (issue #343 §5/§12), and the progress guard that replaced its 10-minute deadline (issue #516 §3
/// component 8, AC 18). Permit exhaustion is deterministically testable by pre-acquiring the shared
/// <see cref="GlobalExportConcurrencyLimiter"/>'s permits directly; the guard runs against a fake clock and a
/// response body the test controls, so a "slow client" is a write that never completes.
/// </summary>
public class ExportConcurrencyFilterTests
{
    private static ResourceExecutingContext CreateContext()
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        return new ResourceExecutingContext(
            actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());
    }

    private static Task<ResourceExecutedContext> Completed(ResourceExecutingContext context) =>
        Task.FromResult(new ResourceExecutedContext(context, context.Filters));

    [Fact]
    public async Task OnResourceExecutionAsync_PermitAvailable_CallsNext_AndReleasesItsPermit()
    {
        var global = new GlobalExportConcurrencyLimiter();
        var filter = new ExportConcurrencyFilter(global, NullLogger<ExportConcurrencyFilter>.Instance);
        var context = CreateContext();
        var nextCalled = false;

        await filter.OnResourceExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Completed(context);
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
        // The lease was disposed (via `using`) once the filter returned — all 4 permits are free again,
        // proving this run didn't leak one.
        Assert.Equal(4, global.Limiter.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public async Task OnResourceExecutionAsync_AllFourGlobalPermitsHeld_Returns429_NeverCallsNext()
    {
        var global = new GlobalExportConcurrencyLimiter();
        using var lease1 = global.Limiter.AttemptAcquire();
        using var lease2 = global.Limiter.AttemptAcquire();
        using var lease3 = global.Limiter.AttemptAcquire();
        using var lease4 = global.Limiter.AttemptAcquire();
        Assert.True(lease1.IsAcquired && lease2.IsAcquired && lease3.IsAcquired && lease4.IsAcquired);

        var filter = new ExportConcurrencyFilter(global, NullLogger<ExportConcurrencyFilter>.Instance);
        var context = CreateContext();
        var nextCalled = false;

        await filter.OnResourceExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Completed(context);
        });

        Assert.False(nextCalled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, result.StatusCode);
        Assert.Equal("application/problem+json", Assert.Single(result.ContentTypes));
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.Status);
    }

    [Fact]
    public async Task OnResourceExecutionAsync_OneOfFourPermitsFree_StillCallsNext()
    {
        // Boundary check on the exhaustion test above — 3 held, 1 free, must still admit the request.
        var global = new GlobalExportConcurrencyLimiter();
        using var lease1 = global.Limiter.AttemptAcquire();
        using var lease2 = global.Limiter.AttemptAcquire();
        using var lease3 = global.Limiter.AttemptAcquire();
        Assert.True(lease1.IsAcquired && lease2.IsAcquired && lease3.IsAcquired);

        var filter = new ExportConcurrencyFilter(global, NullLogger<ExportConcurrencyFilter>.Instance);
        var context = CreateContext();
        var nextCalled = false;

        await filter.OnResourceExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Completed(context);
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    // ------------------------------------------------------------------ progress guard (issue #516 AC 18)

    private const int MiB = 1024 * 1024;

    [Fact]
    public async Task ProgressGuard_NoFirstByteWithinTenMinutes_Aborts()
    {
        var (filter, context, clock, lifetime, _) = Guarded();

        await filter.OnResourceExecutionAsync(context, () =>
        {
            // Planning that never reaches its first byte.
            clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(50));
            Assert.False(lifetime.Aborted);
            clock.Advance(TimeSpan.FromSeconds(15));
            return Completed(context);
        });

        Assert.True(lifetime.Aborted);
    }

    [Fact]
    public async Task ProgressGuard_OneByteTrickle_IsAbortedWithinAWindow()
    {
        var (filter, context, clock, lifetime, body) = Guarded();

        await filter.OnResourceExecutionAsync(context, async () =>
        {
            await context.HttpContext.Response.Body.WriteAsync(new byte[1]);
            // The client stops reading: the next write waits on it, and a byte trickles through each minute.
            body.Block();
            var pending = context.HttpContext.Response.Body.WriteAsync(new byte[MiB]).AsTask();
            for (var minute = 0; minute < 6 && !lifetime.Aborted; minute++)
            {
                body.Release(1);
                clock.Advance(TimeSpan.FromMinutes(1));
            }

            Assert.False(pending.IsCompletedSuccessfully);
            return await Completed(context);
        });

        Assert.True(lifetime.Aborted);
    }

    [Fact]
    public async Task ProgressGuard_UnderOneMibInAWindow_WithAWritePending_Aborts()
    {
        var (filter, context, clock, lifetime, body) = Guarded();

        await filter.OnResourceExecutionAsync(context, async () =>
        {
            await context.HttpContext.Response.Body.WriteAsync(new byte[1]);
            body.Block();
            _ = context.HttpContext.Response.Body.WriteAsync(new byte[2 * MiB]).AsTask();
            body.Release(MiB / 2); // half a MiB in the next five minutes
            clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(15));
            return await Completed(context);
        });

        Assert.True(lifetime.Aborted);
    }

    [Theory]
    [InlineData(0, false)] // exactly 1 MiB in the window: at the floor, not under it
    [InlineData(1, true)]  // one byte short of it
    public async Task ProgressGuard_TheOneMibFloor_IsInclusive(int shortBy, bool aborted)
    {
        var (filter, context, clock, lifetime, body) = Guarded();

        await filter.OnResourceExecutionAsync(context, async () =>
        {
            // The first byte opens the window and counts in it, so the window holds 1 + (MiB - 1 - shortBy) bytes.
            await context.HttpContext.Response.Body.WriteAsync(new byte[1]);
            await context.HttpContext.Response.Body.WriteAsync(new byte[MiB - 1 - shortBy]);
            body.Block();
            _ = context.HttpContext.Response.Body.WriteAsync(new byte[MiB]).AsTask();
            clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(15));
            return await Completed(context);
        });

        Assert.Equal(aborted, lifetime.Aborted);
    }

    [Fact]
    public async Task ProgressGuard_TenMibPerMinuteForAnHour_IsNotAborted()
    {
        var (filter, context, clock, lifetime, _) = Guarded();

        await filter.OnResourceExecutionAsync(context, async () =>
        {
            for (var minute = 0; minute < 60; minute++)
            {
                await context.HttpContext.Response.Body.WriteAsync(new byte[10 * MiB]);
                clock.Advance(TimeSpan.FromMinutes(1));
            }

            return await Completed(context);
        });

        Assert.False(lifetime.Aborted);
    }

    [Fact]
    public async Task ProgressGuard_AWindowEndingWithNoWritePending_IsTheServerProducing_NotAStall()
    {
        // A small export whose client read it all: nothing is waiting on the client, so the floor does not apply.
        var (filter, context, clock, lifetime, _) = Guarded();

        await filter.OnResourceExecutionAsync(context, async () =>
        {
            await context.HttpContext.Response.Body.WriteAsync(new byte[100 * 1024]);
            clock.Advance(TimeSpan.FromMinutes(12));
            return await Completed(context);
        });

        Assert.False(lifetime.Aborted);
    }

    [Fact]
    public async Task ProgressGuard_AnyExport_IsAbortedAtTwentyFourHours()
    {
        var (filter, context, clock, lifetime, _) = Guarded();

        await filter.OnResourceExecutionAsync(context, async () =>
        {
            var elapsed = TimeSpan.Zero;
            while (!lifetime.Aborted && elapsed < TimeSpan.FromHours(25))
            {
                await context.HttpContext.Response.Body.WriteAsync(new byte[2 * MiB]);
                clock.Advance(TimeSpan.FromMinutes(1));
                elapsed += TimeSpan.FromMinutes(1);
            }

            Assert.InRange(elapsed, TimeSpan.FromHours(24), TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
            return await Completed(context);
        });

        Assert.True(lifetime.Aborted);
    }

    [Fact]
    public async Task ProgressGuard_Abort_LogsTheRouteTemplate_NeverTheCallerSuppliedPath()
    {
        // CodeQL "log entries created from user input": the request path is the caller's, the template is the server's.
        var logger = new RecordingLogger<ExportConcurrencyFilter>();
        var (filter, context, clock, lifetime, _) = Guarded(logger);
        context.HttpContext.Request.Path = "/api/calendars/x%0AFORGED/ics";
        context.HttpContext.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask, RoutePatternFactory.Parse("api/calendars/{id:guid}/ics"), 0, null, "calendar export"));

        await filter.OnResourceExecutionAsync(context, () =>
        {
            clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(15));
            return Completed(context);
        });

        Assert.True(lifetime.Aborted);
        var line = Assert.Single(logger.Messages);
        Assert.Contains("api/calendars/{id:guid}/ics", line);
        Assert.DoesNotContain("FORGED", line);
    }

    [Fact]
    public void EndpointName_FallsBackToTheDisplayName_ThenToAPlaceholder()
    {
        var context = new DefaultHttpContext();
        Assert.Equal("(unknown endpoint)", ExportConcurrencyFilter.EndpointName(context));

        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, null, "Some.Controller.Export"));
        Assert.Equal("Some.Controller.Export", ExportConcurrencyFilter.EndpointName(context));
    }

    private static (ExportConcurrencyFilter Filter, ResourceExecutingContext Context, FakeTimeProvider Clock,
        RecordingLifetime Lifetime, GatedBody Body) Guarded(ILogger<ExportConcurrencyFilter>? logger = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var filter = new ExportConcurrencyFilter(
            new GlobalExportConcurrencyLimiter(), logger ?? NullLogger<ExportConcurrencyFilter>.Instance, clock);
        var context = CreateContext();
        var lifetime = new RecordingLifetime();
        var body = new GatedBody();
        context.HttpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.HttpContext.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        return (filter, context, clock, lifetime, body);
    }

    private sealed class RecordingLifetime : IHttpRequestLifetimeFeature
    {
        private readonly CancellationTokenSource aborted = new();

        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted
        {
            get => aborted.Token;
            set { }
        }

        public void Abort()
        {
            Aborted = true;
            aborted.Cancel();
        }
    }

    /// <summary>
    /// A response body standing in for the client: writes complete at once until <see cref="Block"/>, after which a
    /// write completes only as <see cref="Release"/> lets bytes through — a client that is not reading.
    /// </summary>
    private sealed class GatedBody : Stream
    {
        private readonly SemaphoreSlim credit = new(0);
        private bool blocked;

        public void Block() => blocked = true;

        public void Release(int bytes) => credit.Release(bytes);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!blocked)
            {
                return;
            }

            for (var i = 0; i < buffer.Length; i++)
            {
                await credit.WaitAsync(cancellationToken);
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }
}
