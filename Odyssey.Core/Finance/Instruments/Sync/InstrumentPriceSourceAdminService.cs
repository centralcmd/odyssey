using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.Instruments.Sync;

/// <summary>
/// The Admin surface for price sources (issue #446 §5 endpoints 16–18): the registered sources and the sync status,
/// and each instrument's binding list, replaced as one ordered list. It makes no outbound call.
/// </summary>
public sealed class InstrumentPriceSourceAdminService(
    OdysseyContext context,
    InstrumentPriceSourceCatalogue sources,
    InstrumentPriceSyncService sync,
    ISecretSettingsReader secrets,
    IInstrumentPriceSettingsLookup settings,
    TimeProvider timeProvider,
    ILogger<InstrumentPriceSourceAdminService> logger)
{
    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<InstrumentPriceSourcesStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var today = OsloDate.Today(timeProvider);
        var hour = SourceRequestQuota.HourOf(Now);
        var keys = sources.All.Select(s => s.Key).ToList();
        var states = await context.InstrumentPriceSourceStates.AsNoTracking()
            .Where(s => keys.Contains(s.SourceKey))
            .ToDictionaryAsync(s => s.SourceKey, cancellationToken);

        var infos = new List<InstrumentPriceSourceInfo>();
        foreach (var source in sources.All.OrderBy(s => s.DisplayName, StringComparer.Ordinal))
        {
            states.TryGetValue(source.Key, out var state);
            // A bucket for a later hour (another replica's clock ran ahead) still counts: the bucket only moves forward.
            var requestsThisHour = state?.RequestHourUtc is { } bucket && bucket >= hour ? state.RequestHourCount : 0;
            var configured = true;
            foreach (var key in source.SecretKeys)
            {
                configured &= (await secrets.GetAsync(key, cancellationToken)).State == SecretReadState.Found;
            }

            infos.Add(new InstrumentPriceSourceInfo
            {
                Key = source.Key,
                DisplayName = source.DisplayName,
                Attribution = source.Attribution,
                SupportedTypes = source.SupportedTypes.OrderBy(t => t).ToList(),
                Configured = configured,
                BackOffUntilUtc = state?.BackOffUntilUtc > Now ? state.BackOffUntilUtc : null,
                BackOffReason = state?.BackOffUntilUtc > Now ? state.BackOffReason : null,
                RequestsToday = state?.RequestDay == today ? state.RequestCount : 0,
                MaxRequestsPerDay = source.Limits.MaxRequestsPerDay,
                MaxRequestsPerHour = source.Limits.MaxRequestsPerHour,
                RequestsThisHour = requestsThisHour,
                HourlyLimitResetsAtUtc = source.Limits.MaxRequestsPerHour is { } perHour && requestsThisHour >= perHour
                    ? hour.AddHours(1)
                    : null,
                SymbolExample = source.SymbolExample,
                DataProcessingNote = source.DataProcessingNote,
            });
        }

        var failures = await context.Set<InstrumentPriceSyncJobRun>().AsNoTracking()
            .Where(r => r.Trigger != JobRunTrigger.Purge && r.FinishedAtUtc != null && r.Status != JobRunStatus.Skipped)
            .OrderByDescending(r => r.FinishedAtUtc)
            .Take(20)
            .Select(r => r.Status)
            .ToListAsync(cancellationToken);

        return new InstrumentPriceSourcesStatus
        {
            Sources = infos,
            MaxBindingsPerInstrument = InstrumentPriceBindingLimits.MaxBindingsPerInstrument,
            NextRunAtUtc = await sync.NextRunAtUtcAsync(cancellationToken),
            ConsecutiveFailures = failures.TakeWhile(s => s == JobRunStatus.Failed).Count(),
            CurrentRunId = await context.JobRuns.AsNoTracking()
                .Where(r => r.JobType == JobType.InstrumentPriceSync && r.Status == JobRunStatus.Running)
                .Select(r => (Guid?)r.JobRunId)
                .FirstOrDefaultAsync(cancellationToken),
            CatchUpDelayMinutes = InstrumentPriceSyncConstants.CatchUpDelayMinutes,
            RunTimeoutMinutes = InstrumentPriceSyncConstants.RunTimeoutMinutes,
            ManualRunCooldownSeconds = InstrumentPriceSyncConstants.ManualRunCooldownSeconds,
        };
    }

    public async Task<List<ExistingInstrumentPriceBinding>?> GetBindingsAsync(Guid instrumentId, CancellationToken cancellationToken = default)
    {
        if (!await context.Instruments.AnyAsync(i => i.InstrumentId == instrumentId, cancellationToken))
        {
            return null;
        }

        var bindings = await context.InstrumentPriceSourceBindings.AsNoTracking()
            .Where(b => b.InstrumentId == instrumentId)
            .OrderBy(b => b.Priority)
            .ToListAsync(cancellationToken);
        var history = (await settings.GetAsync(cancellationToken)).SyncHistoryDays;
        var today = OsloDate.Today(timeProvider);
        return bindings.Select(b => ToDto(b, today, history)).ToList();
    }

    /// <summary>
    /// Replaces the instrument's whole binding list atomically (issue #446 §5 endpoint 18). Priority is position. A
    /// kept binding keeps its watermarks and failure state; a removed one is deleted (its prices stay); a changed
    /// source symbol resets that binding's watermarks.
    /// </summary>
    public async Task<List<ExistingInstrumentPriceBinding>?> ReplaceBindingsAsync(
        Guid instrumentId, InstrumentPriceBindingsUpdate update, string? userId, CancellationToken cancellationToken = default)
    {
        var instrument = await context.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.InstrumentId == instrumentId, cancellationToken);
        if (instrument is null)
        {
            return null;
        }

        // An archived instrument is not synced, so it may not gain bindings; clearing them is always allowed.
        if (instrument.Archived is not null && update.Bindings.Count > 0)
        {
            throw new DomainUnprocessableException("Archived instruments are not synced.", "instrumentId");
        }

        if (update.Bindings.Count > InstrumentPriceBindingLimits.MaxBindingsPerInstrument)
        {
            throw new DomainUnprocessableException(
                $"An instrument has at most {InstrumentPriceBindingLimits.MaxBindingsPerInstrument} sources.", "bindings");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<IInstrumentPriceSource>(update.Bindings.Count);
        for (var i = 0; i < update.Bindings.Count; i++)
        {
            var input = update.Bindings[i];
            var key = input.SourceKey.Trim();
            if (sources.Find(key) is not { } source || !seen.Add(key))
            {
                throw new DomainUnprocessableException(
                    seen.Contains(key) && sources.Find(key) is not null ? "This source is listed twice." : "This price source is not installed.",
                    $"bindings[{i}].sourceKey");
            }

            if (!Regex.IsMatch(input.SourceSymbol, InstrumentIdentifierRules.SourceSymbolPattern) || InstrumentIdentifierRules.HasDotSegment(input.SourceSymbol))
            {
                throw new DomainUnprocessableException(
                    "Use letters, digits, dots, hyphens and underscores, without '..'.", $"bindings[{i}].sourceSymbol");
            }

            if (!source.SupportedTypes.Contains(instrument.Type))
            {
                throw new DomainUnprocessableException($"{source.DisplayName} does not price this kind of instrument.", $"bindings[{i}]");
            }

            resolved.Add(source);
        }

        await InstrumentLocks.InTransactionAsync(context, async () =>
        {
            await InstrumentLocks.LockAsync(context, instrumentId, cancellationToken);
            var existing = await context.InstrumentPriceSourceBindings
                .Where(b => b.InstrumentId == instrumentId)
                .ToListAsync(cancellationToken);

            // Priorities are unique per instrument and CHECKed to 1–9, so rows cannot be re-ordered in place. Inside the
            // transaction every row is removed, then the list is written again in its new order, carrying each kept
            // binding's watermarks and failure state across.
            var previous = existing.ToDictionary(b => b.SourceKey, StringComparer.Ordinal);
            context.InstrumentPriceSourceBindings.RemoveRange(existing);
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();

            var now = Now;
            for (var i = 0; i < update.Bindings.Count; i++)
            {
                var input = update.Bindings[i];
                var key = input.SourceKey.Trim();
                var row = new InstrumentPriceSourceBinding { InstrumentId = instrumentId, SourceKey = key, SourceSymbol = input.SourceSymbol };
                previous.TryGetValue(key, out var kept);
                if (kept is not null && string.Equals(kept.SourceSymbol, input.SourceSymbol, StringComparison.Ordinal))
                {
                    row.NewestFetched = kept.NewestFetched;
                    row.BackfilledFrom = kept.BackfilledFrom;
                    row.LastSuccessUtc = kept.LastSuccessUtc;
                    row.LastFailure = kept.LastFailure;
                    row.LastFailureUtc = kept.LastFailureUtc;
                    row.ConsecutiveFailures = kept.ConsecutiveFailures;
                }

                row.Priority = i + 1;
                row.Enabled = input.Enabled;
                row.HistoryStartDate = input.HistoryStartDate;
                row.UpdatedAtUtc = now;
                row.UpdatedByUserId = userId;

                // An unchanged binding keeps its edit stamp: the stamp is what clears a binding skipped for the rest of
                // the day after an UnsupportedInput failure (issue #447 A9), and re-saving the list must not do that
                // for a binding nobody touched.
                if (kept is not null && string.Equals(kept.SourceSymbol, input.SourceSymbol, StringComparison.Ordinal)
                    && kept.Priority == row.Priority && kept.Enabled == row.Enabled && kept.HistoryStartDate == row.HistoryStartDate)
                {
                    row.UpdatedAtUtc = kept.UpdatedAtUtc;
                    row.UpdatedByUserId = kept.UpdatedByUserId;
                }
                context.InstrumentPriceSourceBindings.Add(row);
            }

            await context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

        logger.LogInformation("Price sources of instrument {InstrumentId} set to [{Sources}] by {UserId}.",
            instrumentId,
            // The keys come from the compiled catalogue, never from the request body, so nothing caller-typed reaches the log.
            string.Join(", ", resolved.Select((source, i) => $"{source.Key}:{(update.Bindings[i].Enabled ? "on" : "off")}")),
            userId);
        return await GetBindingsAsync(instrumentId, cancellationToken);
    }

    private ExistingInstrumentPriceBinding ToDto(InstrumentPriceSourceBinding binding, DateOnly today, int history) => new()
    {
        SourceKey = binding.SourceKey,
        SourceSymbol = binding.SourceSymbol,
        Priority = binding.Priority,
        Enabled = binding.Enabled,
        HistoryStartDate = binding.HistoryStartDate,
        NewestFetched = binding.NewestFetched,
        BackfilledFrom = binding.BackfilledFrom,
        BackfillComplete = binding.BackfilledFrom is { } from && from <= (binding.HistoryStartDate ?? today.AddDays(-history)),
        LastSuccessUtc = binding.LastSuccessUtc,
        LastFailure = binding.LastFailure,
        LastFailureUtc = binding.LastFailureUtc,
        ConsecutiveFailures = binding.ConsecutiveFailures,
        RetryOn = InstrumentPriceSyncService.IsSkippedToday(binding, today) ? today.AddDays(1) : null,
        SourceRegistered = sources.Find(binding.SourceKey) is not null,
    };
}
