using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal.Originals;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;

namespace Odyssey.Core.Journal;

/// <summary>What the caller may link (issue #521 §3): the read claims an ADDED file or photo needs.</summary>
public sealed record CalendarLinkClaims(bool CanReadFiles, bool CanReadPhotos)
{
    /// <summary>No claim at all: a write may keep or remove links, never add one.</summary>
    public static CalendarLinkClaims None { get; } = new(false, false);

    /// <summary>A trusted, non-HTTP caller.</summary>
    public static CalendarLinkClaims Trusted { get; } = new(true, true);
}

/// <summary>
/// The ONLY writer of the four calendar link tables (issue #521 §3 component 2, SF5): <c>CalendarEventAttachments</c>,
/// <c>CalendarEventPhotos</c>, <c>RecurrencePatternAttachments</c> and <c>RecurrencePatternPhotos</c>. A source-lint
/// fails on an <c>Add</c>/<c>Remove</c> of any of them anywhere else, because invariant 1 — event links exist only on a
/// standalone event — has no database check behind it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes keep what they do not name</b> (A1, SF4): a null array leaves that kind alone and <c>[]</c> clears it. The
/// claims are <b>change-only</b>: <c>files.read</c> is needed to add a file the owner does not already hold and
/// <c>photos.read</c> to add a photo; keeping or removing needs neither. An added id must be visible — a file under
/// <see cref="FileReferenceCounter.VisibleInFilesStore"/>, a photo that exists — and the cap applies only when the count
/// rises, so a lowered setting never blocks an unrelated edit of an over-cap owner.
/// </para>
/// <para>
/// <b>Every detach copies the series' links</b> (A3, v4 §4), keeping each link's <c>CreatedAt</c> and
/// <c>Position</c>. Deleting a series removes its own link rows by tracked <c>RemoveRange</c>, because the EF InMemory
/// provider runs no cascade.
/// </para>
/// </remarks>
internal sealed class CalendarLinkService(OdysseyContext context)
{
    /// <summary>The field keys a link refusal is reported on, the request arrays' JSON names.</summary>
    public const string FilesField = "attachmentFileIds";

    public const string PhotosField = "photoIds";

    /// <summary>Copied link rows saved per <c>SaveChangesAsync</c> in a series delete (§4 invariant 2).</summary>
    public const int CopyChunkSize = 1000;

    public const string OccurrenceLinksMessage = "Attachments belong to the series; edit the series to change them.";

    /// <summary>The unique indexes a concurrent identical add would hit (v3 §C).</summary>
    public static readonly string[] LinkIndexes =
    [
        "IX_CalendarEventAttachments_CalendarEventId_FileId",
        "IX_CalendarEventPhotos_CalendarEventId_PhotoId",
        "IX_RecurrencePatternAttachments_RecurrencePatternId_FileId",
        "IX_RecurrencePatternPhotos_RecurrencePatternId_PhotoId",
    ];

    /// <summary>The link tables' keys to a file and to a photo, as the migration names them.</summary>
    public static readonly string[] FileKeys =
        ["FK_CalendarEventAttachments_FileMetadata_FileId", "FK_RecurrencePatternAttachments_FileMetadata_FileId"];

    public static readonly string[] PhotoKeys = ["FK_CalendarEventPhotos_Photos_PhotoId", "FK_RecurrencePatternPhotos_Photos_PhotoId"];

    /// <summary>
    /// Runs a link write, re-running it once when a concurrent identical add won the unique index (v3 §C): the retry
    /// reloads the links and finds the row already there. A second race is <c>409 calendar-links-busy</c>.
    /// </summary>
    public static async Task<T> WithOneLinkRetryAsync<T>(Func<Task<T>> write)
    {
        try
        {
            return await write();
        }
        catch (CalendarAttachmentsException race) when (race.IsLinkRace)
        {
            try
            {
                return await write();
            }
            catch (CalendarAttachmentsException again) when (again.IsLinkRace)
            {
                throw CalendarAttachmentsException.LinksBusy();
            }
        }
    }

    /// <summary>
    /// Refuses link arrays on a generated occurrence (§3 step 5): a non-null, non-empty array is a <c>422</c> keyed on
    /// it; null and <c>[]</c> are accepted and ignored.
    /// </summary>
    public static void EnsureNoOccurrenceLinks(Guid[]? fileIds, Guid[]? photoIds)
    {
        if (fileIds is { Length: > 0 })
        {
            throw new DomainUnprocessableException(OccurrenceLinksMessage, FilesField);
        }

        if (photoIds is { Length: > 0 })
        {
            throw new DomainUnprocessableException(OccurrenceLinksMessage, PhotosField);
        }
    }

    /// <summary>Applies a write's arrays to a standalone event (§3 steps 2–4), staged on the context unsaved.</summary>
    public async Task ApplyToEventAsync(
        Guid eventId, bool isNew, Guid[]? fileIds, Guid[]? photoIds, CalendarLinkClaims claims, int cap, DateTime now,
        CancellationToken cancellationToken)
    {
        if (fileIds is null && photoIds is null)
        {
            return;
        }

        var files = fileIds is null || isNew
            ? []
            : await Current(context.CalendarEventAttachments, a => a.CalendarEventId == eventId, cancellationToken);
        var photos = photoIds is null || isNew
            ? []
            : await Current(context.CalendarEventPhotos, p => p.CalendarEventId == eventId, cancellationToken);
        var plan = await PlanAsync(
            fileIds, files.Select(a => a.FileId).ToList(), photoIds, photos.Select(p => p.PhotoId).ToList(), claims, cap,
            cancellationToken);

        if (plan.Files is { } desiredFiles)
        {
            DiffFiles(context.CalendarEventAttachments, files, a => a.FileId, desiredFiles,
                fileId => new CalendarEventAttachment { CalendarEventId = eventId, FileId = fileId, CreatedAt = now });
        }

        if (plan.Photos is { } desiredPhotos)
        {
            DiffPhotos(context.CalendarEventPhotos, photos, p => p.PhotoId, (p, i) => p.Position = i, desiredPhotos,
                (photoId, i) => new CalendarEventPhoto { CalendarEventId = eventId, PhotoId = photoId, Position = i, CreatedAt = now });
        }
    }

    /// <summary>Applies a write's arrays to a series, under the same rules as <see cref="ApplyToEventAsync"/>.</summary>
    public async Task ApplyToSeriesAsync(
        Guid patternId, bool isNew, Guid[]? fileIds, Guid[]? photoIds, CalendarLinkClaims claims, int cap, DateTime now,
        CancellationToken cancellationToken)
    {
        if (fileIds is null && photoIds is null)
        {
            return;
        }

        var files = fileIds is null || isNew
            ? []
            : await Current(context.RecurrencePatternAttachments, a => a.RecurrencePatternId == patternId, cancellationToken);
        var photos = photoIds is null || isNew
            ? []
            : await Current(context.RecurrencePatternPhotos, p => p.RecurrencePatternId == patternId, cancellationToken);
        var plan = await PlanAsync(
            fileIds, files.Select(a => a.FileId).ToList(), photoIds, photos.Select(p => p.PhotoId).ToList(), claims, cap,
            cancellationToken);

        if (plan.Files is { } desiredFiles)
        {
            DiffFiles(context.RecurrencePatternAttachments, files, a => a.FileId, desiredFiles,
                fileId => new RecurrencePatternAttachment { RecurrencePatternId = patternId, FileId = fileId, CreatedAt = now });
        }

        if (plan.Photos is { } desiredPhotos)
        {
            DiffPhotos(context.RecurrencePatternPhotos, photos, p => p.PhotoId, (p, i) => p.Position = i, desiredPhotos,
                (photoId, i) => new RecurrencePatternPhoto
                {
                    RecurrencePatternId = patternId, PhotoId = photoId, Position = i, CreatedAt = now,
                });
        }
    }

    private sealed record LinkPlan(List<Guid>? Files, List<Guid>? Photos);

    // Every check before any write: both kinds' claims (403), then visibility and the cap (422).
    private async Task<LinkPlan> PlanAsync(
        Guid[]? fileIds, List<Guid> currentFiles, Guid[]? photoIds, List<Guid> currentPhotos, CalendarLinkClaims claims,
        int cap, CancellationToken cancellationToken)
    {
        var files = fileIds?.Distinct().ToList();
        var photos = photoIds?.Distinct().ToList();
        var addedFiles = files?.Except(currentFiles).ToList() ?? [];
        var addedPhotos = photos?.Except(currentPhotos).ToList() ?? [];

        if (addedFiles.Count > 0 && !claims.CanReadFiles)
        {
            throw new DomainForbiddenException("Attaching a file requires the files.read permission.");
        }

        if (addedPhotos.Count > 0 && !claims.CanReadPhotos)
        {
            throw new DomainForbiddenException("Linking a photo requires the photos.read permission.");
        }

        if (addedFiles.Count > 0)
        {
            var visible = await context.FileMetadata.AsNoTracking()
                .Where(FileReferenceCounter.VisibleInFilesStore(context))
                .Where(f => addedFiles.Contains(f.Id))
                .Select(f => f.Id)
                .ToListAsync(cancellationToken);
            var missing = addedFiles.Except(visible).ToList();
            if (missing.Count > 0)
            {
                throw new DomainUnprocessableException($"Unknown attachment file(s): {string.Join(", ", missing)}.", FilesField);
            }
        }

        if (addedPhotos.Count > 0)
        {
            // Through the effective-original projection (§3): a photo whose original resolves. Only its members are
            // selected — EF composes no filter over the positional record — and the rule is applied here.
            var visible = (await PhotoOriginalSource.Describe(context, context.Photos.AsNoTracking().Where(p => addedPhotos.Contains(p.PhotoId)))
                    .Select(p => new { p.PhotoId, p.ContentType })
                    .ToListAsync(cancellationToken))
                .Where(p => p.ContentType is not null)
                .Select(p => p.PhotoId)
                .ToList();
            var missing = addedPhotos.Except(visible).ToList();
            if (missing.Count > 0)
            {
                throw new DomainUnprocessableException($"Unknown photo(s): {string.Join(", ", missing)}.", PhotosField);
            }
        }

        EnsureCap(files, currentFiles.Count, cap, "file", FilesField);
        EnsureCap(photos, currentPhotos.Count, cap, "photo", PhotosField);
        return new LinkPlan(files, photos);
    }

    // Only a rise above the cap is refused: an owner already over a lowered cap may keep or shrink its links.
    private static void EnsureCap(List<Guid>? desired, int current, int cap, string noun, string field)
    {
        if (desired is not null && desired.Count > cap && desired.Count > current)
        {
            throw new DomainUnprocessableException($"An event or series cannot link more than {cap} {noun}s.", field);
        }
    }

    // The stored rows plus any staged earlier in this unit of work (a detach copy written by the same request).
    private static async Task<List<T>> Current<T>(
        DbSet<T> set, System.Linq.Expressions.Expression<Func<T, bool>> owner, CancellationToken cancellationToken)
        where T : class
    {
        var stored = await set.Where(owner).ToListAsync(cancellationToken);
        var compiled = owner.Compile();
        foreach (var local in set.Local.Where(compiled))
        {
            if (!stored.Contains(local))
            {
                stored.Add(local);
            }
        }

        return stored;
    }

    private static void DiffFiles<T>(DbSet<T> set, List<T> current, Func<T, Guid> fileOf, List<Guid> desired, Func<Guid, T> create)
        where T : class
    {
        var wanted = desired.ToHashSet();
        foreach (var row in current.Where(r => !wanted.Contains(fileOf(r))).ToList())
        {
            set.Remove(row);
        }

        var kept = current.Select(fileOf).ToHashSet();
        foreach (var fileId in desired.Where(id => !kept.Contains(id)))
        {
            set.Add(create(fileId));
        }
    }

    private static void DiffPhotos<T>(
        DbSet<T> set, List<T> current, Func<T, Guid> photoOf, Action<T, int> position, List<Guid> desired, Func<Guid, int, T> create)
        where T : class
    {
        var wanted = desired.ToHashSet();
        foreach (var row in current.Where(r => !wanted.Contains(photoOf(r))).ToList())
        {
            set.Remove(row);
        }

        var existing = current.Where(r => wanted.Contains(photoOf(r))).ToDictionary(photoOf);
        for (var i = 0; i < desired.Count; i++)
        {
            if (existing.TryGetValue(desired[i], out var row))
            {
                position(row, i);
            }
            else
            {
                set.Add(create(desired[i], i));
            }
        }
    }

    // ------------------------------------------------------------------ Import replace

    /// <summary>
    /// What one import component replaces (§8.3 replace rule): a kind is replaced only when at least one of its
    /// <c>ATTACH</c> resolved or was stored; null leaves that kind untouched. The ids are distinct and capped already.
    /// </summary>
    public sealed record ImportReplace(IReadOnlyList<Guid>? Files, IReadOnlyList<Guid>? Photos);

    /// <summary>The counts one replacement adds to the import result.</summary>
    public sealed record ReplaceOutcome(bool Changed, int Removed);

    /// <summary>
    /// Replaces an owner's link sets from an import (no claim check: the import's pass 1 already demanded them), on rows
    /// the caller loaded for the batch. Staged unsaved.
    /// </summary>
    public static ReplaceOutcome ReplaceEventLinks(
        OdysseyContext context, Guid eventId, List<CalendarEventAttachment> files, List<CalendarEventPhoto> photos,
        ImportReplace replace, DateTime now)
    {
        var changed = false;
        var removed = 0;
        if (replace.Files is { } desiredFiles)
        {
            (var c, var r) = Replace(context.CalendarEventAttachments, files, a => a.FileId, null, desiredFiles,
                (id, _) => new CalendarEventAttachment { CalendarEventId = eventId, FileId = id, CreatedAt = now });
            changed |= c;
            removed += r;
        }

        if (replace.Photos is { } desiredPhotos)
        {
            (var c, var r) = Replace(context.CalendarEventPhotos, photos, p => p.PhotoId, (p, i) => p.Position = i, desiredPhotos,
                (id, i) => new CalendarEventPhoto { CalendarEventId = eventId, PhotoId = id, Position = i, CreatedAt = now });
            changed |= c;
            removed += r;
        }

        return new ReplaceOutcome(changed, removed);
    }

    /// <inheritdoc cref="ReplaceEventLinks"/>
    public static ReplaceOutcome ReplaceSeriesLinks(
        OdysseyContext context, Guid patternId, List<RecurrencePatternAttachment> files, List<RecurrencePatternPhoto> photos,
        ImportReplace replace, DateTime now)
    {
        var changed = false;
        var removed = 0;
        if (replace.Files is { } desiredFiles)
        {
            (var c, var r) = Replace(context.RecurrencePatternAttachments, files, a => a.FileId, null, desiredFiles,
                (id, _) => new RecurrencePatternAttachment { RecurrencePatternId = patternId, FileId = id, CreatedAt = now });
            changed |= c;
            removed += r;
        }

        if (replace.Photos is { } desiredPhotos)
        {
            (var c, var r) = Replace(context.RecurrencePatternPhotos, photos, p => p.PhotoId, (p, i) => p.Position = i, desiredPhotos,
                (id, i) => new RecurrencePatternPhoto { RecurrencePatternId = patternId, PhotoId = id, Position = i, CreatedAt = now });
            changed |= c;
            removed += r;
        }

        return new ReplaceOutcome(changed, removed);
    }

    private static (bool Changed, int Removed) Replace<T>(
        DbSet<T> set, List<T> current, Func<T, Guid> idOf, Action<T, int>? position, IReadOnlyList<Guid> desired,
        Func<Guid, int, T> create)
        where T : class
    {
        var wanted = desired.ToHashSet();
        var removed = current.Where(r => !wanted.Contains(idOf(r))).ToList();
        foreach (var row in removed)
        {
            set.Remove(row);
            current.Remove(row);
        }

        var changed = removed.Count > 0;
        var existing = current.ToDictionary(idOf);
        for (var i = 0; i < desired.Count; i++)
        {
            if (existing.TryGetValue(desired[i], out var row))
            {
                if (position is not null)
                {
                    var entry = set.Entry(row);
                    position(row, i);
                    changed |= entry.State == EntityState.Modified;
                }
            }
            else
            {
                var added = create(desired[i], i);
                set.Add(added);
                current.Add(added);
                changed = true;
            }
        }

        return (changed, removed.Count);
    }

    // ------------------------------------------------------------------ Series membership

    /// <summary>
    /// Stages a copy of the series' links onto rows that just left it (A3, v4 §4): series delete, a cancelled series in
    /// an import, and every inline or backfill keying that detaches a row. Each copy keeps the source's
    /// <c>CreatedAt</c> and <c>Position</c>. Unsaved, so it rides the caller's save.
    /// </summary>
    public static async Task StageCopyToDetachedAsync(
        OdysseyContext context, Guid patternId, IReadOnlyCollection<ContextCalendarEvent> detached, CancellationToken cancellationToken)
    {
        if (detached.Count == 0)
        {
            return;
        }

        var snapshot = await SeriesLinksAsync(context, patternId, cancellationToken);
        foreach (var row in detached)
        {
            foreach (var link in snapshot.Files)
            {
                context.CalendarEventAttachments.Add(new CalendarEventAttachment
                {
                    CalendarEventId = row.CalendarEventId, FileId = link.FileId, CreatedAt = link.CreatedAt,
                });
            }

            foreach (var link in snapshot.Photos)
            {
                context.CalendarEventPhotos.Add(new CalendarEventPhoto
                {
                    CalendarEventId = row.CalendarEventId, PhotoId = link.PhotoId, Position = link.Position, CreatedAt = link.CreatedAt,
                });
            }
        }
    }

    /// <summary>A series' links as they stand, untracked: what a detach copies.</summary>
    public sealed record SeriesLinkSnapshot(List<RecurrencePatternAttachment> Files, List<RecurrencePatternPhoto> Photos)
    {
        public int Count => Files.Count + Photos.Count;
    }

    public static async Task<SeriesLinkSnapshot> SeriesLinksAsync(OdysseyContext context, Guid patternId, CancellationToken cancellationToken) =>
        new(
            await context.RecurrencePatternAttachments.AsNoTracking()
                .Where(a => a.RecurrencePatternId == patternId).ToListAsync(cancellationToken),
            await context.RecurrencePatternPhotos.AsNoTracking()
                .Where(p => p.RecurrencePatternId == patternId).ToListAsync(cancellationToken));

    /// <summary>
    /// The series-delete copy (§4 invariant 2, v4 §6): tracked inserts saved in chunks of <see cref="CopyChunkSize"/>,
    /// the tracker cleared after each, inside the caller's transaction. Bounded by
    /// <c>RecurrenceMaxGeneratedOccurrences × 2 × CalendarEventMaxLinksPerKindMax</c> = 200,000 rows.
    /// </summary>
    public static async Task CopyToDetachedChunkedAsync(
        OdysseyContext context, SeriesLinkSnapshot snapshot, IReadOnlyList<Guid> detachedIds, CancellationToken cancellationToken)
    {
        if (snapshot.Count == 0 || detachedIds.Count == 0)
        {
            return;
        }

        var pending = 0;
        foreach (var eventId in detachedIds)
        {
            foreach (var link in snapshot.Files)
            {
                context.CalendarEventAttachments.Add(new CalendarEventAttachment
                {
                    CalendarEventId = eventId, FileId = link.FileId, CreatedAt = link.CreatedAt,
                });
                pending++;
            }

            foreach (var link in snapshot.Photos)
            {
                context.CalendarEventPhotos.Add(new CalendarEventPhoto
                {
                    CalendarEventId = eventId, PhotoId = link.PhotoId, Position = link.Position, CreatedAt = link.CreatedAt,
                });
                pending++;
            }

            if (pending >= CopyChunkSize)
            {
                await context.SaveChangesAsync(cancellationToken);
                context.ChangeTracker.Clear();
                pending = 0;
            }
        }

        if (pending > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
        }
    }

    /// <summary>Stages the removal of a series' own link rows (tracked, so the InMemory tiers match the cascade).</summary>
    public static async Task StageRemoveForSeriesAsync(OdysseyContext context, Guid patternId, CancellationToken cancellationToken)
    {
        context.RecurrencePatternAttachments.RemoveRange(
            await context.RecurrencePatternAttachments.Where(a => a.RecurrencePatternId == patternId).ToListAsync(cancellationToken));
        context.RecurrencePatternPhotos.RemoveRange(
            await context.RecurrencePatternPhotos.Where(p => p.RecurrencePatternId == patternId).ToListAsync(cancellationToken));
    }

    /// <summary>Stages the removal of a standalone event's link rows.</summary>
    public static async Task StageRemoveForEventAsync(OdysseyContext context, Guid eventId, CancellationToken cancellationToken)
    {
        context.CalendarEventAttachments.RemoveRange(
            await context.CalendarEventAttachments.Where(a => a.CalendarEventId == eventId).ToListAsync(cancellationToken));
        context.CalendarEventPhotos.RemoveRange(
            await context.CalendarEventPhotos.Where(p => p.CalendarEventId == eventId).ToListAsync(cancellationToken));
    }

    // ------------------------------------------------------------------ Database errors

    /// <summary>
    /// A link write's database refusals (v3 §C): a concurrent identical add (<c>1062</c> on a link index) is
    /// <c>409 calendar-links-busy</c>; a file or photo deleted between the check and the save (<c>1452</c>) is a
    /// <c>422</c> keyed on its array. Null for anything else.
    /// </summary>
    public static DomainException? Translate(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not MySqlException mysql)
            {
                continue;
            }

            if (mysql.Number == 1062 && LinkIndexes.Any(index => mysql.Message.Contains($"{index}'", StringComparison.Ordinal)))
            {
                return CalendarAttachmentsException.LinkRace();
            }

            // By constraint name, never by a word in the message (PR #529 review).
            if (mysql.Number == 1452 && FileKeys.Any(key => mysql.Message.Contains($"`{key}`", StringComparison.Ordinal)))
            {
                return new DomainUnprocessableException("An attached file was removed while the event was being saved.", FilesField);
            }

            if (mysql.Number == 1452 && PhotoKeys.Any(key => mysql.Message.Contains($"`{key}`", StringComparison.Ordinal)))
            {
                return new DomainUnprocessableException("A linked photo was removed while the event was being saved.", PhotosField);
            }
        }

        return null;
    }
}
