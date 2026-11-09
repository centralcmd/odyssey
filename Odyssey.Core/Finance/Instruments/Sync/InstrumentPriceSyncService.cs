using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Core.Jobs;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.Instruments.Sync;

/// <summary>The sync's fixed limits (issue #446 §4.5): constants, sent on the status DTO so the client holds no copy.</summary>
public static class InstrumentPriceSyncConstants
{
    public static readonly TimeSpan SchedulerTick = TimeSpan.FromSeconds(15);

    public const int CatchUpDelayMinutes = 5;

    public const int RunTimeoutMinutes = 10;

    public const int BackoffAfterFailures = 3;

    public const int MaxBackoffHours = 168;

    public const int ManualRunCooldownSeconds = 60;

    /// <summary>Consecutive <c>ProviderUnavailable</c> results in one run that back a source off.</summary>
    public const int UnavailableBackoffThreshold = 3;

    public static readonly TimeSpan UnavailableBackoff = TimeSpan.FromHours(1);

    public static readonly TimeSpan RateLimitDefaultBackoff = TimeSpan.FromHours(1);

    public static readonly TimeSpan RateLimitMinBackoff = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan RateLimitMaxBackoff = TimeSpan.FromHours(24);
}

/// <summary>
/// The instrument-price sync (issue #446 §3), modelled on <c>ExchangeRateSyncService</c>: a scheduled run is due
/// <c>InstrumentPriceSyncIntervalHours</c> after the last, a catch-up five minutes after a run that inserted rows
/// while any binding is still behind, and the interval doubles after three failed runs (capped at 168 h). With no
/// enabled binding to a registered source it writes nothing at all. Each binding forward-fills from its newest
/// fetched date to today, then backfills toward its start date; a failure falls back to the next binding.
/// </summary>
public sealed class InstrumentPriceSyncService(
    OdysseyContext context,
    InstrumentPriceSourceCatalogue sources,
    IInstrumentPriceSettingsLookup settings,
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    JobRunRecorder recorder,
    TimeProvider timeProvider,
    ILogger<InstrumentPriceSyncService> logger)
{
    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Whether any non-archived instrument has an enabled binding to a registered source.</summary>
    public async Task<bool> HasWorkAsync(CancellationToken cancellationToken = default)
    {
        if (sources.IsEmpty)
        {
            return false;
        }

        var keys = sources.All.Select(s => s.Key).ToList();
        return await context.InstrumentPriceSourceBindings.AsNoTracking()
            .AnyAsync(b => b.Enabled && keys.Contains(b.SourceKey)
                && context.Instruments.Any(i => i.InstrumentId == b.InstrumentId && i.Archived == null), cancellationToken);
    }

    /// <summary>When the next scheduled run is due, or <c>null</c> when there is nothing to sync.</summary>
    public async Task<DateTime?> NextRunAtUtcAsync(CancellationToken cancellationToken = default)
    {
        if (!await HasWorkAsync(cancellationToken))
        {
            return null;
        }

        var recent = await context.Set<InstrumentPriceSyncJobRun>().AsNoTracking()
            .Where(r => r.Trigger != JobRunTrigger.Purge && r.FinishedAtUtc != null && r.Status != JobRunStatus.Skipped)
            .OrderByDescending(r => r.FinishedAtUtc)
            .Take(InstrumentPriceSyncConstants.BackoffAfterFailures + 8)
            .ToListAsync(cancellationToken);
        if (recent.Count == 0)
        {
            return Now;
        }

        var last = recent[0];
        var finished = last.FinishedAtUtc!.Value;
        var interval = TimeSpan.FromHours((await settings.GetAsync(cancellationToken)).SyncIntervalHours);
        var failures = recent.TakeWhile(r => r.Status == JobRunStatus.Failed).Count();
        if (failures >= InstrumentPriceSyncConstants.BackoffAfterFailures)
        {
            var hours = Math.Min(interval.TotalHours * Math.Pow(2, failures - InstrumentPriceSyncConstants.BackoffAfterFailures + 1),
                InstrumentPriceSyncConstants.MaxBackoffHours);
            return finished + TimeSpan.FromHours(hours);
        }

        if (last.PricesInserted > 0 && await AnyBindingBehindAsync(cancellationToken))
        {
            return finished + TimeSpan.FromMinutes(InstrumentPriceSyncConstants.CatchUpDelayMinutes);
        }

        return finished + interval;
    }

    public Task<Guid?> ClaimRequestedRunAsync(CancellationToken cancellationToken = default) =>
        recorder.ClaimManualAsync(JobType.InstrumentPriceSync, cancellationToken);

    /// <summary>The worker's tick: starts and executes a scheduled run when one is due; with nothing enabled, writes nothing.</summary>
    public async Task<Guid?> RunScheduledIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (await NextRunAtUtcAsync(cancellationToken) is not { } next || next > Now)
        {
            return null;
        }

        if (await context.JobRuns.AnyAsync(r => r.JobType == JobType.InstrumentPriceSync && r.Status == JobRunStatus.Running, cancellationToken))
        {
            return null;
        }

        var now = Now;
        var run = new InstrumentPriceSyncJobRun
        {
            JobRunId = Guid.NewGuid(),
            Trigger = JobRunTrigger.Scheduled,
            Status = JobRunStatus.Running,
            StartedAtUtc = now,
            ClaimedAtUtc = now,
            LastActivityAtUtc = now,
            WorkerId = recorder.WorkerId,
        };
        context.JobRuns.Add(run);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbErrors.IsDuplicateKey(exception))
        {
            // Another replica took the ExclusiveGuard first.
            context.Entry(run).State = EntityState.Detached;
            return null;
        }

        context.Entry(run).State = EntityState.Detached;
        await ExecuteAsync(run.JobRunId, cancellationToken);
        return run.JobRunId;
    }

    /// <summary>Executes a claimed run end to end and finishes its row.</summary>
    public async Task ExecuteAsync(Guid runId, CancellationToken cancellationToken)
    {
        var counters = new RunCounters();
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cap.CancelAfter(TimeSpan.FromMinutes(InstrumentPriceSyncConstants.RunTimeoutMinutes));
        (JobRunStatus, JobFailureCategory?)? outcome = null;
        try
        {
            await SyncAsync(counters, cap.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = (JobRunStatus.Failed, JobFailureCategory.Cancelled);
        }
        catch (Exception exception)
        {
            logger.LogError("Instrument-price sync run {RunId} failed ({Failure}).", runId, exception.GetType().Name);
            outcome = (JobRunStatus.Failed, JobFailureCategory.Internal);
        }

        outcome ??= counters.Outcome();
        await recorder.FinishAsync<InstrumentPriceSyncJobRun>(JobType.InstrumentPriceSync, runId, counters.ApplyTo, outcome, CancellationToken.None);
    }

    private async Task<bool> AnyBindingBehindAsync(CancellationToken cancellationToken)
    {
        var today = OsloDate.Today(timeProvider);
        var history = (await settings.GetAsync(cancellationToken)).SyncHistoryDays;
        var keys = sources.All.Select(s => s.Key).ToList();
        var bindings = await context.InstrumentPriceSourceBindings.AsNoTracking()
            .Where(b => b.Enabled && keys.Contains(b.SourceKey))
            .ToListAsync(cancellationToken);
        return bindings.Any(b => b.NewestFetched is null || b.NewestFetched < today
            || b.BackfilledFrom is null || b.BackfilledFrom > StartOf(b, today, history));
    }

    private static DateOnly StartOf(InstrumentPriceSourceBinding binding, DateOnly today, int historyDays) =>
        binding.HistoryStartDate ?? today.AddDays(-historyDays);

    private sealed class SourceRunState(IInstrumentPriceSource source)
    {
        public IInstrumentPriceSource Source { get; } = source;

        public bool Unusable { get; set; }

        public bool Unconfigured { get; set; }

        public bool Capped { get; set; }

        public int Requests { get; set; }

        public int ForwardRequests { get; set; }

        public int ConsecutiveUnavailable { get; set; }

        public bool BudgetLeft => Requests < Source.Limits.MaxRequestsPerRun;

        public bool ForwardBudgetLeft => ForwardRequests < Math.Max(1, (Source.Limits.MaxRequestsPerRun + 1) / 2);
    }

    private async Task SyncAsync(RunCounters counters, CancellationToken cancellationToken)
    {
        var resolved = await settings.GetAsync(cancellationToken);
        var today = OsloDate.Today(timeProvider);
        var now = Now;

        var keys = sources.All.Select(s => s.Key).ToList();
        var bindings = await context.InstrumentPriceSourceBindings
            .Where(b => b.Enabled && keys.Contains(b.SourceKey))
            .OrderBy(b => b.InstrumentId).ThenBy(b => b.Priority)
            .ToListAsync(cancellationToken);
        var instrumentIds = bindings.Select(b => b.InstrumentId).Distinct().ToList();
        var instruments = await context.Instruments.AsNoTracking()
            .Where(i => instrumentIds.Contains(i.InstrumentId) && i.Archived == null)
            .ToDictionaryAsync(i => i.InstrumentId, cancellationToken);

        var states = await context.InstrumentPriceSourceStates.Where(s => keys.Contains(s.SourceKey)).ToDictionaryAsync(s => s.SourceKey, cancellationToken);
        var runStates = new Dictionary<string, SourceRunState>(StringComparer.Ordinal);
        foreach (var source in sources.All)
        {
            var runState = new SourceRunState(source);
            if (states.TryGetValue(source.Key, out var stored) && stored.BackOffUntilUtc > now)
            {
                runState.Unusable = true;
            }

            if (source.SecretKeys.Count > 0)
            {
                await using var scope = scopes.CreateAsyncScope();
                var secret = await scope.ServiceProvider.GetRequiredService<ISecretSettingsReader>().GetAsync(source.SecretKeys[0], cancellationToken);
                if (secret.State == SecretReadState.NotSet)
                {
                    runState.Unconfigured = true;
                }
                else if (secret.State != SecretReadState.Found)
                {
                    runState.Unusable = true;
                    counters.Fail(JobFailureCategory.Configuration);
                }
            }

            runStates[source.Key] = runState;
        }

        foreach (var group in bindings.GroupBy(b => b.InstrumentId))
        {
            if (!instruments.TryGetValue(group.Key, out var instrument))
            {
                continue;
            }

            var attemptedAny = false;
            var succeeded = false;
            foreach (var binding in group)
            {
                var runState = runStates[binding.SourceKey];
                if (runState.Unusable || runState.Unconfigured || runState.Capped || !runState.BudgetLeft
                    || !runState.Source.SupportedTypes.Contains(instrument.Type))
                {
                    continue;
                }

                if (attemptedAny)
                {
                    counters.FallbacksUsed++;
                }

                attemptedAny = true;
                var result = await SyncBindingAsync(instrument, binding, runState, today, resolved.SyncHistoryDays, counters, cancellationToken);
                if (result)
                {
                    succeeded = true;
                    break;
                }
            }

            if (!attemptedAny)
            {
                continue;
            }

            counters.InstrumentsAttempted++;
            if (succeeded)
            {
                counters.InstrumentsSucceeded++;
            }
            else
            {
                counters.InstrumentsFailed++;
            }
        }

        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Forward fill then one backfill span for one binding; <c>true</c> on a valid batch.</summary>
    private async Task<bool> SyncBindingAsync(
        Instrument instrument, InstrumentPriceSourceBinding binding, SourceRunState runState, DateOnly today, int historyDays,
        RunCounters counters, CancellationToken cancellationToken)
    {
        var maxDays = Math.Max(1, runState.Source.Limits.MaxDaysPerRequest);
        var start = StartOf(binding, today, historyDays);
        var anySuccess = false;

        // Forward fill, oldest span first so NewestFetched stays a contiguous watermark.
        while (runState.BudgetLeft && runState.ForwardBudgetLeft && !runState.Unusable && !runState.Capped)
        {
            var from = binding.NewestFetched is { } newest ? newest.AddDays(1) : MaxDate(start, today.AddDays(-(maxDays - 1)));
            if (from > today)
            {
                anySuccess = true;
                break;
            }

            var to = MinDate(today, from.AddDays(maxDays - 1));
            runState.ForwardRequests++;
            var ok = await FetchSpanAsync(instrument, binding, runState, from, to, counters, cancellationToken);
            if (!ok)
            {
                return anySuccess;
            }

            anySuccess = true;
            binding.NewestFetched = to;
            binding.BackfilledFrom ??= from;
        }

        // Backfill, newest span first; BackfilledFrom only advances across complete spans.
        if (anySuccess && binding.BackfilledFrom is { } backfilled && backfilled > start
            && runState.BudgetLeft && !runState.Unusable && !runState.Capped)
        {
            var to = backfilled.AddDays(-1);
            var from = MaxDate(start, to.AddDays(-(maxDays - 1)));
            if (await FetchSpanAsync(instrument, binding, runState, from, to, counters, cancellationToken))
            {
                binding.BackfilledFrom = from;
            }
        }

        return anySuccess;
    }

    private async Task<bool> FetchSpanAsync(
        Instrument instrument, InstrumentPriceSourceBinding binding, SourceRunState runState, DateOnly from, DateOnly to,
        RunCounters counters, CancellationToken cancellationToken)
    {
        var source = runState.Source;
        var request = new SourceFetchRequest(binding.SourceSymbol, from, to, instrument.CurrencyCode);
        await using var scope = scopes.CreateAsyncScope();
        var http = new SourceHttp(
            source,
            httpClients.CreateClient(SourceHttpLimits.ClientName(source.Key)),
            scope.ServiceProvider.GetRequiredService<ISecretSettingsReader>(),
            scope.ServiceProvider.GetRequiredService<ISourceRequestQuota>(),
            logger,
            timeProvider);

        SourceFetchResult result;
        try
        {
            result = await source.FetchClosesAsync(request, http, cancellationToken);
        }
        catch (SourceQuotaExhaustedException)
        {
            runState.Capped = true;
            return false;
        }
        catch (SourceCredentialException credential)
        {
            if (credential.State == SecretReadState.NotSet)
            {
                runState.Unconfigured = true;
                return false;
            }

            result = SourceFetchResult.Failed(JobFailureCategory.Configuration);
        }
        catch (SourceDestinationException)
        {
            result = SourceFetchResult.Failed(JobFailureCategory.Configuration);
        }
        catch (SourceResponseMalformedException)
        {
            result = SourceFetchResult.Failed(JobFailureCategory.ProviderMalformed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Price source {SourceKey} threw for instrument {InstrumentId} ({Failure}).",
                source.Key, instrument.InstrumentId, exception.GetType().Name);
            result = SourceFetchResult.Failed(JobFailureCategory.Internal);
        }
        finally
        {
            runState.Requests += http.RequestsMade;
            counters.RequestsMade += http.RequestsMade;
        }

        if (result.Succeeded && FetchedBatchCheck.Check(result.Closes!, request, instrument.CurrencyCode, OsloDate.Today(timeProvider)) is { } rejected)
        {
            logger.LogWarning("Price source {SourceKey} batch for instrument {InstrumentId} rejected by rule {Rule}.",
                source.Key, instrument.InstrumentId, rejected.Rule);
            result = SourceFetchResult.Failed(rejected.Category);
        }

        if (!result.Succeeded)
        {
            await RecordFailureAsync(binding, runState, result, counters, cancellationToken);
            return false;
        }

        counters.PricesInserted += await InsertAsync(instrument.InstrumentId, source.Key, result.Closes!, cancellationToken);
        runState.ConsecutiveUnavailable = 0;
        binding.LastSuccessUtc = Now;
        binding.ConsecutiveFailures = 0;
        binding.LastFailure = null;
        return true;
    }

    private async Task<int> InsertAsync(Guid instrumentId, string sourceKey, IReadOnlyList<FetchedClose> closes, CancellationToken cancellationToken)
    {
        if (closes.Count == 0)
        {
            return 0;
        }

        await using var scope = scopes.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await InstrumentLocks.InTransactionAsync(writer, async () =>
        {
            await InstrumentLocks.LockAsync(writer, instrumentId, cancellationToken);
            var dates = closes.Select(c => c.TradingDate).ToList();
            var existing = await writer.InstrumentPrices.AsNoTracking()
                .Where(p => p.InstrumentId == instrumentId && p.SourceKey == sourceKey && dates.Contains(p.TradingDate))
                .Select(p => p.TradingDate)
                .ToListAsync(cancellationToken);
            var fresh = closes.Where(c => !existing.Contains(c.TradingDate)).ToList();
            foreach (var close in fresh)
            {
                writer.InstrumentPrices.Add(new InstrumentPrice
                {
                    InstrumentPriceId = Guid.NewGuid(),
                    InstrumentId = instrumentId,
                    TradingDate = close.TradingDate,
                    Close = close.Close,
                    SourceKey = sourceKey,
                    CreatedAtUtc = Now,
                });
            }

            await writer.SaveChangesAsync(cancellationToken);
            return fresh.Count;
        }, cancellationToken);
    }

    private async Task RecordFailureAsync(
        InstrumentPriceSourceBinding binding, SourceRunState runState, SourceFetchResult result, RunCounters counters,
        CancellationToken cancellationToken)
    {
        var category = result.Failure!.Value;
        var now = Now;
        binding.LastFailure = category;
        binding.LastFailureUtc = now;
        binding.ConsecutiveFailures++;
        counters.Fail(category);

        switch (category)
        {
            case JobFailureCategory.ProviderRateLimited:
                var wait = result.RetryAfter ?? InstrumentPriceSyncConstants.RateLimitDefaultBackoff;
                wait = wait < InstrumentPriceSyncConstants.RateLimitMinBackoff ? InstrumentPriceSyncConstants.RateLimitMinBackoff
                    : wait > InstrumentPriceSyncConstants.RateLimitMaxBackoff ? InstrumentPriceSyncConstants.RateLimitMaxBackoff : wait;
                runState.Unusable = true;
                await BackOffAsync(runState.Source.Key, now + wait, category, cancellationToken);
                break;
            case JobFailureCategory.ProviderUnavailable:
                if (++runState.ConsecutiveUnavailable >= InstrumentPriceSyncConstants.UnavailableBackoffThreshold)
                {
                    runState.Unusable = true;
                    await BackOffAsync(runState.Source.Key, now + InstrumentPriceSyncConstants.UnavailableBackoff, category, cancellationToken);
                }

                break;
            case JobFailureCategory.Configuration:
                runState.Unusable = true;
                break;
        }
    }

    private async Task BackOffAsync(string sourceKey, DateTime until, JobFailureCategory reason, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        for (var attempt = 0; attempt < SourceRequestQuota.MaxConflicts; attempt++)
        {
            var state = await writer.InstrumentPriceSourceStates.FirstOrDefaultAsync(s => s.SourceKey == sourceKey, cancellationToken);
            if (state is null)
            {
                state = new InstrumentPriceSourceState { SourceKey = sourceKey };
                writer.InstrumentPriceSourceStates.Add(state);
            }

            state.BackOffUntilUtc = until;
            state.BackOffReason = reason;
            state.UpdatedAtUtc = Now;
            try
            {
                await writer.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException)
            {
                writer.ChangeTracker.Clear();
            }
        }
    }

    private static DateOnly MaxDate(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly MinDate(DateOnly a, DateOnly b) => a < b ? a : b;

    private sealed class RunCounters
    {
        private readonly Dictionary<JobFailureCategory, int> failures = [];

        public int InstrumentsAttempted { get; set; }

        public int InstrumentsSucceeded { get; set; }

        public int InstrumentsFailed { get; set; }

        public int PricesInserted { get; set; }

        public int FallbacksUsed { get; set; }

        public int RequestsMade { get; set; }

        public void Fail(JobFailureCategory category) => failures[category] = failures.GetValueOrDefault(category) + 1;

        public void ApplyTo(InstrumentPriceSyncJobRun run)
        {
            run.InstrumentsAttempted = InstrumentsAttempted;
            run.InstrumentsSucceeded = InstrumentsSucceeded;
            run.InstrumentsFailed = InstrumentsFailed;
            run.PricesInserted = PricesInserted;
            run.FallbacksUsed = FallbacksUsed;
            run.RequestsMade = RequestsMade;
        }

        public (JobRunStatus, JobFailureCategory?) Outcome()
        {
            var category = failures.Count == 0 ? (JobFailureCategory?)null : failures.MaxBy(pair => pair.Value).Key;
            if (InstrumentsFailed == 0)
            {
                return (JobRunStatus.Succeeded, null);
            }

            return InstrumentsSucceeded == 0
                ? (JobRunStatus.Failed, category)
                : (JobRunStatus.PartiallySucceeded, category);
        }
    }
}
