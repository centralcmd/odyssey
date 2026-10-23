using System.Threading.Channels;
using Odyssey.Core.Finance.ExchangeRateSync;

namespace Odyssey.Api.ExchangeRateSync;

/// <summary>Hands a started run to whatever executes it (issue #349 §3 "Scheduler lifecycle").</summary>
public interface IExchangeRateSyncDispatcher
{
    /// <summary>Queues a run that is already <c>Running</c>. Returns once it is queued — or, inline, done.</summary>
    Task DispatchAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>
/// The production dispatcher: an in-process channel the <see cref="ExchangeRateSyncScheduler"/> reads.
/// A singleton shared by the controller and the hosted service.
/// </summary>
public sealed class ChannelExchangeRateSyncDispatcher : IExchangeRateSyncDispatcher
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<Guid> Reader => channel.Reader;

    public Task DispatchAsync(Guid runId, CancellationToken cancellationToken) =>
        channel.Writer.WriteAsync(runId, cancellationToken).AsTask();
}

/// <summary>
/// The <c>Testing</c> dispatcher: executes the run before the request returns, so a test's
/// <c>POST …/runs</c> never leaves a <c>Running</c> row holding the guard.
/// </summary>
public sealed class InlineExchangeRateSyncDispatcher(IServiceScopeFactory scopes) : IExchangeRateSyncDispatcher
{
    public async Task DispatchAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ExchangeRateSyncService>().ExecuteAsync(runId, cancellationToken);
    }
}

/// <summary>
/// The exchange-rate scheduler — the first <see cref="BackgroundService"/> in <c>Odyssey.Api</c> (issue
/// #349 §3). It keeps no schedule in memory: every minute it asks <see cref="ExchangeRateSyncService"/>
/// whether a run is due, which derives the answer from the run log, and between ticks it executes any
/// run-now the controller queued. Not registered under <c>Testing</c>.
/// </summary>
/// <remarks>
/// The host's stopping token is passed through, so a shutdown ends a run <c>Failed / Cancelled</c> where
/// the run can still record it; if the process dies first, the next tick marks it <c>Abandoned</c>.
/// </remarks>
public sealed class ExchangeRateSyncScheduler(
    ChannelExchangeRateSyncDispatcher dispatcher,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<ExchangeRateSyncScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? queued = null;
            using (var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                tick.CancelAfter(ExchangeRateSyncConstants.SchedulerTick);
                try
                {
                    if (await dispatcher.Reader.WaitToReadAsync(tick.Token) && dispatcher.Reader.TryRead(out var runId))
                    {
                        queued = runId;
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The tick elapsed with nothing queued.
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<ExchangeRateSyncService>();
                if (queued is { } runId)
                {
                    await sync.ExecuteAsync(runId, stoppingToken);
                }
                else
                {
                    await sync.RunScheduledIfDueAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // One bad tick must not stop the scheduler; the next tick re-derives everything.
                logger.LogError(exception, "The exchange-rate sync tick failed at {Now}.", timeProvider.GetUtcNow());
            }
        }
    }
}
