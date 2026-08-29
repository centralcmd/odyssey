using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Direct coverage of <see cref="ExportConcurrencyFilter"/> — the global 4-permit ceiling shared by all
/// five bulk-export endpoints (issue #343 §5/§12). Nothing referenced this type before (PR #403
/// test-review finding); permit exhaustion is deterministically testable by pre-acquiring the shared
/// <see cref="GlobalExportConcurrencyLimiter"/>'s permits directly, without any real concurrency.
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
}
