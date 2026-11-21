using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance.ExchangeRateSync.Providers;
using Odyssey.Core.Jobs;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.ExchangeRateSync;

/// <summary>
/// Runs the exchange-rate sync (issue #349 §3 "Processing pipeline"): plans forward fill and backfill per
/// enabled currency, fetches each span from Norges Bank, writes the published <c>X → NOK</c> row idempotently
/// (readers invert it for <c>NOK → X</c>, issue #472), and records the run. It holds no schedule state: everything the worker's job and the status
/// endpoint need is derived from the run log and the rows on every call.
/// </summary>
/// <remarks>
/// <para>
/// <b>The guard.</b> A run starts by inserting its <c>Running</c> row, which the generated
/// <c>JobRuns.ExclusiveGuard</c> column's unique index makes exclusive on MariaDB; the pre-check here is what the EF
/// InMemory tiers see. A <c>Running</c> row older than twice the hard timeout is marked
/// <c>Failed / Abandoned</c> before the next run takes the guard.
/// </para>
/// <para>
/// <b>Committed requests stay.</b> Each request's rows are saved on their own, so a later failure, a
/// timeout or a shutdown keeps what earlier requests wrote; the backfill watermark only advances over
/// chunks that committed, so it never jumps a hole.
/// </para>
/// </remarks>
public sealed class ExchangeRateSyncService(
    OdysseyContext context,
    NorgesBankClient client,
    ExchangeRateSupportedList supportedList,
    IExchangeRateSyncSettingsLookup settingsLookup,
    ExchangeRateSyncPacing pacing,
    JobWorkerIdentity worker,
    TimeProvider timeProvider,
    ILogger<ExchangeRateSyncService> logger)
{
    private const string Hub = ExchangeRateSyncConstants.BaseCurrencyCode;

    private static readonly decimal RateMin = decimal.Parse(MoneyBounds.ExchangeRateMin, CultureInfo.InvariantCulture);

    private static readonly decimal RateMax = decimal.Parse(MoneyBounds.ExchangeRateMax, CultureInfo.InvariantCulture);

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>The sync's rows of the shared run log (issue #409): its runs and purges, ids kept from #349.</summary>
    private DbSet<ExchangeRateSyncJobRun> Runs => context.Set<ExchangeRateSyncJobRun>();

    // ── Starting a run ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a manual run (issue #349 §5.5): <c>429</c> within the cooldown of the last manual run,
    /// <c>409</c> while the guard is held. With nothing enabled the run is recorded as <c>Skipped</c> and
    /// finished at once, so the caller dispatches only a run that is still <c>Running</c>.
    /// </summary>
    public async Task<ExchangeRateSyncJobRun> StartManualAsync(string? userId, CancellationToken cancellationToken = default)
    {
        var now = Now;
        var lastManual = await Runs.AsNoTracking()
            .Where(run => run.Trigger == JobRunTrigger.Manual)
            .MaxAsync(run => (DateTime?)run.StartedAtUtc, cancellationToken);
        if (lastManual is { } last && last.AddSeconds(ExchangeRateSyncConstants.ManualRunCooldownSeconds) > now)
        {
            throw new ExchangeRateSyncCooldownException(
                last.AddSeconds(ExchangeRateSyncConstants.ManualRunCooldownSeconds));
        }

        // A run with nothing to do is finished here, so it is inserted already claimed: an unclaimed row
        // could be taken by a worker between the insert and the finish, failing this request's save.
        var nothingEnabled = (await EnabledCodesAsync(cancellationToken)).Count == 0;
        var run = await TryStartAsync(ExchangeRateSyncTrigger.Manual, userId, claimNow: nothingEnabled, cancellationToken)
            ?? throw new DomainConflictException("An exchange-rate sync run or purge is already in progress.");

        logger.LogInformation(
            "Exchange-rate sync run {RunId} started manually by user {UserId}.", run.JobRunId, userId);

        if (nothingEnabled)
        {
            await FinishAsync(run, ExchangeRateSyncRunStatus.Skipped, null, cancellationToken);
        }

        return run;
    }

    /// <summary>
    /// Inserts a <c>Running</c> row for <paramref name="trigger"/>, or returns null while another run or
    /// purge holds the guard. Stale <c>Running</c> rows are abandoned first.
    /// </summary>
    public Task<ExchangeRateSyncJobRun?> TryStartAsync(
        ExchangeRateSyncTrigger trigger, string? userId, CancellationToken cancellationToken = default) =>
        TryStartAsync(trigger, userId, claimNow: trigger != ExchangeRateSyncTrigger.Manual, cancellationToken);

    private async Task<ExchangeRateSyncJobRun?> TryStartAsync(
        ExchangeRateSyncTrigger trigger, string? userId, bool claimNow, CancellationToken cancellationToken)
    {
        await AbandonStaleAsync(cancellationToken);

        if (await Runs.AnyAsync(run => run.Status == JobRunStatus.Running, cancellationToken))
        {
            return null;
        }

        var now = Now;
        var run = new ExchangeRateSyncJobRun
        {
            JobRunId = Guid.NewGuid(),
            Trigger = (JobRunTrigger)trigger,
            RequestedByUserId = userId,
            Provider = ExchangeRateProvider.NorgesBank,
            Status = JobRunStatus.Running,
            StartedAtUtc = now,
            LastActivityAtUtc = now,
            // A manual run is started by the API and executed by the worker, which claims it; every other
            // run is executed by the process that starts it. A purge runs in the API, which heartbeats nothing,
            // so it names no worker (issue #409 §3.4).
            ClaimedAtUtc = claimNow ? now : null,
            WorkerId = claimNow && trigger != ExchangeRateSyncTrigger.Purge ? worker.WorkerId : null,
        };
        Runs.Add(run);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbErrors.IsDuplicateKey(exception))
        {
            // The unique JobRuns.ExclusiveGuard index: another instance took the guard between the check and here.
            // Only that error means "in progress"; any other write fault propagates as itself.
            context.Entry(run).State = EntityState.Detached;
            return null;
        }

        return run;
    }

    /// <summary>
    /// The worker job's tick: starts and executes a scheduled run when one is due. With nothing enabled it
    /// writes nothing at all, so idle ticks never flood the 500-row run log.
    /// </summary>
    public async Task<Guid?> RunScheduledIfDueAsync(CancellationToken cancellationToken = default)
    {
        // Checked first and cheaply, so an instance that never enables a currency never calls out.
        if ((await EnabledCodesAsync(cancellationToken)).Count == 0)
        {
            return null;
        }

        // A Running row left by a dead process must be abandoned HERE, before the status read: that read
        // reports it as the current run, so without this the job would wait on it forever.
        await AbandonStaleAsync(cancellationToken);

        var status = await GetStatusAsync(cancellationToken);
        if (status.EnabledCount == 0 || status.CurrentRun is not null
            || status.NextRunAtUtc is not { } next || next > Now)
        {
            return null;
        }

        var run = await TryStartAsync(ExchangeRateSyncTrigger.Scheduled, null, cancellationToken);
        if (run is null)
        {
            return null;
        }

        await ExecuteAsync(run.JobRunId, cancellationToken);
        return run.JobRunId;
    }

    /// <summary>
    /// Claims the oldest manual run no process has taken yet, or returns null. Every worker replica asks;
    /// the run's <see cref="JobRun.Generation"/> concurrency token lets exactly one save succeed.
    /// Stale runs are abandoned first, so a request no worker took within
    /// <see cref="ExchangeRateSyncConstants.UnclaimedRequestTimeout"/> is never executed late.
    /// </summary>
    public async Task<Guid?> ClaimRequestedRunAsync(CancellationToken cancellationToken = default)
    {
        await AbandonStaleAsync(cancellationToken);

        var run = await Runs
            .Where(value => value.Status == JobRunStatus.Running
                && value.Trigger == JobRunTrigger.Manual
                && value.ClaimedAtUtc == null)
            .OrderBy(value => value.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            return null;
        }

        run.ClaimedAtUtc = Now;
        run.LastActivityAtUtc = run.ClaimedAtUtc.Value;
        run.WorkerId = worker.WorkerId;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another replica claimed it between the read and here.
            context.Entry(run).State = EntityState.Detached;
            return null;
        }

        return run.JobRunId;
    }

    // ── Executing a run ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a run that is already <c>Running</c>, under the 10-minute hard timeout. A shutdown or the
    /// timeout ends it <c>Failed / Cancelled</c>; requests that already committed stay.
    /// </summary>
    public async Task ExecuteAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await Runs.FirstOrDefaultAsync(
            value => value.JobRunId == runId, cancellationToken);
        if (run is null || run.Status != JobRunStatus.Running)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(ExchangeRateSyncConstants.RunTimeoutMinutes));

        var outcome = new RunOutcome();
        try
        {
            await ExecuteCoreAsync(run, outcome, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            outcome.Fail(ExchangeRateSyncErrorKind.Cancelled);
            outcome.Stopped = true;
        }
        catch (Exception exception)
        {
            // Anything unforeseen still finishes the run, so it never stays Running holding the guard
            // until the stale-run sweep. Requests that committed before the fault stay.
            logger.LogError(exception, "Exchange-rate sync run {RunId} failed unexpectedly.", runId);
            outcome.Fail(ExchangeRateSyncErrorKind.Internal);
            outcome.Stopped = true;
        }

        context.ChangeTracker.Clear();
        // A run row deleted under the run (a hand edit, a restore) leaves nothing to finish; it holds no
        // guard any more either, so log it and stop rather than throw out of the job.
        var tracked = await Runs.FirstOrDefaultAsync(
            value => value.JobRunId == runId, CancellationToken.None);
        if (tracked is null)
        {
            logger.LogWarning("Exchange-rate sync run {RunId} was removed before it finished; its outcome is not recorded.", runId);
            return;
        }

        tracked.RowsInserted = outcome.RowsInserted;
        tracked.RowsSkipped = outcome.RowsSkipped;
        tracked.RowsRejected = outcome.RowsRejected;
        tracked.SpanFrom = outcome.SpanFrom;
        tracked.SpanTo = outcome.SpanTo;
        tracked.CurrencyCodes = Truncate(string.Join(',', outcome.Codes.Order(StringComparer.Ordinal)));
        try
        {
            await FinishAsync(tracked, outcome.Status, outcome.ErrorKind, CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The stale-run sweep finished the row first (issue #409 §3.4): its write wins and this one is dropped.
            logger.LogWarning("Exchange-rate sync run {RunId} was finished by the stale-run sweep first; its outcome is dropped.", runId);
            return;
        }

        logger.LogInformation(
            "Exchange-rate sync run {RunId} finished {Status} ({ErrorKind}): {Inserted} inserted, {Skipped} skipped, {Rejected} rejected.",
            runId, tracked.Status, tracked.ErrorKind, outcome.RowsInserted, outcome.RowsSkipped, outcome.RowsRejected);
    }

    private async Task ExecuteCoreAsync(ExchangeRateSyncJobRun run, RunOutcome outcome, CancellationToken cancellationToken)
    {
        // Resolved per run, never at startup: a host without zone data still starts, and the status names it.
        if (ExchangeRateSyncClock.Oslo is not { } oslo)
        {
            outcome.Fail(ExchangeRateSyncErrorKind.TimeZoneUnavailable);
            return;
        }

        var settings = await settingsLookup.GetAsync(cancellationToken);
        if (settings.ReadFailed)
        {
            outcome.Fail(ExchangeRateSyncErrorKind.SettingsUnusable);
            return;
        }

        var enabled = await EnabledCodesAsync(cancellationToken);
        if (enabled.Count == 0)
        {
            outcome.Skip();
            return;
        }

        // Fetched and stored, unless a list was stored in the last few minutes (a catch-up run's predecessor).
        var series = await supportedList.RefreshAsync(cancellationToken);
        if (series is null)
        {
            outcome.Fail(ExchangeRateSyncErrorKind.ProviderUnavailable);
            return;
        }

        var today = ExchangeRateSyncClock.TodayIn(oslo, Now);
        var todayUtc = DateOnly.FromDateTime(Now);
        var fresh = series
            .Where(value => ExchangeRateSupportedList.IsFresh(value, todayUtc))
            .ToDictionary(value => value.CurrencyCode, StringComparer.Ordinal);

        var unsupported = enabled.Keys.Where(code => !fresh.ContainsKey(code)).ToList();
        if (unsupported.Count > 0)
        {
            outcome.Unsupported = true;
            logger.LogWarning(
                "Exchange-rate sync run {RunId}: enabled currencies no longer published were skipped: {Codes}.",
                run.JobRunId, string.Join(',', unsupported));
        }

        var extents = await FetchedExtentsAsync(oslo, enabled.Keys.ToList(), cancellationToken);
        var states = enabled
            .Where(pair => fresh.ContainsKey(pair.Key))
            .Select(pair =>
            {
                extents.TryGetValue(pair.Key, out var extent);
                var start = pair.Value.HistoryStartDate
                    ?? settings.HistoryStartDate
                    ?? ExchangeRateHistoryStartDateRule.DynamicDefault(todayUtc);
                return new CurrencyPlan(pair.Key, start, fresh[pair.Key].FirstObservation, extent, pair.Value.BackfilledFrom);
            })
            .ToList();

        var requests = 0;

        // Forward fill first, oldest chunk first so a failure leaves no hole, on at most 5 requests.
        while (requests < ExchangeRateSyncConstants.ForwardFillRequestsPerRun)
        {
            var group = states
                .Where(state => !state.Failed && state.ForwardSpan(today) is not null)
                .GroupBy(state => state.ForwardSpan(today)!.Value)
                .OrderBy(g => g.Key.From)
                .ThenByDescending(g => g.Count())
                .FirstOrDefault();
            if (group is null)
            {
                break;
            }

            var span = group.Key;
            var codes = group.ToList();
            requests++;
            if (await FetchAndWriteAsync(codes, span, oslo, outcome, requests > 1, backfill: false, cancellationToken))
            {
                foreach (var state in codes)
                {
                    state.NewestFetched = span.To;
                }
            }
        }

        // Then backfill, newest chunk first per currency, so the fetched history stays contiguous.
        while (requests < ExchangeRateSyncConstants.RequestsPerRun)
        {
            var group = states
                .Where(state => !state.Failed && state.BackfillSpan(today) is not null)
                .GroupBy(state => state.BackfillSpan(today)!.Value)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key.To)
                .FirstOrDefault();
            if (group is null)
            {
                break;
            }

            var span = group.Key;
            var codes = group.ToList();
            requests++;
            if (await FetchAndWriteAsync(codes, span, oslo, outcome, requests > 1, backfill: true, cancellationToken))
            {
                foreach (var state in codes)
                {
                    state.BackfilledFrom = BackfillWatermark(state, span);
                    state.NewestFetched ??= span.To;
                }
            }
        }
    }

    /// <summary>Where a currency's backfill stands once <paramref name="span"/> has committed.</summary>
    private static DateOnly BackfillWatermark(CurrencyPlan plan, DateSpan span) =>
        plan.FirstObservation is { } first && first > span.From ? first : span.From;

    /// <summary>One request's fetch, checks and idempotent write. Returns whether it committed.</summary>
    private async Task<bool> FetchAndWriteAsync(
        IReadOnlyList<CurrencyPlan> plans,
        DateSpan span,
        TimeZoneInfo oslo,
        RunOutcome outcome,
        bool spaced,
        bool backfill,
        CancellationToken cancellationToken)
    {
        if (spaced)
        {
            await pacing.Delay(ExchangeRateSyncConstants.RequestSpacing, cancellationToken);
        }

        var codes = plans.Select(plan => plan.Code).Order(StringComparer.Ordinal).ToList();
        outcome.Cover(codes, span);

        IReadOnlyList<ProviderObservation> observations;
        try
        {
            observations = await client.GetObservationsAsync(codes, span.From, span.To, cancellationToken);
        }
        catch (ExchangeRateProviderException failure)
        {
            outcome.Fail(failure.Kind);
            foreach (var plan in plans)
            {
                plan.Failed = true;
            }

            return false;
        }

        var requested = codes.ToHashSet(StringComparer.Ordinal);
        var fromUtc = ExchangeRateSyncClock.FetchedAsOf(span.From, oslo);
        var toUtc = ExchangeRateSyncClock.FetchedAsOf(span.To, oslo);
        var pairCodes = codes.Append(Hub).ToList();

        context.ChangeTracker.Clear();
        var existing = (await context.ExchangeRates.AsNoTracking()
                .Where(rate => rate.Provider == ExchangeRateProvider.NorgesBank
                    && pairCodes.Contains(rate.FromCurrencyCode)
                    && pairCodes.Contains(rate.ToCurrencyCode)
                    && rate.AsOf >= fromUtc && rate.AsOf <= toUtc)
                .Select(rate => new { rate.FromCurrencyCode, rate.ToCurrencyCode, rate.AsOf })
                .ToListAsync(cancellationToken))
            .Select(rate => (rate.FromCurrencyCode, rate.ToCurrencyCode, rate.AsOf))
            .ToHashSet();

        var now = Now;
        var rounded = new Dictionary<string, (int Count, DateOnly First, DateOnly Last)>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            // fromHub is computed and range-checked but never stored (issue #472 §8.1): readers invert the
            // published X → NOK row instead, and a reciprocal outside the stored bounds would hand them a
            // rate no manual entry could hold.
            if (!requested.Contains(observation.CurrencyCode)
                || !TryNormalise(observation, out var toHub, out _))
            {
                outcome.RowsRejected++;
                continue;
            }

            var asOf = ExchangeRateSyncClock.FetchedAsOf(observation.Date, oslo);
            if (!existing.Add((observation.CurrencyCode, Hub, asOf)))
            {
                outcome.RowsSkipped++;
                continue;
            }

            if (!IsStoredExactly(observation, toHub))
            {
                rounded[observation.CurrencyCode] = rounded.TryGetValue(observation.CurrencyCode, out var seen)
                    ? (seen.Count + 1, seen.First < observation.Date ? seen.First : observation.Date,
                        seen.Last > observation.Date ? seen.Last : observation.Date)
                    : (1, observation.Date, observation.Date);
            }

            // The published direction only (issue #472 release 2): NOK → X is 1 / this row, inverted at read time.
            context.ExchangeRates.Add(new ExchangeRate
            {
                FromCurrencyCode = observation.CurrencyCode,
                ToCurrencyCode = Hub,
                Rate = toHub,
                AsOf = asOf,
                CreatedAt = now,
                Provider = ExchangeRateProvider.NorgesBank,
            });
            outcome.RowsInserted++;
        }

        // Norges Bank has never published more than 8 decimals after scaling (issue #472 §7.11), so a rounded row
        // means the stored rate — and every reciprocal read from it — is off in the last place. Only rows this
        // request inserts count, and each currency logs once per request: a deep backfill would otherwise log one
        // line per business day. Named by currency, count and dates only, never a rate.
        foreach (var (code, (count, first, last)) in rounded)
        {
            logger.LogWarning(
                "Exchange-rate sync stored {Count} rounded {CurrencyCode} rate(s) between {First:yyyy-MM-dd} and {Last:yyyy-MM-dd}: the published values need more than {Scale} decimals.",
                count, code, first, last, MoneyBounds.ExchangeRateScale);
        }

        if (backfill)
        {
            // The watermark moves in the same save as the chunk's rows, so a fault or the timeout later in
            // the run never re-requests a chunk that already committed.
            var watermarks = plans.ToDictionary(plan => plan.Code, plan => BackfillWatermark(plan, span), StringComparer.Ordinal);
            var syncRows = await context.ExchangeRateSyncCurrencies
                .Where(row => codes.Contains(row.CurrencyCode))
                .ToListAsync(CancellationToken.None);
            foreach (var row in syncRows)
            {
                row.BackfilledFrom = watermarks[row.CurrencyCode];
            }
        }

        // Saved without the run's token: a request that fetched is committed whole or not at all.
        await context.SaveChangesAsync(CancellationToken.None);
        context.ChangeTracker.Clear();
        outcome.Committed = true;
        return true;
    }

    /// <summary>
    /// The structural checks (issue #349 §7.7) — never a plausibility check. A rejected observation is
    /// counted, not logged verbatim.
    /// </summary>
    internal static bool TryNormalise(ProviderObservation observation, out decimal toHub, out decimal fromHub)
    {
        toHub = 0;
        fromHub = 0;

        if (observation.Value is null
            || !decimal.TryParse(observation.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || value <= 0
            || observation.UnitMult is null
            || !int.TryParse(observation.UnitMult, NumberStyles.None, CultureInfo.InvariantCulture, out var unitMult)
            || unitMult is < 0 or > 6)
        {
            return false;
        }

        var scale = 1m;
        for (var i = 0; i < unitMult; i++)
        {
            scale *= 10m;
        }

        // A published value below the stored floor cannot survive rounding anyway, and rejecting it first
        // keeps scale / value inside decimal's range: 10 / 1e-28 would otherwise throw OverflowException
        // and leave the run Running, holding the guard.
        if (value < RateMin)
        {
            return false;
        }

        try
        {
            toHub = Math.Round(value / scale, MoneyBounds.ExchangeRateScale, MidpointRounding.AwayFromZero);
            fromHub = Math.Round(scale / value, MoneyBounds.ExchangeRateScale, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return false;
        }

        return toHub >= RateMin && toHub <= RateMax && fromHub >= RateMin && fromHub <= RateMax;
    }

    /// <summary>
    /// Whether <paramref name="toHub"/> — already rounded to <see cref="MoneyBounds.ExchangeRateScale"/> — is the
    /// published value exactly, after scaling. Only meaningful once <see cref="TryNormalise"/> has accepted it.
    /// </summary>
    internal static bool IsStoredExactly(ProviderObservation observation, decimal toHub)
    {
        var value = decimal.Parse(observation.Value!, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        var scale = 1m;
        for (var i = 0; i < int.Parse(observation.UnitMult!, NumberStyles.None, CultureInfo.InvariantCulture); i++)
        {
            scale *= 10m;
        }

        return toHub * scale == value;
    }

    // ── Reading state ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Refresh status: fetches and stores the provider's list (at most once per
    /// <see cref="ExchangeRateSyncConstants.SupportedListRefreshInterval"/>), then reads status. A failed fetch
    /// keeps the stored list, so the status still answers.
    /// </summary>
    public async Task<ExchangeRateSyncStatus> RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        await supportedList.RefreshAsync(cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    /// <summary>The status the sync panel shows (issue #349 §5.3), derived from the run log and the rows.</summary>
    public async Task<ExchangeRateSyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        var todayUtc = DateOnly.FromDateTime(now);
        var settings = await settingsLookup.GetAsync(cancellationToken);
        var enabled = await EnabledCodesAsync(cancellationToken);
        // The stored list only: a status poll or a job tick never waits on the provider. Null until a first
        // list has been stored, which reads as an unknown list, never as "nothing is published".
        var stored = await supportedList.ReadAsync(cancellationToken);
        var series = stored?.Series;
        var firstDates = series?.ToDictionary(value => value.CurrencyCode, value => value.FirstObservation, StringComparer.Ordinal);
        var effectiveGlobal = settings.HistoryStartDate ?? ExchangeRateHistoryStartDateRule.DynamicDefault(todayUtc);

        var unsupported = series is null
            ? []
            : enabled.Keys
                .Where(code => !series.Any(value => value.CurrencyCode == code
                    && ExchangeRateSupportedList.IsFresh(value, todayUtc)))
                .Order(StringComparer.Ordinal)
                .ToList();

        var oslo = ExchangeRateSyncClock.Oslo;
        var extents = await FetchedExtentsAsync(oslo, enabled.Keys.ToList(), cancellationToken);
        var today = oslo is null ? todayUtc : ExchangeRateSyncClock.TodayIn(oslo, now);
        var backfillComplete = true;
        var forwardIncomplete = false;
        foreach (var (code, sync) in enabled)
        {
            if (unsupported.Contains(code))
            {
                continue;
            }

            var start = sync.HistoryStartDate ?? effectiveGlobal;
            var first = firstDates?.GetValueOrDefault(code);
            backfillComplete &= IsBackfillComplete(sync.BackfilledFrom, start, first);
            if (extents.TryGetValue(code, out var extent)
                && today.DayNumber - extent.Newest.DayNumber > ExchangeRateSyncConstants.ForwardFillCaughtUpDays)
            {
                forwardIncomplete = true;
            }
        }

        var runs = Runs.AsNoTracking();
        var currentRow = await runs
            .Where(run => run.Status == JobRunStatus.Running)
            .OrderByDescending(run => run.StartedAtUtc)
            .Select(run => new { run.JobRunId, run.Trigger, run.StartedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        var current = currentRow is null ? null : new ExchangeRateSyncCurrentRun
        {
            Id = currentRow.JobRunId,
            Trigger = (ExchangeRateSyncTrigger)currentRow.Trigger,
            StartedAtUtc = currentRow.StartedAtUtc,
        };
        var last = await runs
            .Where(run => run.Status != JobRunStatus.Running)
            .OrderByDescending(run => run.FinishedAtUtc)
            .ThenByDescending(run => run.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var lastManual = await runs
            .Where(run => run.Trigger == JobRunTrigger.Manual)
            .MaxAsync(run => (DateTime?)run.StartedAtUtc, cancellationToken);

        // The sync history only: purges never count towards the backoff or the schedule.
        var recentSync = await runs
            .Where(run => run.Trigger != JobRunTrigger.Purge && run.Status != JobRunStatus.Running)
            .OrderByDescending(run => run.StartedAtUtc)
            .Take(ExchangeRateSyncConstants.RunRetention)
            .Select(run => new { run.Status, run.ErrorKind, run.FinishedAtUtc, run.RowsInserted })
            .ToListAsync(cancellationToken);
        // Only a fault of the provider or the run counts towards the backoff. A Run now no worker picked up
        // (Abandoned) or a run stopped by a shutdown or its timeout (Cancelled) says nothing about the
        // provider, so — like a skipped run — it neither counts nor breaks the streak.
        var failures = recentSync
            .Where(run => run.Status != JobRunStatus.Skipped
                && run.ErrorKind is not (ExchangeRateSyncErrorKind.Abandoned or ExchangeRateSyncErrorKind.Cancelled))
            .TakeWhile(run => run.Status == JobRunStatus.Failed)
            .Count();
        var lastFinished = recentSync.FirstOrDefault()?.FinishedAtUtc;

        // A gap only means "forward fill unfinished" while the last sync run is still finding rows. A run
        // capped by the forward-fill budget inserts some and leaves the gap; a provider that has stopped
        // publishing inserts none, and re-asking every catch-up delay would only repeat the empty answer.
        forwardIncomplete &= recentSync.FirstOrDefault() is { RowsInserted: > 0 };
        var catchingUp = enabled.Count > 0 && (!backfillComplete || forwardIncomplete);
        var intervalHours = settings.IntervalHours;
        var delay = catchingUp && failures < ExchangeRateSyncConstants.BackoffAfterFailures
            ? TimeSpan.FromMinutes(ExchangeRateSyncConstants.CatchUpDelayMinutes)
            : TimeSpan.FromHours(BackedOffHours(intervalHours, failures));
        DateTime? next = enabled.Count == 0 ? null : lastFinished is { } finished ? finished + delay : now;

        var state = enabled.Count == 0 ? ExchangeRateSyncState.Idle
            : current is not null ? ExchangeRateSyncState.Running
            : failures >= ExchangeRateSyncConstants.BackoffAfterFailures ? ExchangeRateSyncState.BackingOff
            : catchingUp ? ExchangeRateSyncState.CatchingUp
            : ExchangeRateSyncState.Scheduled;

        var cooldownEnds = lastManual?.AddSeconds(ExchangeRateSyncConstants.ManualRunCooldownSeconds);

        return new ExchangeRateSyncStatus
        {
            Provider = ExchangeRateProvider.NorgesBank,
            Attribution = ExchangeRateSyncConstants.Attribution,
            State = state,
            EnabledCount = enabled.Count,
            IntervalHours = intervalHours,
            GlobalHistoryStartDate = settings.HistoryStartDate,
            EffectiveGlobalHistoryStartDate = effectiveGlobal,
            BackfillComplete = backfillComplete,
            LastRun = last is null ? null : ToDto(last),
            CurrentRun = current,
            ManualRunAvailableAtUtc = cooldownEnds > now ? cooldownEnds : null,
            SupportedListAvailable = stored is not null,
            SupportedListFetchedAtUtc = stored?.FetchedAtUtc,
            NextRunAtUtc = next,
            UnsupportedEnabledCodes = unsupported,
            ConsecutiveFailures = failures,
            CatchUpDelayMinutes = ExchangeRateSyncConstants.CatchUpDelayMinutes,
            RunTimeoutMinutes = ExchangeRateSyncConstants.RunTimeoutMinutes,
            ManualRunCooldownSeconds = ExchangeRateSyncConstants.ManualRunCooldownSeconds,
            BaseCurrencyCode = Hub,
            ProviderDisplayName = ExchangeRateSyncConstants.ProviderDisplayName,
            FetchedRateLocalTime = ExchangeRateSyncConstants.FetchedRateLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            FetchedRateTimeZone = ExchangeRateSyncConstants.FetchedRateTimeZone,
            FetchedRateTimeZoneLabel = ExchangeRateSyncConstants.FetchedRateTimeZoneLabel,
        };
    }

    /// <summary>The run log, newest first (issue #349 §5.4).</summary>
    public async Task<PagedResult<ExistingExchangeRateSyncRun>> ListRunsAsync(
        ExchangeRateSyncRunsQueryParams query, CancellationToken cancellationToken = default)
    {
        var runs = Runs.AsNoTracking();
        if (query.Status is { } status)
        {
            var wanted = (JobRunStatus)status;
            runs = runs.Where(run => run.Status == wanted);
        }

        var ascending = query.SortDir == SortDirection.Asc;
        var ordered = ascending
            ? runs.OrderBy(run => run.StartedAtUtc).ThenBy(run => run.JobRunId)
            : runs.OrderByDescending(run => run.StartedAtUtc).ThenBy(run => run.JobRunId);

        var total = await ordered.CountAsync(cancellationToken);
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, MaxRunsPageSize);
        var items = await ordered.Skip(offset).Take(limit).ToListAsync(cancellationToken);

        return new PagedResult<ExistingExchangeRateSyncRun>
        {
            Items = items.Select(ToDto).ToList(),
            Offset = offset,
            Limit = limit,
            TotalCount = total,
        };
    }

    /// <summary>The run log's page cap (issue #349 §5.4).</summary>
    public const int MaxRunsPageSize = 100;

    internal static bool IsBackfillComplete(DateOnly? backfilledFrom, DateOnly effectiveStart, DateOnly? firstObservation)
    {
        // A start before the series begins (or on a holiday) still completes: the watermark stops at the
        // first observation, so that is the furthest it can ever reach.
        var reachable = firstObservation is { } first && first > effectiveStart ? first : effectiveStart;
        return backfilledFrom is { } from && from <= reachable;
    }

    private static int BackedOffHours(int intervalHours, int failures)
    {
        if (failures < ExchangeRateSyncConstants.BackoffAfterFailures)
        {
            return intervalHours;
        }

        var hours = (long)intervalHours;
        for (var i = ExchangeRateSyncConstants.BackoffAfterFailures - 1; i < failures && hours < ExchangeRateSyncConstants.MaxBackoffHours; i++)
        {
            hours *= 2;
        }

        return (int)Math.Min(hours, ExchangeRateSyncConstants.MaxBackoffHours);
    }

    internal static ExistingExchangeRateSyncRun ToDto(ExchangeRateSyncJobRun run) => new()
    {
        Id = run.JobRunId,
        Trigger = run.SyncTrigger,
        Status = run.SyncStatus,
        ErrorKind = run.ErrorKind,
        StartedAtUtc = run.StartedAtUtc,
        FinishedAtUtc = run.FinishedAtUtc,
        SpanFrom = run.SpanFrom,
        SpanTo = run.SpanTo,
        CurrencyCodes = run.CurrencyCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
        RowsInserted = run.RowsInserted,
        RowsSkipped = run.RowsSkipped,
        RowsRejected = run.RowsRejected,
        RowsDeleted = run.RowsDeleted,
    };

    // ── Shared helpers ────────────────────────────────────────────────────────────────────────────

    /// <summary>The enabled, non-archived currencies other than NOK, with their configuration.</summary>
    internal async Task<Dictionary<string, ExchangeRateSyncCurrency>> EnabledCodesAsync(CancellationToken cancellationToken)
    {
        var rows = await (
                from sync in context.ExchangeRateSyncCurrencies.AsNoTracking()
                join currency in context.Currencies.AsNoTracking() on sync.CurrencyCode equals currency.CurrencyCode
                where sync.Enabled && currency.Archived == null && sync.CurrencyCode != Hub
                select sync)
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.CurrencyCode, StringComparer.Ordinal);
    }

    /// <summary>
    /// Per currency, the Oslo dates of its oldest and newest fetched <c>X → NOK</c> row. Without zone data
    /// the UTC date is used, which is the same date for a 16:00 Oslo stamp.
    /// </summary>
    internal async Task<Dictionary<string, FetchedExtent>> FetchedExtentsAsync(
        TimeZoneInfo? oslo, IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return new Dictionary<string, FetchedExtent>(StringComparer.Ordinal);
        }

        // Scoped to the codes asked about, so the (From, To, AsOf, Provider) index is entered once per
        // code rather than scanned across every fetched row — this runs on every job tick.
        var rows = await context.ExchangeRates.AsNoTracking()
            .Where(rate => codes.Contains(rate.FromCurrencyCode) && rate.ToCurrencyCode == Hub && rate.Provider != null)
            .GroupBy(rate => rate.FromCurrencyCode)
            .Select(group => new
            {
                Code = group.Key,
                Oldest = group.Min(rate => rate.AsOf),
                Newest = group.Max(rate => rate.AsOf),
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.Code,
            row => new FetchedExtent(ExchangeRateSyncClock.DateIn(oslo, row.Oldest), ExchangeRateSyncClock.DateIn(oslo, row.Newest)),
            StringComparer.Ordinal);
    }

    private async Task AbandonStaleAsync(CancellationToken cancellationToken)
    {
        var cutoff = Now.AddMinutes(-2 * ExchangeRateSyncConstants.RunTimeoutMinutes);
        // A manual request no worker took (none running, or all down) would otherwise hold the guard for
        // the whole stale window, refusing every Run now and purge; it is given up much sooner.
        var unclaimedCutoff = Now - ExchangeRateSyncConstants.UnclaimedRequestTimeout;
        var stale = await Runs
            .Where(run => run.Status == JobRunStatus.Running
                && (run.StartedAtUtc < cutoff || (run.ClaimedAtUtc == null && run.StartedAtUtc < unclaimedCutoff)))
            .ToListAsync(cancellationToken);
        if (stale.Count == 0)
        {
            return;
        }

        foreach (var run in stale)
        {
            run.ErrorKind = ExchangeRateSyncErrorKind.Abandoned;
            if (run.ClaimedAtUtc is null)
            {
                run.Finish(JobRunStatus.Failed, JobFailureCategory.Abandoned,
                    run.StartedAtUtc + ExchangeRateSyncConstants.UnclaimedRequestTimeout);
                logger.LogWarning(
                    "Exchange-rate sync run {RunId} was requested but no worker took it; it is marked failed. Is Odyssey.Worker running?",
                    run.JobRunId);
                continue;
            }

            // Stamped at the latest it can have been alive (the hard timeout), not at discovery: "now"
            // would read as a run that just finished and push the next one back a whole delay again.
            run.Finish(JobRunStatus.Failed, JobFailureCategory.Abandoned,
                run.StartedAtUtc.AddMinutes(ExchangeRateSyncConstants.RunTimeoutMinutes));
            logger.LogWarning("Exchange-rate sync run {RunId} was abandoned mid-run and is marked failed.", run.JobRunId);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A worker claimed an unclaimed request in the same instant (Generation is the concurrency
            // token): it is live after all, so the abandonment is dropped and the caller sees it Running.
            foreach (var run in stale)
            {
                context.Entry(run).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Finishes a run row. Its <see cref="JobRun.FailureCategory"/> is its error kind — the ordinals are equal by
    /// design (issue #409 §4.1.1). Pruning is the run log's retention setting, applied by the worker's
    /// <c>JobRunMaintenanceJob</c>; the newest-500 rule this replaced is gone.
    /// </summary>
    internal async Task FinishAsync(
        ExchangeRateSyncJobRun run, ExchangeRateSyncRunStatus status, ExchangeRateSyncErrorKind? errorKind, CancellationToken cancellationToken)
    {
        run.ErrorKind = errorKind;
        run.Finish((JobRunStatus)status, (JobFailureCategory?)errorKind, Now);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value) => value.Length <= 256 ? value : value[..256];

    internal readonly record struct FetchedExtent(DateOnly Oldest, DateOnly Newest);

    private readonly record struct DateSpan(DateOnly From, DateOnly To);

    /// <summary>One currency's planning state for a run, advanced in memory as its requests commit.</summary>
    private sealed class CurrencyPlan(
        string code, DateOnly effectiveStart, DateOnly? firstObservation, FetchedExtent extent, DateOnly? backfilledFrom)
    {
        private readonly bool hasRows = extent != default;

        public string Code { get; } = code;

        public DateOnly? FirstObservation { get; } = firstObservation;

        public DateOnly? NewestFetched { get; set; } = extent == default ? null : extent.Newest;

        public DateOnly? BackfilledFrom { get; set; } = backfilledFrom ?? (extent == default ? null : extent.Oldest);

        public bool Failed { get; set; }


        /// <summary>
        /// From the day after the newest fetched date to today, capped at one request. A currency with no
        /// fetched rows has none: its whole history is backfill.
        /// </summary>
        public DateSpan? ForwardSpan(DateOnly today)
        {
            if (!hasRows || NewestFetched is not { } newest || newest >= today)
            {
                return null;
            }

            var from = newest.AddDays(1);
            var to = from.AddDays(ExchangeRateSyncConstants.DaysPerRequest - 1);
            return new DateSpan(from, to < today ? to : today);
        }

        /// <summary>The next backfill chunk, ending the day before the watermark (or today), or null when complete.</summary>
        public DateSpan? BackfillSpan(DateOnly today)
        {
            if (IsBackfillComplete(BackfilledFrom, effectiveStart, FirstObservation))
            {
                return null;
            }

            var to = BackfilledFrom is { } watermark ? watermark.AddDays(-1) : today;
            var floor = FirstObservation is { } first && first > effectiveStart ? first : effectiveStart;
            if (to < floor)
            {
                return null;
            }

            var from = to.AddDays(-(ExchangeRateSyncConstants.DaysPerRequest - 1));
            return new DateSpan(from > effectiveStart ? from : effectiveStart, to);
        }
    }

    /// <summary>What one run did, folded into its row when it finishes.</summary>
    private sealed class RunOutcome
    {
        public int RowsInserted { get; set; }

        public int RowsSkipped { get; set; }

        public int RowsRejected { get; set; }

        public bool Committed { get; set; }

        public bool Unsupported { get; set; }

        public bool Stopped { get; set; }

        public ExchangeRateSyncErrorKind? Failure { get; private set; }

        public HashSet<string> Codes { get; } = new(StringComparer.Ordinal);

        public DateOnly? SpanFrom { get; private set; }

        public DateOnly? SpanTo { get; private set; }

        private bool skipped;

        public void Fail(ExchangeRateSyncErrorKind kind) => Failure ??= kind;

        public void Skip() => skipped = true;

        public void Cover(IEnumerable<string> codes, DateSpan span)
        {
            Codes.UnionWith(codes);
            SpanFrom = SpanFrom is { } from && from < span.From ? from : span.From;
            SpanTo = SpanTo is { } to && to > span.To ? to : span.To;
        }

        public ExchangeRateSyncErrorKind? ErrorKind =>
            Failure ?? (Unsupported ? ExchangeRateSyncErrorKind.UnsupportedCurrency : null);

        /// <summary>
        /// A shutdown or the hard timeout is <c>Failed</c> outright (issue #349 §9); any other failure after
        /// a committed request is <c>PartiallySucceeded</c>, since what committed stays.
        /// </summary>
        public ExchangeRateSyncRunStatus Status =>
            skipped && Failure is null ? ExchangeRateSyncRunStatus.Skipped
            : Stopped ? ExchangeRateSyncRunStatus.Failed
            : Failure is not null ? (Committed ? ExchangeRateSyncRunStatus.PartiallySucceeded : ExchangeRateSyncRunStatus.Failed)
            : Unsupported ? ExchangeRateSyncRunStatus.PartiallySucceeded
            : ExchangeRateSyncRunStatus.Succeeded;
    }
}
