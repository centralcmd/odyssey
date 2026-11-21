using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
// The RRULE as read back off a parsed VEVENT: CalendarEvent.RecurrenceRule is typed as the base
// RecurrenceRule, which carries every part MapPattern inspects. IcalRecurrencePattern (its subclass)
// stays the build-side type, since that is what BuildRRule constructs.
using IcalRecurrenceRule = Ical.Net.DataTypes.RecurrenceRule;
using CalDateTime = Ical.Net.DataTypes.CalDateTime;
using WeekDay = Ical.Net.DataTypes.WeekDay;
using FrequencyType = Ical.Net.FrequencyType;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;

namespace Odyssey.Core.Journal;

public partial class CalendarIcsService
{
    // ----- Apply

    // What one attempt reads and writes: the resolver, the series it has loaded under its locks, and its tally.
    private sealed class BatchAttempt(BatchResolver resolver, ImportTally tally)
    {
        public BatchResolver Resolver { get; } = resolver;

        public ImportTally Tally { get; } = tally;

        public Dictionary<Guid, RecurrenceSeriesState> Series { get; } = [];

        public void Skip(string reason, string? sample) => Tally.Skips.Add((reason, sample));

        public void SkipAttachment(string reason)
        {
            Tally.Skips.Add((reason, null));
            Tally.SkippedAttachments++;
        }
    }

    private async Task ApplyGroupAsync(ImportGroup group, ImportRun run, BatchAttempt attempt, CancellationToken cancellationToken)
    {
        if (group.IsSeries)
        {
            await ApplySeriesGroupAsync(group, run, attempt, cancellationToken);
            return;
        }

        // Plain events: applied in file order, so a UID seen again resolves to what the earlier one wrote and the last
        // component wins.
        ContextCalendarEvent? written = null;
        var removed = false;
        foreach (var item in group.Items)
        {
            if (item.Kind == ItemKind.Cancelled)
            {
                removed |= await ApplyCancelledEventAsync(item, run, attempt, removed ? null : written, cancellationToken);
                written = null;
                continue;
            }

            written = await ApplyStandaloneAsync(item, run, attempt, removed ? null : written, cancellationToken);
            removed = false;
        }
    }

    private ContextCalendarEvent? MatchEvent(string? uid, BatchAttempt attempt) =>
        uid is null ? null : Match(uid, attempt.Resolver.Events, e => e.CalendarEventId, e => e.ExternalUid, e => e.ImportSourceUid, context);

    private RecurrencePattern? MatchPattern(string? uid, BatchAttempt attempt) =>
        uid is null ? null : Match(uid, attempt.Resolver.Patterns, p => p.RecurrencePatternId, p => p.ExternalUid, p => p.ImportSourceUid, context);

    // A series this attempt reads under its lock (the lock is the batch's first statement), keyed inline if it is not
    // keyed yet (#519 v4 §4).
    private async Task<RecurrenceSeriesState?> SeriesAsync(
        Guid patternId, ImportRun run, BatchAttempt attempt, CancellationToken cancellationToken)
    {
        if (attempt.Series.TryGetValue(patternId, out var known))
        {
            return known;
        }

        var state = await RecurrenceSeriesState.LoadAsync(context, patternId, tracked: true, cancellationToken);
        if (state is null)
        {
            return null;
        }

        if (state.Pattern.KeysBackfilledAt is null)
        {
            var detachedRows = new List<ContextCalendarEvent>();
            var detached = RecurrenceSeries.KeyInPlace(state, run.Now, run.UserId, x => context.RecurrenceExclusions.Add(x), detachedRows);
            await CalendarLinkService.StageCopyToDetachedAsync(context, patternId, detachedRows, cancellationToken);
            logger.LogInformation(
                "Calendar series {RecurrencePatternId} keyed inline by an import: {DetachedCount} row(s) detached.",
                patternId, detached);
        }

        attempt.Series[patternId] = state;
        return state;
    }

    // A standalone event is never counted against CalendarIcsMaxAggregateOccurrences: that cap bounds series
    // expansion (§3 component 5, Non-Goal 4), and counting plain rows would reinstate a file-wide event ceiling. One
    // that matches a generated row edits that occurrence, as an override (#519 v3 §B.5, AC 28).
    private async Task<ContextCalendarEvent> ApplyStandaloneAsync(
        ImportItem item, ImportRun run, BatchAttempt attempt, ContextCalendarEvent? earlier, CancellationToken cancellationToken)
    {
        var (locationId, detail, keepLink) = ResolvePlace(run.Places, item.Place);
        var existing = earlier ?? MatchEvent(item.Uid, attempt);
        if (existing is not null)
        {
            if (existing.RecurrencePatternId is { } seriesId)
            {
                await SeriesAsync(seriesId, run, attempt, cancellationToken);
            }

            // An occurrence's links are its series' (SF5): a standalone VEVENT resolving to one has its attachments ignored.
            if (existing.RecurrencePatternId is not null)
            {
                attempt.Tally.IgnoredOccurrenceAttachments += item.Attachments.Count;
            }
            else
            {
                await ApplyEventLinksAsync(existing.CalendarEventId, isNew: context.Entry(existing).State == EntityState.Added,
                    item, run, attempt, cancellationToken);
            }

            existing.Title = item.Title;
            existing.Description = item.Description;
            existing.Location = detail;
            if (!keepLink)
            {
                existing.LocationId = locationId;
            }

            existing.StartDateTime = item.Start;
            existing.EndDateTime = item.End;
            existing.IsAllDay = item.IsAllDay;
            existing.TimeZoneId = item.ZoneId;
            if (existing.RecurrencePatternId is not null && existing.OccurrenceKey is not null)
            {
                existing.IsOverride = true;
            }

            // The stored ExternalUid is kept, never cleared (gap 1): a rule-1 match carries the row's own synthetic
            // UID, and a rule-2 match carries the ExternalUid itself.
            existing.UpdatedByUserId = run.UserId;
            existing.UpdatedAt = run.Now;
            attempt.Tally.Updated++;

            CountAdjusted(attempt, item);
            return existing;
        }

        var created = new ContextCalendarEvent
        {
            CalendarEventId = Guid.NewGuid(),
            CalendarId = run.CalendarId,
            Title = item.Title,
            Description = item.Description,
            Location = detail,
            LocationId = keepLink ? null : locationId,
            StartDateTime = item.Start,
            EndDateTime = item.End,
            IsAllDay = item.IsAllDay,
            TimeZoneId = item.ZoneId,
            ExternalUid = PersistableExternalUid(item.Uid),
            ImportSourceUid = ImportSourceUidFor(item.Uid),
            CreatedByUserId = run.UserId,
            CreatedAt = run.Now,
            UpdatedAt = run.Now,
        };
        context.CalendarEvents.Add(created);
        attempt.Tally.Imported++;
        CountAdjusted(attempt, item);
        await ApplyEventLinksAsync(created.CalendarEventId, isNew: true, item, run, attempt, cancellationToken);
        return created;
    }

    // ----- Links (issue #521 §8.3, phase B)

    // A kind's resolved ids, distinct and capped (links beyond the cap are skipped and counted); null when none of that
    // kind resolved or was stored, which leaves the stored set untouched (v3 §A.2).
    private async Task<CalendarLinkService.ImportReplace?> DesiredLinksAsync(
        ImportItem item, ImportRun run, BatchAttempt attempt, CancellationToken cancellationToken)
    {
        if (item.Attachments.Count == 0)
        {
            return null;
        }

        async Task<List<Guid>?> Kind(CalendarLinkKind kind)
        {
            List<Guid>? ids = null;
            foreach (var slot in item.Attachments.Where(s => s.Kind == kind))
            {
                if (await run.Importer.MaterializeAsync(slot, attempt.Tally.Links, cancellationToken) is not { } id)
                {
                    continue;
                }

                ids ??= [];
                if (ids.Contains(id))
                {
                    continue;
                }

                if (ids.Count >= run.MaxLinksPerKind)
                {
                    attempt.SkipAttachment(CalendarAttachmentImporter.CappedReason(run.MaxLinksPerKind));
                    continue;
                }

                ids.Add(id);
            }

            return ids;
        }

        var files = await Kind(CalendarLinkKind.File);
        var photos = await Kind(CalendarLinkKind.Photo);
        return files is null && photos is null ? null : new CalendarLinkService.ImportReplace(files, photos);
    }

    private async Task ApplyEventLinksAsync(
        Guid eventId, bool isNew, ImportItem item, ImportRun run, BatchAttempt attempt, CancellationToken cancellationToken)
    {
        if (await DesiredLinksAsync(item, run, attempt, cancellationToken) is not { } replace)
        {
            return;
        }

        var files = isNew ? [] : await context.CalendarEventAttachments.Where(a => a.CalendarEventId == eventId).ToListAsync(cancellationToken);
        var photos = isNew ? [] : await context.CalendarEventPhotos.Where(p => p.CalendarEventId == eventId).ToListAsync(cancellationToken);
        files.AddRange(context.CalendarEventAttachments.Local.Where(a => a.CalendarEventId == eventId && !files.Contains(a)));
        photos.AddRange(context.CalendarEventPhotos.Local.Where(p => p.CalendarEventId == eventId && !photos.Contains(p)));
        Count(attempt, CalendarLinkService.ReplaceEventLinks(context, eventId, files, photos, replace, run.Now));
    }

    private async Task ApplySeriesLinksAsync(
        Guid patternId, bool isNew, ImportItem item, ImportRun run, BatchAttempt attempt, CancellationToken cancellationToken)
    {
        if (await DesiredLinksAsync(item, run, attempt, cancellationToken) is not { } replace)
        {
            return;
        }

        var files = isNew ? [] : await context.RecurrencePatternAttachments.Where(a => a.RecurrencePatternId == patternId).ToListAsync(cancellationToken);
        var photos = isNew ? [] : await context.RecurrencePatternPhotos.Where(p => p.RecurrencePatternId == patternId).ToListAsync(cancellationToken);
        files.AddRange(context.RecurrencePatternAttachments.Local.Where(a => a.RecurrencePatternId == patternId && !files.Contains(a)));
        photos.AddRange(context.RecurrencePatternPhotos.Local.Where(p => p.RecurrencePatternId == patternId && !photos.Contains(p)));
        Count(attempt, CalendarLinkService.ReplaceSeriesLinks(context, patternId, files, photos, replace, run.Now));
    }

    private static void Count(BatchAttempt attempt, CalendarLinkService.ReplaceOutcome outcome)
    {
        if (outcome.Changed)
        {
            attempt.Tally.ReplacedLinkSets++;
        }

        attempt.Tally.RemovedLinks += outcome.Removed;
    }

    private static void CountAdjusted(BatchAttempt attempt, ImportItem item)
    {
        if (item.Adjusted)
        {
            attempt.Tally.Adjusted++;
        }
    }

    // §8.4: a cancelled plain component removes what it matches in the target calendar, and only with allowRemovals. A
    // generated row it removes is recorded as an exclusion, as an occurrence delete records one.
    private async Task<bool> ApplyCancelledEventAsync(
        ImportItem item, ImportRun run, BatchAttempt attempt, ContextCalendarEvent? earlier, CancellationToken cancellationToken)
    {
        var matched = earlier ?? MatchEvent(item.Uid, attempt);
        if (matched is null)
        {
            if (MatchPattern(item.Uid, attempt) is { } series)
            {
                await ApplyCancelledSeriesAsync(item, series, run, attempt, cancellationToken);
                return true;
            }

            attempt.Skip(CancelledUnknownReason, item.SampleTitle);
            return false;
        }

        if (!run.AllowRemovals)
        {
            attempt.Skip(CancelledKeptReason, item.SampleTitle);
            return false;
        }

        // A row this batch created and the file then cancels never existed in the database: it is un-created, not
        // removed, so neither the import count nor the removal count reports it (architect nit 9).
        var created = context.Entry(matched).State == EntityState.Added;
        if (!created)
        {
            attempt.Tally.RemovedEventIds.Add(matched.CalendarEventId);
        }

        if (matched.RecurrencePatternId is { } seriesId)
        {
            await SeriesAsync(seriesId, run, attempt, cancellationToken);
            if (matched.RecurrencePatternId is not null && matched.OccurrenceKey is { } key)
            {
                AddExclusion(attempt.Series[seriesId], key, run);
                attempt.Series[seriesId].Rows.Remove(matched);
            }
        }

        if (matched.RecurrencePatternId is null)
        {
            await CalendarLinkService.StageRemoveForEventAsync(context, matched.CalendarEventId, cancellationToken);
        }

        context.CalendarEvents.Remove(matched);
        CountRemoval(attempt, run, matched.Title, created);
        return true;
    }

    // Exactly RecurrencePatternService.Delete's semantics (A3): future generated rows are deleted and past ones are
    // detached and kept — wherever they sit — and the exclusions go with the series.
    private async Task ApplyCancelledSeriesAsync(
        ImportItem item, RecurrencePattern series, ImportRun run, BatchAttempt attempt, CancellationToken cancellationToken)
    {
        if (!run.AllowRemovals)
        {
            attempt.Skip(CancelledKeptReason, item.SampleTitle);
            return;
        }

        var state = await SeriesAsync(series.RecurrencePatternId, run, attempt, cancellationToken);
        if (state is null)
        {
            attempt.Skip(CancelledUnknownReason, item.SampleTitle);
            return;
        }

        var created = context.Entry(state.Pattern).State == EntityState.Added;
        attempt.Tally.RemovedEventIds.AddRange(RecurrencePatternService.RemoveSeries(context, state, run.Now));
        // Every detach copies the series' links, and the series' own rows go with it (issue #521 §4).
        await CalendarLinkService.StageCopyToDetachedAsync(
            context, state.Pattern.RecurrencePatternId,
            state.Rows.Where(r => r.RecurrencePatternId is null && context.Entry(r).State != EntityState.Deleted).ToList(),
            cancellationToken);
        await CalendarLinkService.StageRemoveForSeriesAsync(context, state.Pattern.RecurrencePatternId, cancellationToken);
        if (!created)
        {
            attempt.Tally.RemovedSeriesIds.Add(state.Pattern.RecurrencePatternId);
        }

        attempt.Series.Remove(series.RecurrencePatternId);
        CountRemoval(attempt, run, state.Pattern.Title, created);
    }

    private void AddExclusion(RecurrenceSeriesState state, DateTime key, ImportRun run)
    {
        if (state.Exclusions.Any(x => x.OccurrenceKey == key))
        {
            return;
        }

        var exclusion = new RecurrenceExclusion
        {
            RecurrencePatternId = state.Pattern.RecurrencePatternId,
            OccurrenceKey = key,
            CreatedAt = run.Now,
            CreatedByUserId = run.UserId,
        };
        context.RecurrenceExclusions.Add(exclusion);
        state.Exclusions.Add(exclusion);
    }

    // A series group (issue #519 §3 "import of one UID group"): its master (the last one in the file), its EXDATEs and its
    // overrides applied together, to a new series or to the stored one under its lock and the removal gate.
    private async Task ApplySeriesGroupAsync(ImportGroup group, ImportRun run, BatchAttempt attempt, CancellationToken cancellationToken)
    {
        var masters = group.Items.Where(i => i.Kind == ItemKind.Master || (i.Kind == ItemKind.Cancelled && i.HasRule)).ToList();
        var master = masters.LastOrDefault();
        foreach (var other in masters.Take(Math.Max(0, masters.Count - 1)))
        {
            attempt.Skip(DuplicateMasterReason, other.SampleTitle);
        }

        foreach (var plain in group.Items.Where(i => i.Kind == ItemKind.Standalone || (i.Kind == ItemKind.Cancelled && !i.HasRule)))
        {
            attempt.Skip(SharedUidReason, plain.SampleTitle);
        }

        var overrides = group.Items.Where(i => i.Kind is ItemKind.Override or ItemKind.CancelledOverride).ToList();
        var stored = MatchPattern(group.Uid, attempt);

        if (master is { Kind: ItemKind.Cancelled })
        {
            if (stored is not null)
            {
                await ApplyCancelledSeriesAsync(master, stored, run, attempt, cancellationToken);
            }
            else
            {
                attempt.Skip(CancelledUnknownReason, master.SampleTitle);
            }

            foreach (var item in overrides)
            {
                attempt.Skip(OverrideWithoutSeriesReason, item.SampleTitle);
            }

            return;
        }

        if (master is null && stored is null)
        {
            foreach (var item in overrides)
            {
                attempt.Skip(OverrideWithoutSeriesReason, item.SampleTitle);
            }

            return;
        }

        if (stored is null)
        {
            CreateSeries(master!, overrides, run, attempt);
            await ApplySeriesLinksAsync(master!.Pattern!.RecurrencePatternId, isNew: true, master, run, attempt, cancellationToken);
            return;
        }

        var state = await SeriesAsync(stored.RecurrencePatternId, run, attempt, cancellationToken);
        if (state is null)
        {
            foreach (var item in overrides)
            {
                attempt.Skip(OverrideWithoutSeriesReason, item.SampleTitle);
            }

            return;
        }

        if (UpdateSeries(state, master, overrides, run, attempt) && master is not null)
        {
            await ApplySeriesLinksAsync(
                state.Pattern.RecurrencePatternId, isNew: context.Entry(state.Pattern).State == EntityState.Added, master, run, attempt,
                cancellationToken);
        }
    }

    // A series the calendar does not have yet: exclusions on it remove nothing, so they need no opt-in.
    private void CreateSeries(ImportItem master, List<ImportItem> overrides, ImportRun run, BatchAttempt attempt)
    {
        var pattern = master.Pattern!;
        var (locationId, detail, keepLink) = ResolvePlace(run.Places, master.Place);
        pattern.RecurrencePatternId = Guid.NewGuid();
        pattern.Location = detail;
        pattern.LocationId = keepLink ? null : locationId;
        pattern.ExternalUid = PersistableExternalUid(master.Uid);
        pattern.ImportSourceUid = ImportSourceUidFor(master.Uid);
        pattern.Version = Guid.NewGuid();
        pattern.KeysBackfilledAt = run.Now;

        var byKey = master.Occurrences.ToDictionary(o => o.Key);
        var excluded = new HashSet<DateTime>();
        foreach (var key in master.ExDates)
        {
            if (byKey.ContainsKey(key))
            {
                excluded.Add(key);
            }
            else
            {
                attempt.Skip(StrayExclusionReason, master.SampleTitle);
            }
        }

        var applied = new Dictionary<DateTime, ImportItem>();
        foreach (var item in overrides)
        {
            if (item.RecurrenceId is not { } at || ToKey(at, pattern) is not { } key || !byKey.ContainsKey(key))
            {
                attempt.Skip(OverrideMismatchReason, item.SampleTitle);
                continue;
            }

            if (item.Kind == ItemKind.CancelledOverride)
            {
                excluded.Add(key);
                applied.Remove(key);
            }
            else if (!excluded.Contains(key))
            {
                applied[key] = item;
            }
            else
            {
                attempt.Skip(OverrideMismatchReason, item.SampleTitle);
            }
        }

        CountOccurrences(attempt, run, master.Occurrences.Count - excluded.Count);
        context.RecurrencePatterns.Add(pattern);
        var rows = new List<ContextCalendarEvent>();
        foreach (var occurrence in master.Occurrences.Where(o => !excluded.Contains(o.Key)))
        {
            var row = RecurrenceSeries.NewOccurrence(pattern, occurrence, run.UserId, run.Now);
            context.CalendarEvents.Add(row);
            rows.Add(row);
            if (occurrence.Adjusted)
            {
                attempt.Tally.Adjusted++;
            }
        }

        var state = new RecurrenceSeriesState { Pattern = pattern, Rows = rows, Exclusions = [] };
        foreach (var key in excluded)
        {
            AddExclusion(state, key, run);
        }

        foreach (var (key, item) in applied)
        {
            ApplyOverrideFields(rows.First(r => r.OccurrenceKey == key), item, run);
        }

        attempt.Tally.ExclusionsApplied += excluded.Count;
        attempt.Tally.OverridesApplied += applied.Count;
        attempt.Series[pattern.RecurrencePatternId] = state;
        attempt.Tally.Imported++;
    }

    // A stored series: the master (when the file has one) reconciles it like a series edit; the file's EXDATEs and
    // overrides apply on top. Anything that would remove a stored row or discard a stored exception is gated on
    // allowRemovals, and a gated group changes nothing (§3 step 3). Past keys never remove rows (SR5).
    // False when the group was skipped or gated: its links are then left as they are too.
    private bool UpdateSeries(
        RecurrenceSeriesState state, ImportItem? master, List<ImportItem> overrides, ImportRun run, BatchAttempt attempt)
    {
        RecurrenceSeriesPlan? plan = null;
        RecurrencePattern target = state.Pattern;
        (Guid? LocationId, string? Detail, bool KeepLink) place = default;
        if (master is not null)
        {
            target = RecurrenceSeries.CloneRule(master.Pattern!);
            target.RecurrencePatternId = state.Pattern.RecurrencePatternId;
            place = ResolvePlace(run.Places, master.Place);
            target.Location = place.Detail;
            target.LocationId = place.KeepLink ? state.Pattern.LocationId : place.LocationId;
            try
            {
                plan = RecurrenceSeries.Plan(state, target, run.Now, run.MaxGeneratedOccurrences);
            }
            catch (DomainValidationException ex)
            {
                attempt.Skip(ex.Message, master.SampleTitle);
                return false;
            }
        }

        var keys = plan?.KeySet ?? RecurrenceOccurrenceGenerator.Generate(state.Pattern, RecurrenceSeries.KeyingCeiling)
            .Select(o => o.Key).ToHashSet();
        var nowKey = RecurrenceSeries.NowKey(target, run.Now);
        var removedByPlan = plan?.RowRemovals.ToHashSet() ?? [];
        var exclusionsDroppedByPlan = plan?.ExclusionRemovals.ToHashSet() ?? [];
        var rowsByKey = state.Rows
            .Where(r => r.OccurrenceKey is not null && !removedByPlan.Contains(r))
            .ToDictionary(r => r.OccurrenceKey!.Value);
        var storedExclusions = state.Exclusions
            .Where(x => !exclusionsDroppedByPlan.Contains(x))
            .ToDictionary(x => x.OccurrenceKey);
        var sample = master?.SampleTitle ?? overrides.FirstOrDefault()?.SampleTitle ?? UntitledEvent;

        var newExclusions = new HashSet<DateTime>();
        var extraRemovals = new List<ContextCalendarEvent>();
        void Exclude(DateTime key, string title)
        {
            if (!keys.Contains(key))
            {
                attempt.Skip(StrayExclusionReason, title);
                return;
            }

            if (storedExclusions.ContainsKey(key) || !newExclusions.Add(key))
            {
                return;
            }

            if (rowsByKey.TryGetValue(key, out var row))
            {
                if (key < nowKey)
                {
                    newExclusions.Remove(key);
                    attempt.Skip(PastRemovalReason, title);
                    return;
                }

                extraRemovals.Add(row);
            }
        }

        foreach (var key in master?.ExDates ?? [])
        {
            Exclude(key, master!.SampleTitle);
        }

        var applied = new Dictionary<DateTime, ImportItem>();
        var discardedExclusions = new List<RecurrenceExclusion>();
        foreach (var item in overrides)
        {
            if (item.RecurrenceId is not { } at || ToKey(at, target) is not { } key || !keys.Contains(key))
            {
                attempt.Skip(OverrideMismatchReason, item.SampleTitle);
                continue;
            }

            if (item.Kind == ItemKind.CancelledOverride)
            {
                applied.Remove(key);
                Exclude(key, item.SampleTitle);
                continue;
            }

            if (newExclusions.Contains(key))
            {
                attempt.Skip(OverrideMismatchReason, item.SampleTitle);
                continue;
            }

            if (storedExclusions.TryGetValue(key, out var exclusion))
            {
                discardedExclusions.Add(exclusion);
                storedExclusions.Remove(key);
            }

            applied[key] = item;
        }

        var gated = (plan?.Counts.RemovesOrDiscards ?? false) || extraRemovals.Count > 0 || discardedExclusions.Count > 0;
        if (gated && !run.AllowRemovals)
        {
            attempt.Skip(GatedReason, sample);
            attempt.Tally.Gated++;
            return false;
        }

        if (plan is not null)
        {
            plan.Inserts.RemoveAll(o => newExclusions.Contains(o.Key));
            CountOccurrences(attempt, run, plan.Inserts.Count + plan.Updates.Count);
            attempt.Tally.RemovedEventIds.AddRange(plan.RowRemovals.Select(r => r.CalendarEventId));
            attempt.Tally.DroppedOverrideKeys.AddRange(plan.RowRemovals.Where(r => r.IsOverride).Select(r => r.OccurrenceKey!.Value));
            attempt.Tally.DroppedExclusionKeys.AddRange(plan.ExclusionRemovals.Select(x => x.OccurrenceKey));
            attempt.Tally.DroppedOverrides += plan.Counts.DroppedOverrides;
            attempt.Tally.DroppedExclusions += plan.Counts.DroppedExclusions;
            attempt.Tally.Adjusted += plan.Counts.Adjusted;
            attempt.Tally.AnySeriesRegenerated |= plan.Counts.DroppedOverrides > 0;
            RecurrenceSeries.Apply(context, state, plan, run.UserId, run.Now);
            attempt.Tally.Updated++;
        }

        foreach (var row in extraRemovals)
        {
            attempt.Tally.RemovedEventIds.Add(row.CalendarEventId);
            context.CalendarEvents.Remove(row);
            state.Rows.Remove(row);
        }

        foreach (var exclusion in discardedExclusions)
        {
            attempt.Tally.DroppedExclusionKeys.Add(exclusion.OccurrenceKey);
            attempt.Tally.DroppedExclusions++;
            context.RecurrenceExclusions.Remove(exclusion);
            state.Exclusions.Remove(exclusion);
        }

        foreach (var key in newExclusions)
        {
            AddExclusion(state, key, run);
        }

        var occurrences = plan?.Occurrences ?? RecurrenceOccurrenceGenerator.Generate(state.Pattern, RecurrenceSeries.KeyingCeiling);
        var occurrenceByKey = occurrences.ToDictionary(o => o.Key);
        foreach (var (key, item) in applied)
        {
            var row = state.Rows.FirstOrDefault(r => r.OccurrenceKey == key);
            if (row is null)
            {
                row = RecurrenceSeries.NewOccurrence(state.Pattern, occurrenceByKey[key], run.UserId, run.Now);
                context.CalendarEvents.Add(row);
                state.Rows.Add(row);
            }

            ApplyOverrideFields(row, item, run);
        }

        attempt.Tally.ExclusionsApplied += newExclusions.Count;
        attempt.Tally.OverridesApplied += applied.Count;
        return true;
    }

    private void ApplyOverrideFields(ContextCalendarEvent row, ImportItem item, ImportRun run)
    {
        var (locationId, detail, keepLink) = ResolvePlace(run.Places, item.Place);
        row.Title = item.Title;
        row.Description = item.Description;
        row.Location = detail;
        if (!keepLink)
        {
            row.LocationId = locationId;
        }

        row.StartDateTime = item.Start;
        row.EndDateTime = item.End;
        row.IsAllDay = item.IsAllDay;
        row.TimeZoneId = item.IsAllDay ? null : item.ZoneId;
        row.IsOverride = true;
        row.UpdatedByUserId = run.UserId;
        row.UpdatedAt = run.Now;
    }

    private static void CountRemoval(BatchAttempt attempt, ImportRun run, string title, bool createdInThisBatch)
    {
        if (createdInThisBatch)
        {
            attempt.Tally.Imported--;
            return;
        }

        attempt.Tally.Removed++;
        if (attempt.Tally.RemovedSamples.Count < run.MaxSamples)
        {
            attempt.Tally.RemovedSamples.Add(title);
        }
    }

    // Generated series occurrences only, cumulative across the whole import (§3 pipeline step 3).
    private static void CountOccurrences(BatchAttempt attempt, ImportRun run, int count)
    {
        attempt.Tally.Aggregate += count;
        if (run.Totals.Aggregate + attempt.Tally.Aggregate > run.MaxAggregateOccurrences)
        {
            throw new DomainValidationException(
                $"The import would create or regenerate more than {run.MaxAggregateOccurrences} series occurrences.");
        }
    }

    // Maps an ICS RRULE onto the bounded RecurrencePattern model. Returns (pattern, null) on success
    // or (null, reason) when the rule doesn't map — the reason becomes the per-event skip message. The default
    // weekday, day of month and month come from the LOCAL start (AC 2).
    private static (RecurrencePattern? Pattern, string? Reason) MapPattern(
        Guid calendarId, IcalRecurrenceRule rule, string title, string? description, string? location,
        ImportTimes times, bool isAllDay, string userId, DateTime now)
    {
        RecurrenceFrequency frequency;
        switch (rule.Frequency)
        {
            case FrequencyType.Daily:
                frequency = RecurrenceFrequency.Daily;
                break;
            case FrequencyType.Weekly:
                frequency = RecurrenceFrequency.Weekly;
                break;
            case FrequencyType.Monthly:
                frequency = RecurrenceFrequency.Monthly;
                break;
            case FrequencyType.Yearly:
                frequency = RecurrenceFrequency.Yearly;
                break;
            default:
                return (null, "Unsupported recurrence frequency.");
        }

        // WKST (rule.FirstDayOfWeek) is recognized and ignored — Odyssey's generator always uses
        // Monday-start weeks. Every other unrecognized part is a hard skip.
        if (rule.BySetPosition.Count > 0 || rule.ByWeekNo.Count > 0 || rule.ByYearDay.Count > 0
            || rule.ByHour.Count > 0 || rule.ByMinute.Count > 0 || rule.BySecond.Count > 0)
        {
            return (null, "Unsupported recurrence rule.");
        }

        if (rule.ByDay.Any(d => d.Offset.HasValue))
        {
            return (null, "Unsupported BYDAY ordinal.");
        }

        var interval = rule.Interval <= 0 ? 1 : rule.Interval;
        if (interval > 365)
        {
            return (null, "Recurrence interval exceeds the maximum of 365.");
        }

        var hasCount = rule.Count.HasValue;
        var hasUntil = rule.Until is not null;
        if (hasCount == hasUntil)
        {
            return (null, "A recurrence rule must set exactly one of COUNT or UNTIL.");
        }

        int? occurrenceCount = hasCount ? rule.Count : null;
        if (occurrenceCount is { } oc && (oc < 1 || oc > 730))
        {
            return (null, "Recurrence count exceeds the maximum of 730.");
        }

        DateTime? recurrenceEnd = null;
        if (hasUntil && !TryReadUntil(rule.Until!, isAllDay, times.Zone, out recurrenceEnd))
        {
            return (null, UnknownTimeZoneReason);
        }

        var local = isAllDay || times.Zone is null
            ? times.Start
            : CalendarTimeZones.ToWall(times.Start, times.Zone);
        DaysOfWeekFlags? daysOfWeek = null;
        int? dayOfMonth = null;
        int? monthOfYear = null;

        switch (frequency)
        {
            case RecurrenceFrequency.Daily:
                if (rule.ByDay.Count > 0 || rule.ByMonthDay.Count > 0 || rule.ByMonth.Count > 0)
                {
                    return (null, "Unsupported recurrence rule.");
                }

                break;

            case RecurrenceFrequency.Weekly:
                if (rule.ByMonthDay.Count > 0 || rule.ByMonth.Count > 0)
                {
                    return (null, "Unsupported recurrence rule.");
                }

                daysOfWeek = rule.ByDay.Count > 0 ? ToDaysOfWeekFlags(rule.ByDay) : ToSingleDay(local.DayOfWeek);
                break;

            case RecurrenceFrequency.Monthly:
                if (rule.ByDay.Count > 0 || rule.ByMonth.Count > 0)
                {
                    return (null, "Unsupported recurrence rule.");
                }

                if (rule.ByMonthDay.Count > 1)
                {
                    return (null, "Multiple BYMONTHDAY values are not supported.");
                }

                dayOfMonth = rule.ByMonthDay.Count == 1 ? rule.ByMonthDay[0] : local.Day;
                if (dayOfMonth is < 1 or > 31)
                {
                    return (null, "Unsupported BYMONTHDAY value.");
                }

                break;

            case RecurrenceFrequency.Yearly:
                if (rule.ByDay.Count > 0)
                {
                    return (null, "Unsupported recurrence rule.");
                }

                if (rule.ByMonthDay.Count > 1 || rule.ByMonth.Count > 1)
                {
                    return (null, "Multiple BYMONTHDAY/BYMONTH values are not supported.");
                }

                dayOfMonth = rule.ByMonthDay.Count == 1 ? rule.ByMonthDay[0] : local.Day;
                if (dayOfMonth is < 1 or > 31)
                {
                    return (null, "Unsupported BYMONTHDAY value.");
                }

                monthOfYear = rule.ByMonth.Count == 1 ? rule.ByMonth[0] : local.Month;
                if (monthOfYear is < 1 or > 12)
                {
                    return (null, "Unsupported BYMONTH value.");
                }

                break;
        }

        var pattern = new RecurrencePattern
        {
            CalendarId = calendarId,
            Title = title,
            Description = description,
            Location = location,
            StartDateTime = times.Start,
            EndDateTime = times.End,
            IsAllDay = isAllDay,
            TimeZoneId = isAllDay ? null : times.Zone?.Id,
            Frequency = frequency,
            Interval = interval,
            DaysOfWeek = daysOfWeek,
            DayOfMonth = dayOfMonth,
            MonthOfYear = monthOfYear,
            RecurrenceEndDate = recurrenceEnd,
            OccurrenceCount = occurrenceCount,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        return (pattern, null);
    }

    // UNTIL as stored (#519 v3 §C): inclusive; a UTC instant on a zoned series, a wall-clock on a floating one; a
    // date-only value means the end of that day in the series' zone.
    private static bool TryReadUntil(CalDateTime until, bool isAllDay, TimeZoneInfo? zone, out DateTime? stored)
    {
        stored = null;
        if (isAllDay)
        {
            stored = DateTime.SpecifyKind(until.Value.Date, DateTimeKind.Utc);
            return true;
        }

        if (!until.HasTime)
        {
            var endOfDay = DateTime.SpecifyKind(until.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Unspecified);
            stored = zone is null ? endOfDay : CalendarTimeZones.ToUtc(endOfDay, zone);
            return true;
        }

        DateTime instant;
        if (until.IsUtc)
        {
            instant = DateTime.SpecifyKind(until.Value, DateTimeKind.Utc);
        }
        else if (until.TzId is { Length: > 0 } tzid)
        {
            if (CalendarTimeZones.ResolveFileZone(tzid) is not { } untilZone)
            {
                return false;
            }

            instant = CalendarTimeZones.ToUtc(until.Value, untilZone);
        }
        else
        {
            var wall = DateTime.SpecifyKind(until.Value, DateTimeKind.Unspecified);
            stored = zone is null ? wall : CalendarTimeZones.ToUtc(wall, zone);
            return true;
        }

        stored = zone is null ? DateTime.SpecifyKind(instant, DateTimeKind.Unspecified) : instant;
        return true;
    }

    private static DaysOfWeekFlags ToDaysOfWeekFlags(IEnumerable<WeekDay> days)
    {
        var flags = DaysOfWeekFlags.None;
        foreach (var day in days)
        {
            flags |= ToSingleDay(day.DayOfWeek);
        }

        return flags;
    }

    private static DaysOfWeekFlags ToSingleDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => DaysOfWeekFlags.Monday,
        DayOfWeek.Tuesday => DaysOfWeekFlags.Tuesday,
        DayOfWeek.Wednesday => DaysOfWeekFlags.Wednesday,
        DayOfWeek.Thursday => DaysOfWeekFlags.Thursday,
        DayOfWeek.Friday => DaysOfWeekFlags.Friday,
        DayOfWeek.Saturday => DaysOfWeekFlags.Saturday,
        _ => DaysOfWeekFlags.Sunday,
    };
}
