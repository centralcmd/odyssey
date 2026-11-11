using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Core.Jobs;
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

    /// <summary>
    /// Days after which a forward window counts as settled (issue #447 A6, #468): the watermark records the newest
    /// <em>returned</em> date, but never trails a window's end by more than this, so a long holiday or a delisted
    /// symbol does not re-request the same days forever.
    /// </summary>
    public const int WatermarkSettleDays = 5;
}

/// <summary>
/// The instrument-price sync (issue #446 §3), modelled on <c>ExchangeRateSyncService</c>: a scheduled run is due
/// <c>InstrumentPriceSyncIntervalHours</c> after the last, a catch-up five minutes after a run that inserted rows
/// while any binding is still behind, the start of the next quota hour after a run that deferred bindings (issue
/// #447 A8), and the interval doubles after three failed runs (capped at 168 h). With no enabled binding to a
/// registered source it writes nothing at all. Each binding forward-fills from its newest fetched date to today, then
/// backfills toward its start date; a failure falls back to the next binding, a reached cap defers it.
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

        if (last.InstrumentsDeferred > 0 && await AnyBindingBehindAsync(cancellationToken))
        {
            // A cap or the forward budget stopped the run: resume when the quota does. Hourly caps are UTC clock hours.
            return sources.All.Any(s => s.Limits.MaxRequestsPerHour is not null)
                ? SourceRequestQuota.HourOf(finished).AddHours(1)
                : finished + TimeSpan.FromMinutes(InstrumentPriceSyncConstants.CatchUpDelayMinutes);
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
        catch (DbUpdateException exception) when (InstrumentDbErrors.IsDuplicateKey(exception))
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

    /// <summary>
    /// One source's state for one run. Budgets count <strong>wire requests</strong> (issue #447 A8): the pacer asks
    /// <see cref="MayRequest"/> before every request, so a span of several requests cannot overshoot either budget.
    /// </summary>
    private sealed class SourceRunState
    {
        private int forwardBefore;
        private int forwardSpanStart = -1;

        public SourceRunState(IInstrumentPriceSource source, TimeProvider timeProvider)
        {
            Source = source;
            Pacer = new SourceRequestPacer(source.Limits.MinRequestInterval, timeProvider) { Budget = MayRequest };
        }

        public IInstrumentPriceSource Source { get; }

        public SourceRequestPacer Pacer { get; }

        public bool Unusable { get; set; }

        public bool Unconfigured { get; set; }

        /// <summary>The credential read <c>Unreadable</c>: every binding of the source records <c>Configuration</c>.</summary>
        public bool CredentialUnreadable { get; set; }

        /// <summary>A shared cap or the run's whole budget is reached: every later binding of this source is deferred.</summary>
        public bool Capped { get; set; }

        public int ConsecutiveUnavailable { get; set; }

        public int Requests => Pacer.Sent;

        public bool BudgetLeft => Requests < Source.Limits.MaxRequestsPerRun;

        public int ForwardRequests => forwardBefore + (forwardSpanStart < 0 ? 0 : Pacer.Sent - forwardSpanStart);

        public bool ForwardBudgetLeft => ForwardRequests < Math.Max(1, (Source.Limits.MaxRequestsPerRun + 1) / 2);

        public void BeginSpan(bool forward) => forwardSpanStart = forward ? Pacer.Sent : -1;

        public void EndSpan()
        {
            forwardBefore = ForwardRequests;
            forwardSpanStart = -1;
        }

        private bool MayRequest() => BudgetLeft && (forwardSpanStart < 0 || ForwardBudgetLeft);
    }

    private enum BindingOutcome
    {
        Succeeded,
        Failed,

        /// <summary>Not attempted, or stopped before anything was stored, because a cap or the forward budget was reached.</summary>
        Deferred,
    }

    private async Task SyncAsync(RunCounters counters, CancellationToken cancellationToken)
    {
        var resolved = await settings.GetAsync(cancellationToken);
        var today = OsloDate.Today(timeProvider);
        var now = Now;

        var keys = sources.All.Select(s => s.Key).ToList();
        // Untracked snapshots: each binding's watermark and failure state is written per span, under the instrument's
        // lock and in the same transaction as that span's inserts, so a binding replaced or removed mid-run is
        // detected rather than overwritten, and a run that ends early keeps every span it committed.
        var bindings = await context.InstrumentPriceSourceBindings.AsNoTracking()
            .Where(b => b.Enabled && keys.Contains(b.SourceKey))
            .ToListAsync(cancellationToken);
        var instrumentIds = bindings.Select(b => b.InstrumentId).Distinct().ToList();
        var instruments = await context.Instruments.AsNoTracking()
            .Where(i => instrumentIds.Contains(i.InstrumentId) && i.Archived == null)
            .ToDictionaryAsync(i => i.InstrumentId, cancellationToken);

        var states = await context.InstrumentPriceSourceStates.AsNoTracking().Where(s => keys.Contains(s.SourceKey)).ToDictionaryAsync(s => s.SourceKey, cancellationToken);
        var runStates = new Dictionary<string, SourceRunState>(StringComparer.Ordinal);
        foreach (var source in sources.All)
        {
            var runState = new SourceRunState(source, timeProvider);
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
                    // Unreadable is never quiet: the run cannot end Succeeded while a source's credential is unusable.
                    runState.Unusable = true;
                    runState.CredentialUnreadable = true;
                    counters.UnreadableSources++;
                    counters.Fail(JobFailureCategory.Configuration);
                }
            }

            runStates[source.Key] = runState;
        }

        // Most behind first (issue #447 A8), so a capped run never starves the same instruments: the oldest forward
        // watermark (never fetched first), then the least recently attempted — a watermark A6 left unchanged must not
        // keep an instrument at the head of every run — then the id.
        var groups = bindings.GroupBy(b => b.InstrumentId)
            .Select(g => g.OrderBy(b => b.Priority).ToList())
            .OrderBy(g => g.Min(b => b.NewestFetched ?? DateOnly.MinValue))
            .ThenBy(g => g.Max(LastAttemptUtc) ?? DateTime.MinValue)
            .ThenBy(g => g[0].InstrumentId)
            .ToList();
        foreach (var group in groups)
        {
            if (!instruments.TryGetValue(group[0].InstrumentId, out var instrument))
            {
                continue;
            }

            var attemptedAny = false;
            var outcome = BindingOutcome.Failed;
            foreach (var binding in group)
            {
                var runState = runStates[binding.SourceKey];
                if (runState.CredentialUnreadable)
                {
                    // Fails closed, and says so where an Admin looks first: on the binding. Nothing is sent.
                    await PersistFailureAsync(binding, row =>
                    {
                        row.LastFailure = JobFailureCategory.Configuration;
                        row.LastFailureUtc = now;
                        row.ConsecutiveFailures++;
                    }, cancellationToken);
                    continue;
                }

                if (runState.Unusable || runState.Unconfigured || !runState.Source.SupportedTypes.Contains(instrument.Type)
                    || IsSkippedToday(binding, today))
                {
                    continue;
                }

                if (runState.Capped || !runState.BudgetLeft)
                {
                    // A deferral never falls back: the next source is not a substitute for a quota that resets.
                    outcome = BindingOutcome.Deferred;
                    break;
                }

                if (attemptedAny)
                {
                    counters.FallbacksUsed++;
                }

                attemptedAny = true;
                outcome = await SyncBindingAsync(instrument, binding, runState, today, resolved.SyncHistoryDays, counters, cancellationToken);
                if (outcome != BindingOutcome.Failed)
                {
                    break;
                }
            }

            if (outcome == BindingOutcome.Deferred)
            {
                counters.InstrumentsDeferred++;
                continue;
            }

            if (!attemptedAny)
            {
                continue;
            }

            counters.InstrumentsAttempted++;
            if (outcome == BindingOutcome.Succeeded)
            {
                counters.InstrumentsSucceeded++;
            }
            else
            {
                counters.InstrumentsFailed++;
            }
        }
    }

    private static DateTime? LastAttemptUtc(InstrumentPriceSourceBinding binding) =>
        binding.LastSuccessUtc > binding.LastFailureUtc || binding.LastFailureUtc is null ? binding.LastSuccessUtc : binding.LastFailureUtc;

    /// <summary>
    /// A binding whose last span failed <see cref="JobFailureCategory.UnsupportedInput"/> today (Oslo) is not retried
    /// until tomorrow (issue #447 A9) — an unknown symbol or an unmapped venue would otherwise spend capped requests
    /// on every run. An edit since the failure (<see cref="InstrumentPriceSourceBinding.UpdatedAtUtc"/>) clears it.
    /// </summary>
    internal static bool IsSkippedToday(InstrumentPriceSourceBinding binding, DateOnly today) =>
        binding.LastFailure == JobFailureCategory.UnsupportedInput
        && binding.LastFailureUtc is { } failedAt
        && failedAt >= binding.UpdatedAtUtc
        && OsloDate.FromUtc(failedAt) == today;

    /// <summary>Forward fill then one backfill span for one binding.</summary>
    private async Task<BindingOutcome> SyncBindingAsync(
        Instrument instrument, InstrumentPriceSourceBinding binding, SourceRunState runState, DateOnly today, int historyDays,
        RunCounters counters, CancellationToken cancellationToken)
    {
        var maxDays = Math.Max(1, runState.Source.Limits.MaxDaysPerRequest);
        var start = StartOf(binding, today, historyDays);
        var anySuccess = false;

        // Forward fill, oldest span first so NewestFetched stays a contiguous watermark.
        while (!runState.Unusable)
        {
            var from = binding.NewestFetched is { } newest ? newest.AddDays(1) : MaxDate(start, today.AddDays(-(maxDays - 1)));
            if (from > today)
            {
                anySuccess = true;
                break;
            }

            if (runState.Capped || !runState.BudgetLeft || !runState.ForwardBudgetLeft)
            {
                if (anySuccess)
                {
                    break;
                }

                return BindingOutcome.Deferred;
            }

            var to = MinDate(today, from.AddDays(maxDays - 1));
            var outcome = await FetchSpanAsync(instrument, binding, runState, from, to, forward: true, counters, (row, closes) =>
            {
                if (ForwardWatermark(row.NewestFetched, closes, from, to, today) is { } mark)
                {
                    row.NewestFetched = mark;
                }

                row.BackfilledFrom ??= from;
            }, cancellationToken);
            if (outcome == SpanOutcome.Deferred)
            {
                return anySuccess ? BindingOutcome.Succeeded : BindingOutcome.Deferred;
            }

            if (outcome != SpanOutcome.Stored)
            {
                return anySuccess && outcome != SpanOutcome.BindingChanged ? BindingOutcome.Succeeded : BindingOutcome.Failed;
            }

            anySuccess = true;
            if (binding.NewestFetched is null || binding.NewestFetched < to)
            {
                // The provider has nothing newer yet (#468): the rest of the window is asked for again on a later run.
                break;
            }
        }

        // Backfill, newest span first; BackfilledFrom only advances across complete spans.
        if (anySuccess && binding.BackfilledFrom is { } backfilled && backfilled > start
            && runState.BudgetLeft && !runState.Unusable && !runState.Capped)
        {
            var to = backfilled.AddDays(-1);
            var from = MaxDate(start, to.AddDays(-(maxDays - 1)));
            await FetchSpanAsync(instrument, binding, runState, from, to, forward: false, counters, (row, _) => row.BackfilledFrom = from, cancellationToken);
        }

        return anySuccess ? BindingOutcome.Succeeded : BindingOutcome.Failed;
    }

    /// <summary>
    /// The forward watermark after a stored span (issue #447 A6, #468): the newest <em>returned</em> date, so a close
    /// published after the run is fetched by the next one; an empty batch leaves it unchanged; and it is never left
    /// more than <see cref="InstrumentPriceSyncConstants.WatermarkSettleDays"/> behind the window's end, which bounds
    /// the re-requests for a long holiday or a delisted symbol. <c>null</c> means unchanged.
    /// </summary>
    internal static DateOnly? ForwardWatermark(
        DateOnly? current, IReadOnlyList<FetchedClose> closes, DateOnly from, DateOnly to, DateOnly today)
    {
        DateOnly? mark = closes.Count > 0 ? closes.Max(c => c.TradingDate) : null;

        // Every day of the window more than WatermarkSettleDays old counts as fetched, returned or not.
        var settled = MinDate(to, today.AddDays(-(InstrumentPriceSyncConstants.WatermarkSettleDays + 1)));
        if (settled >= from && (mark is null || settled > mark))
        {
            mark = settled;
        }

        return mark is { } m && (current is null || m > current) ? m : null;
    }

    private enum SpanOutcome
    {
        Stored,
        Failed,

        /// <summary>The binding was removed, or its source symbol changed, while the run held a snapshot of it.</summary>
        BindingChanged,

        /// <summary>A request limit stopped the span before it was stored; nothing was written (issue #447 A8).</summary>
        Deferred,
    }

    private async Task<SpanOutcome> FetchSpanAsync(
        Instrument instrument, InstrumentPriceSourceBinding binding, SourceRunState runState, DateOnly from, DateOnly to, bool forward,
        RunCounters counters, Action<InstrumentPriceSourceBinding, IReadOnlyList<FetchedClose>> advance, CancellationToken cancellationToken)
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
            timeProvider,
            runState.Pacer);

        SourceFetchResult result;
        runState.BeginSpan(forward);
        try
        {
            result = await source.FetchClosesAsync(request, http, cancellationToken);
        }
        catch (SourceQuotaExhaustedException exhausted)
        {
            // Only the forward half of the run's budget being spent leaves the source usable for backfill.
            if (exhausted.Limit != SourceQuotaLimit.Run || !runState.BudgetLeft)
            {
                runState.Capped = true;
            }

            return SpanOutcome.Deferred;
        }
        catch (SourceCredentialException credential)
        {
            if (credential.State == SecretReadState.NotSet)
            {
                runState.Unconfigured = true;
                return SpanOutcome.Failed;
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
            runState.EndSpan();
            counters.RequestsMade += http.RequestsMade;
        }

        if (result.Succeeded && FetchedBatchCheck.Check(result.Closes!, request, instrument.CurrencyCode, OsloDate.Today(timeProvider)) is { } rejected)
        {
            logger.LogWarning("Price source {SourceKey} batch for instrument {InstrumentId} rejected by rule {Rule}.",
                source.Key, instrument.InstrumentId, rejected.Rule);
            source.OnBatchRejected(request, rejected.Rule);
            result = SourceFetchResult.Failed(rejected.Category);
        }

        if (!result.Succeeded)
        {
            await RecordFailureAsync(binding, runState, result, counters, cancellationToken);
            return SpanOutcome.Failed;
        }

        var now = Now;
        var closes = result.Closes!;
        void Succeed(InstrumentPriceSourceBinding row)
        {
            advance(row, closes);
            row.LastSuccessUtc = now;
            row.ConsecutiveFailures = 0;
            row.LastFailure = null;
        }

        if (await InsertAsync(binding, closes, Succeed, cancellationToken) is not { } inserted)
        {
            return SpanOutcome.BindingChanged;
        }

        counters.PricesInserted += inserted;
        runState.ConsecutiveUnavailable = 0;
        Succeed(binding);
        return SpanOutcome.Stored;
    }

    /// <summary>
    /// Inserts a span's closes and advances its binding in one transaction under the instrument's lock. Returns
    /// <c>null</c>, writing nothing, when the binding no longer exists with the symbol the run fetched for.
    /// </summary>
    private async Task<int?> InsertAsync(
        InstrumentPriceSourceBinding snapshot, IReadOnlyList<FetchedClose> closes, Action<InstrumentPriceSourceBinding> advance,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await InstrumentLocks.InTransactionAsync(writer, async () =>
        {
            await InstrumentLocks.LockAsync(writer, snapshot.InstrumentId, cancellationToken);
            if (await CurrentBindingAsync(writer, snapshot, cancellationToken) is not { } row)
            {
                return (int?)null;
            }

            var dates = closes.Select(c => c.TradingDate).ToList();
            var existing = await writer.InstrumentPrices.AsNoTracking()
                .Where(p => p.InstrumentId == snapshot.InstrumentId && p.SourceKey == snapshot.SourceKey && dates.Contains(p.TradingDate))
                .Select(p => p.TradingDate)
                .ToListAsync(cancellationToken);
            var fresh = closes.Where(c => !existing.Contains(c.TradingDate)).ToList();
            foreach (var close in fresh)
            {
                writer.InstrumentPrices.Add(new InstrumentPrice
                {
                    InstrumentPriceId = Guid.NewGuid(),
                    InstrumentId = snapshot.InstrumentId,
                    TradingDate = close.TradingDate,
                    Close = close.Close,
                    SourceKey = snapshot.SourceKey,
                    CreatedAtUtc = Now,
                });
            }

            advance(row);
            await writer.SaveChangesAsync(cancellationToken);
            return fresh.Count;
        }, cancellationToken);
    }

    /// <summary>Writes a binding's failure state under the instrument's lock; a binding replaced meanwhile is left alone.</summary>
    private async Task PersistFailureAsync(InstrumentPriceSourceBinding snapshot, Action<InstrumentPriceSourceBinding> record, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await InstrumentLocks.InTransactionAsync(writer, async () =>
        {
            await InstrumentLocks.LockAsync(writer, snapshot.InstrumentId, cancellationToken);
            if (await CurrentBindingAsync(writer, snapshot, cancellationToken) is { } row)
            {
                record(row);
                await writer.SaveChangesAsync(cancellationToken);
            }

            return true;
        }, cancellationToken);
    }

    private static Task<InstrumentPriceSourceBinding?> CurrentBindingAsync(
        OdysseyContext writer, InstrumentPriceSourceBinding snapshot, CancellationToken cancellationToken) =>
        writer.InstrumentPriceSourceBindings.FirstOrDefaultAsync(
            b => b.InstrumentId == snapshot.InstrumentId && b.SourceKey == snapshot.SourceKey && b.SourceSymbol == snapshot.SourceSymbol,
            cancellationToken);

    private async Task RecordFailureAsync(
        InstrumentPriceSourceBinding binding, SourceRunState runState, SourceFetchResult result, RunCounters counters,
        CancellationToken cancellationToken)
    {
        var category = result.Failure!.Value;
        var now = Now;
        void Record(InstrumentPriceSourceBinding row)
        {
            row.LastFailure = category;
            row.LastFailureUtc = now;
            row.ConsecutiveFailures++;
        }

        await PersistFailureAsync(binding, Record, cancellationToken);
        Record(binding);
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

        // Every attempt lost a race on the state row's token; the in-run flag still stops this run using the source.
        logger.LogWarning("Price source {SourceKey} back-off was not recorded after {Attempts} conflicting writes.",
            sourceKey, SourceRequestQuota.MaxConflicts);
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

        public int InstrumentsDeferred { get; set; }

        /// <summary>Sources skipped because their credential read <c>Unreadable</c>.</summary>
        public int UnreadableSources { get; set; }

        public void Fail(JobFailureCategory category) => failures[category] = failures.GetValueOrDefault(category) + 1;

        public void ApplyTo(InstrumentPriceSyncJobRun run)
        {
            run.InstrumentsAttempted = InstrumentsAttempted;
            run.InstrumentsSucceeded = InstrumentsSucceeded;
            run.InstrumentsFailed = InstrumentsFailed;
            run.PricesInserted = PricesInserted;
            run.FallbacksUsed = FallbacksUsed;
            run.RequestsMade = RequestsMade;
            run.InstrumentsDeferred = InstrumentsDeferred;
        }

        public (JobRunStatus, JobFailureCategory?) Outcome()
        {
            var category = failures.Count == 0 ? (JobFailureCategory?)null : failures.MaxBy(pair => pair.Value).Key;
            if (InstrumentsFailed == 0 && UnreadableSources == 0)
            {
                // A deferral is a shortfall, not a failure: it names no category and never counts toward the back-off.
                return InstrumentsDeferred > 0 && InstrumentsSucceeded > 0
                    ? (JobRunStatus.PartiallySucceeded, null)
                    : (JobRunStatus.Succeeded, null);
            }

            if (InstrumentsFailed == 0)
            {
                return InstrumentsSucceeded == 0
                    ? (JobRunStatus.Failed, JobFailureCategory.Configuration)
                    : (JobRunStatus.PartiallySucceeded, JobFailureCategory.Configuration);
            }

            return InstrumentsSucceeded == 0
                ? (JobRunStatus.Failed, category)
                : (JobRunStatus.PartiallySucceeded, category);
        }
    }
}
