using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Journal.Interop;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using CalDateTime = Ical.Net.DataTypes.CalDateTime;
using RecurrenceRange = Ical.Net.DataTypes.RecurrenceRange;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;

namespace Odyssey.Core.Journal;

public partial class CalendarIcsService
{
    // ----- Classification

    private ImportItem? Classify(IcalEvent? ev, ImportRun run, long offset)
    {
        if (ev is null)
        {
            run.Skipped.Add("Event could not be parsed.", UntitledEvent);
            return null;
        }

        var title = ev.Summary?.Trim();
        var sampleTitle = string.IsNullOrWhiteSpace(title) ? UntitledEvent : title;
        var uid = string.IsNullOrWhiteSpace(ev.Uid) ? null : ev.Uid.Trim();

        // ExternalUid is varchar(255): a longer UID would fail the whole batch's save under strict mode, and the same
        // file could then never complete, so it is that event's skip (architect finding 4).
        if (uid is { Length: > MaxUidLength })
        {
            run.Skipped.Add(UidTooLongReason, sampleTitle);
            return null;
        }

        var cancelled = string.Equals(ev.Status?.Trim(), "CANCELLED", StringComparison.OrdinalIgnoreCase);
        FileDate? recurrenceId = null;
        if (ev.Properties.ContainsKey("RECURRENCE-ID"))
        {
            if (uid is null)
            {
                run.Skipped.Add(OverrideWithoutSeriesReason, sampleTitle);
                return null;
            }

            if (ev.RecurrenceIdentifier is not { } identifier || identifier.StartTime is null)
            {
                run.Skipped.Add(OverrideMismatchReason, sampleTitle);
                return null;
            }

            if (identifier.Range == RecurrenceRange.ThisAndFuture)
            {
                run.Skipped.Add(ThisAndFutureReason, sampleTitle);
                return null;
            }

            var at = identifier.StartTime;
            recurrenceId = new FileDate(at.Value, at.TzId, at.IsUtc, at.HasTime);
            if (cancelled)
            {
                return new ImportItem
                {
                    Kind = ItemKind.CancelledOverride,
                    SampleTitle = sampleTitle,
                    Uid = uid,
                    Offset = offset,
                    RecurrenceId = recurrenceId,
                };
            }
        }
        else if (cancelled)
        {
            if (uid is null)
            {
                run.Skipped.Add(CancelledUnknownReason, sampleTitle);
                return null;
            }

            return new ImportItem
            {
                Kind = ItemKind.Cancelled,
                SampleTitle = sampleTitle,
                Uid = uid,
                Offset = offset,
                HasRule = ev.RecurrenceRule is not null,
            };
        }

        if (ev.Start is null)
        {
            run.Skipped.Add("Event is missing a start date (DTSTART).", sampleTitle);
            return null;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            run.Skipped.Add("Event is missing a title (SUMMARY).", sampleTitle);
            return null;
        }

        if (title.Length > 200)
        {
            run.Skipped.Add("Title exceeds the maximum length of 200 characters.", sampleTitle);
            return null;
        }

        var description = ev.Description;
        if (description is { Length: > 2000 })
        {
            run.Skipped.Add("Description exceeds the maximum length of 2000 characters.", sampleTitle);
            return null;
        }

        var place = IcsPlaces.ReadEventPlace(ev);
        if (place.Detail is { Length: > MaxLocationLength } || (!place.HasDetailProperty && ev.Location is { Length: > MaxLocationLength }))
        {
            run.Skipped.Add("Location exceeds the maximum length of 300 characters.", sampleTitle);
            return null;
        }

        var isAllDay = !ev.Start.HasTime;
        if (!TryReadTimes(ev, isAllDay, run.DefaultZone, out var times, out var timeReason))
        {
            run.Skipped.Add(timeReason!, sampleTitle);
            return null;
        }

        try
        {
            CalendarEventService.ValidateTimes(times.StartUtc, times.EndUtc, isAllDay, run.MaxEventDurationDays);
        }
        catch (DomainValidationException ex)
        {
            run.Skipped.Add(ex.Message, sampleTitle);
            return null;
        }

        if (recurrenceId is not null)
        {
            return new ImportItem
            {
                Kind = ItemKind.Override,
                SampleTitle = sampleTitle,
                Uid = uid,
                Offset = offset,
                Title = title,
                Description = description,
                Place = place,
                Start = times.Start,
                End = times.End,
                IsAllDay = isAllDay,
                ZoneId = times.Zone?.Id,
                Adjusted = times.Adjusted,
                RecurrenceId = recurrenceId,
            };
        }

        if (ev.RecurrenceRule is null)
        {
            return new ImportItem
            {
                Kind = ItemKind.Standalone,
                SampleTitle = sampleTitle,
                Uid = uid,
                Offset = offset,
                Title = title,
                Description = description,
                Place = place,
                Start = times.Start,
                End = times.End,
                IsAllDay = isAllDay,
                ZoneId = times.Zone?.Id,
                Adjusted = times.Adjusted,
            };
        }

        if (ev.Properties.ContainsKey("RDATE"))
        {
            run.Skipped.Add(RecurrenceDatesReason, sampleTitle);
            return null;
        }

        // Ical.Net exposes only a single RecurrenceRule, and no EXRULE at all, so "more
        // recurrence than v1 models" has to be read off the raw property list.
        if (ev.Properties.CountOf("RRULE") > 1 || ev.Properties.ContainsKey("EXRULE"))
        {
            run.Skipped.Add("Unsupported recurrence rule.", sampleTitle);
            return null;
        }

        var (pattern, reason) = MapPattern(run.CalendarId, ev.RecurrenceRule, title, description, place.Detail, times, isAllDay,
            run.UserId, run.Now);
        if (pattern is null)
        {
            run.Skipped.Add(reason!, sampleTitle);
            return null;
        }

        List<GeneratedOccurrence> occurrences;
        try
        {
            occurrences = RecurrenceOccurrenceGenerator.Generate(pattern, run.MaxGeneratedOccurrences);
        }
        catch (DomainValidationException ex)
        {
            run.Skipped.Add(ex.Message, sampleTitle);
            return null;
        }

        if (occurrences.Count == 0)
        {
            run.Skipped.Add("Recurrence rule produces no occurrences.", sampleTitle);
            return null;
        }

        var exDates = new List<DateTime>();
        foreach (var excluded in ev.ExceptionDates.GetAllDates())
        {
            if (ToKey(new FileDate(excluded.Value, excluded.TzId, excluded.IsUtc, excluded.HasTime), pattern) is { } key)
            {
                exDates.Add(key);
            }
            else
            {
                run.Skipped.Add(StrayExclusionReason, sampleTitle);
            }
        }

        return new ImportItem
        {
            Kind = ItemKind.Master,
            SampleTitle = sampleTitle,
            Uid = uid,
            Offset = offset,
            HasRule = true,
            Title = title,
            Description = description,
            Place = place,
            Start = times.Start,
            End = times.End,
            IsAllDay = isAllDay,
            ZoneId = times.Zone?.Id,
            Adjusted = times.Adjusted,
            Pattern = pattern,
            Occurrences = occurrences,
            ExDates = exDates,
        };
    }

    // A component's start and end as stored (issue #519 §8.4): an IANA or Windows TZID resolves, Z is zone "UTC", a
    // floating time takes defaultTimeZoneId or stays floating. An unknown TZID is the component's skip.
    private readonly record struct ImportTimes(
        DateTime Start, DateTime End, DateTime StartUtc, DateTime EndUtc, TimeZoneInfo? Zone, bool Adjusted);

    private static bool TryReadTimes(IcalEvent ev, bool isAllDay, TimeZoneInfo? defaultZone, out ImportTimes times, out string? reason)
    {
        times = default;
        reason = null;
        var start = ev.Start!;
        try
        {
            if (isAllDay)
            {
                var day = DateTime.SpecifyKind(start.Value.Date, DateTimeKind.Utc);
                var endDay = ev.End is { } end ? DateTime.SpecifyKind(end.Value.Date, DateTimeKind.Utc)
                    : day + ev.EffectiveDuration.ToTimeSpanUnspecified();
                times = new ImportTimes(day, endDay, day, endDay, null, false);
                return true;
            }

            if (!TryZoneOf(start, defaultZone, out var zone))
            {
                reason = UnknownTimeZoneReason;
                return false;
            }

            var wallStart = DateTime.SpecifyKind(start.Value, DateTimeKind.Unspecified);
            var adjusted = false;
            var startStored = zone is null ? wallStart : CalendarTimeZones.ToUtc(wallStart, zone, out adjusted);
            DateTime endStored;
            if (ev.End is { } endValue)
            {
                if (!TryZoneOf(endValue, defaultZone, out var endZone))
                {
                    reason = UnknownTimeZoneReason;
                    return false;
                }

                var endWall = DateTime.SpecifyKind(endValue.Value, DateTimeKind.Unspecified);
                var endInstant = endZone is null ? endWall : CalendarTimeZones.ToUtc(endWall, endZone);
                endStored = zone is null
                    ? (endZone is null ? endWall : endInstant)
                    : (endZone is null ? CalendarTimeZones.ToUtc(endWall, zone) : endInstant);
            }
            else
            {
                // A nominal duration: local end = local start + duration (S7).
                var endWall = wallStart + ev.EffectiveDuration.ToTimeSpanUnspecified();
                endStored = zone is null ? endWall : CalendarTimeZones.ToUtc(endWall, zone);
            }

            times = new ImportTimes(startStored, endStored, DateTime.SpecifyKind(startStored, DateTimeKind.Utc),
                DateTime.SpecifyKind(endStored, DateTimeKind.Utc), zone, adjusted);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A malformed duration is a per-event problem (skip with a reason), not a 500 for the whole file.
            reason = "Event has an unsupported or unresolvable date/time.";
            return false;
        }
    }

    // The zone a file time is in: null when floating without a default. False for an unknown TZID.
    private static bool TryZoneOf(CalDateTime value, TimeZoneInfo? defaultZone, out TimeZoneInfo? zone)
    {
        if (value.IsUtc)
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        if (value.TzId is { Length: > 0 } tzid)
        {
            zone = CalendarTimeZones.ResolveFileZone(tzid);
            return zone is not null;
        }

        zone = defaultZone;
        return true;
    }

    // A file date (EXDATE, RECURRENCE-ID) as a key of the series (issue #519 v3 §C, O10): converted to the master's zone
    // wall-clock, or by date for an all-day series. Null when its TZID is unknown.
    private static DateTime? ToKey(FileDate date, RecurrencePattern series)
    {
        if (series.IsAllDay)
        {
            return DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Unspecified);
        }

        var zone = CalendarTimeZones.Find(series.TimeZoneId);
        var value = DateTime.SpecifyKind(date.Value, DateTimeKind.Unspecified);
        DateTime? instant = null;
        if (date.IsUtc)
        {
            instant = DateTime.SpecifyKind(date.Value, DateTimeKind.Utc);
        }
        else if (date.TzId is { Length: > 0 } tzid)
        {
            if (CalendarTimeZones.ResolveFileZone(tzid) is not { } fileZone)
            {
                return null;
            }

            if (zone is not null && fileZone.Id == zone.Id)
            {
                return value;
            }

            instant = CalendarTimeZones.ToUtc(value, fileZone);
        }

        if (instant is not { } at)
        {
            return value; // floating: already a wall-clock
        }

        return zone is null ? DateTime.SpecifyKind(at, DateTimeKind.Unspecified) : CalendarTimeZones.ToWall(at, zone);
    }

    // ----- UID resolution (§8.2)

    // The batch's rows in the target calendar that any of its UIDs could name, read by one keyed query per table
    // (R1): by ExternalUid, by ImportSourceUid for a synthetic UID, and by primary key for a synthetic UID's guid.
    // Tracked under the locks, because the batch updates them. The whole calendar is never loaded.
    private sealed record BatchResolver(List<ContextCalendarEvent> Events, List<RecurrencePattern> Patterns);

    private async Task<BatchResolver> ResolveAsync(Guid calendarId, List<string> uids, bool tracked, CancellationToken cancellationToken)
    {
        if (uids.Count == 0)
        {
            return new BatchResolver([], []);
        }

        var synthetic = uids.Where(IsSyntheticUid).ToList();
        var guids = synthetic.Select(u => Guid.Parse(u[..^SyntheticUidSuffix.Length])).ToList();

        var events = tracked ? context.CalendarEvents : context.CalendarEvents.AsNoTracking();
        var patterns = tracked ? context.RecurrencePatterns : context.RecurrencePatterns.AsNoTracking();
        return new BatchResolver(
            await events
                .Where(e => e.CalendarId == calendarId
                    && ((e.ExternalUid != null && uids.Contains(e.ExternalUid))
                        || (e.ImportSourceUid != null && synthetic.Contains(e.ImportSourceUid))
                        || guids.Contains(e.CalendarEventId)))
                .OrderBy(e => e.CreatedAt).ThenBy(e => e.CalendarEventId)
                .ToListAsync(cancellationToken),
            await patterns
                .Where(p => p.CalendarId == calendarId
                    && ((p.ExternalUid != null && uids.Contains(p.ExternalUid))
                        || (p.ImportSourceUid != null && synthetic.Contains(p.ImportSourceUid))
                        || guids.Contains(p.RecurrencePatternId)))
                .OrderBy(p => p.CreatedAt).ThenBy(p => p.RecurrencePatternId)
                .ToListAsync(cancellationToken));
    }

    // The first rule that matches wins: a synthetic UID naming a row of the target calendar by its key; a stored
    // ExternalUid; a synthetic UID stored as ImportSourceUid. A row removed earlier in this batch matches nothing.
    private static T? Match<T>(
        string uid, IEnumerable<T> candidates, Func<T, Guid> id, Func<T, string?> externalUid, Func<T, string?> importSourceUid,
        OdysseyContext context)
        where T : class
    {
        var live = candidates.Where(c => context.Entry(c).State is not (EntityState.Deleted or EntityState.Detached)).ToList();
        if (IsSyntheticUid(uid) && Guid.TryParse(uid[..^SyntheticUidSuffix.Length], out var key)
            && live.FirstOrDefault(c => id(c) == key) is { } byKey)
        {
            return byKey;
        }

        if ((live.FirstOrDefault(c => string.Equals(externalUid(c), uid, StringComparison.Ordinal))
            ?? live.FirstOrDefault(c => externalUid(c) is { } stored && UidComparer.Equals(stored, uid))) is { } byExternal)
        {
            return byExternal;
        }

        return IsSyntheticUid(uid)
            ? live.FirstOrDefault(c => importSourceUid(c) is { } stored && UidComparer.Equals(stored, uid))
            : null;
    }

    // ----- Places (§8.3)

    // What the stored row's place becomes. KeepLink: an updated row keeps its link and a created one has none — the
    // file names no place, or the importer may not link one. Map runs only for a row that is about to be stored, so a
    // skipped block never creates a place.
    private static (Guid? LocationId, string? Detail, bool KeepLink) ResolvePlace(IcsPlaces places, ImportedPlace place)
    {
        if (!places.Enabled)
        {
            return (null, place.Detail, true);
        }

        if (!place.HasDetailProperty)
        {
            var (id, detail) = places.Map(place.PlaceText);
            return (id, detail, false);
        }

        if (place.PlaceText is null)
        {
            return (null, place.Detail, true);
        }

        var (locationId, mapped) = places.Map(place.PlaceText);
        return locationId is null ? (null, place.Detail ?? mapped, false) : (locationId, place.Detail, false);
    }
}
