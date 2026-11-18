using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Journal.Originals;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal;

/// <summary>
/// The read side of calendar links (issue #521 §3 component 3, §5.3, A8): a generated row's DTO carries its series'
/// links and a standalone event's its own. List reads fill the counts — four grouped queries for a page, two link
/// tables keyed by event and by series — and leave the lists null; detail reads fill both, one query per kind. Names,
/// types and sizes are filled here for every caller; the controller's <c>ApplyLinkVisibility</c> withholds them.
/// </summary>
internal static class CalendarLinkProjection
{
    public static async Task FillCountsAsync(
        OdysseyContext context, IReadOnlyCollection<ExistingCalendarEvent> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var eventIds = rows.Where(r => r.RecurrencePatternId is null).Select(r => r.CalendarEventId).Distinct().ToList();
        var patternIds = rows.Where(r => r.RecurrencePatternId is not null).Select(r => r.RecurrencePatternId!.Value).Distinct().ToList();
        var eventFiles = await CountAsync(context.CalendarEventAttachments, a => eventIds.Contains(a.CalendarEventId), a => a.CalendarEventId, eventIds.Count, cancellationToken);
        var eventPhotos = await CountAsync(context.CalendarEventPhotos, p => eventIds.Contains(p.CalendarEventId), p => p.CalendarEventId, eventIds.Count, cancellationToken);
        var seriesFiles = await CountAsync(context.RecurrencePatternAttachments, a => patternIds.Contains(a.RecurrencePatternId), a => a.RecurrencePatternId, patternIds.Count, cancellationToken);
        var seriesPhotos = await CountAsync(context.RecurrencePatternPhotos, p => patternIds.Contains(p.RecurrencePatternId), p => p.RecurrencePatternId, patternIds.Count, cancellationToken);

        foreach (var row in rows)
        {
            row.LinksFromSeries = row.RecurrencePatternId is not null;
            if (row.RecurrencePatternId is { } patternId)
            {
                row.AttachmentCount = seriesFiles.GetValueOrDefault(patternId);
                row.PhotoCount = seriesPhotos.GetValueOrDefault(patternId);
            }
            else
            {
                row.AttachmentCount = eventFiles.GetValueOrDefault(row.CalendarEventId);
                row.PhotoCount = eventPhotos.GetValueOrDefault(row.CalendarEventId);
            }
        }
    }

    public static async Task FillCountsAsync(
        OdysseyContext context, IReadOnlyCollection<ExistingRecurrencePattern> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.RecurrencePatternId).Distinct().ToList();
        var files = await CountAsync(context.RecurrencePatternAttachments, a => ids.Contains(a.RecurrencePatternId), a => a.RecurrencePatternId, ids.Count, cancellationToken);
        var photos = await CountAsync(context.RecurrencePatternPhotos, p => ids.Contains(p.RecurrencePatternId), p => p.RecurrencePatternId, ids.Count, cancellationToken);
        foreach (var row in rows)
        {
            row.AttachmentCount = files.GetValueOrDefault(row.RecurrencePatternId);
            row.PhotoCount = photos.GetValueOrDefault(row.RecurrencePatternId);
        }
    }

    public static async Task FillDetailAsync(OdysseyContext context, ExistingCalendarEvent dto, CancellationToken cancellationToken)
    {
        dto.LinksFromSeries = dto.RecurrencePatternId is not null;
        if (dto.RecurrencePatternId is { } patternId)
        {
            (dto.Attachments, dto.Photos) = await SeriesListsAsync(context, patternId, cancellationToken);
        }
        else
        {
            var eventId = dto.CalendarEventId;
            dto.Attachments = await FilesAsync(
                context, context.CalendarEventAttachments.Where(a => a.CalendarEventId == eventId)
                    .Select(a => new LinkRow { TargetId = a.FileId, CreatedAt = a.CreatedAt, Position = 0 }), cancellationToken);
            dto.Photos = await PhotosAsync(
                context, context.CalendarEventPhotos.Where(p => p.CalendarEventId == eventId)
                    .Select(p => new LinkRow { TargetId = p.PhotoId, CreatedAt = p.CreatedAt, Position = p.Position }), cancellationToken);
        }

        dto.AttachmentCount = dto.Attachments.Count;
        dto.PhotoCount = dto.Photos.Count;
    }

    public static async Task FillDetailAsync(OdysseyContext context, ExistingRecurrencePattern dto, CancellationToken cancellationToken)
    {
        (dto.Attachments, dto.Photos) = await SeriesListsAsync(context, dto.RecurrencePatternId, cancellationToken);
        dto.AttachmentCount = dto.Attachments.Count;
        dto.PhotoCount = dto.Photos.Count;
    }

    // A member-init shape, not a positional record: EF composes a Join over its members only in this form.
    private sealed class LinkRow
    {
        public Guid TargetId { get; init; }

        public DateTime CreatedAt { get; init; }

        public int Position { get; init; }
    }

    private static async Task<(List<CalendarLinkFileDto>, List<CalendarLinkPhotoDto>)> SeriesListsAsync(
        OdysseyContext context, Guid patternId, CancellationToken cancellationToken) =>
        (await FilesAsync(
                context, context.RecurrencePatternAttachments.Where(a => a.RecurrencePatternId == patternId)
                    .Select(a => new LinkRow { TargetId = a.FileId, CreatedAt = a.CreatedAt, Position = 0 }), cancellationToken),
            await PhotosAsync(
                context, context.RecurrencePatternPhotos.Where(p => p.RecurrencePatternId == patternId)
                    .Select(p => new LinkRow { TargetId = p.PhotoId, CreatedAt = p.CreatedAt, Position = p.Position }), cancellationToken));

    // One query: the links joined to their file's metadata, in link order.
    private static async Task<List<CalendarLinkFileDto>> FilesAsync(
        OdysseyContext context, IQueryable<LinkRow> links, CancellationToken cancellationToken)
    {
        var rows = await links.AsNoTracking()
            .Join(context.FileMetadata, l => l.TargetId, f => f.Id, (l, f) => new
            {
                l.TargetId, l.CreatedAt, f.FileName, f.ContentType, f.SizeBytes, f.UploadedAtUtc,
            })
            .ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.CreatedAt).ThenBy(r => r.TargetId)
            .Select(r => new CalendarLinkFileDto
            {
                FileId = r.TargetId, CreatedAt = r.CreatedAt, FileName = r.FileName, ContentType = r.ContentType,
                SizeBytes = r.SizeBytes, UploadedAtUtc = r.UploadedAtUtc,
            })
            .ToList();
    }

    // One query for the labels through the effective-name projection, one for thumbnail readiness.
    private static async Task<List<CalendarLinkPhotoDto>> PhotosAsync(
        OdysseyContext context, IQueryable<LinkRow> links, CancellationToken cancellationToken)
    {
        var rows = await links.AsNoTracking().ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.TargetId).Distinct().ToList();
        var titles = await context.Photos.AsNoTracking()
            .Where(p => ids.Contains(p.PhotoId))
            .Select(p => new { p.PhotoId, p.Title })
            .ToDictionaryAsync(p => p.PhotoId, p => p.Title, cancellationToken);
        var names = await PhotoOriginalSource.Describe(context, context.Photos.AsNoTracking().Where(p => ids.Contains(p.PhotoId)))
            .Select(p => new { p.PhotoId, p.FileName })
            .ToDictionaryAsync(p => p.PhotoId, p => p.FileName, cancellationToken);
        var ready = await PhotoThumbnailService.ReadyAsync(context, ids, cancellationToken);
        return rows.Where(r => titles.ContainsKey(r.TargetId))
            .OrderBy(r => r.Position).ThenBy(r => r.CreatedAt)
            .Select(r => new CalendarLinkPhotoDto
            {
                PhotoId = r.TargetId, Position = r.Position, CreatedAt = r.CreatedAt,
                Title = titles.GetValueOrDefault(r.TargetId), FileName = names.GetValueOrDefault(r.TargetId),
                ThumbnailsReady = ready.Contains(r.TargetId),
            })
            .ToList();
    }

    private static async Task<Dictionary<Guid, int>> CountAsync<T>(
        DbSet<T> set, System.Linq.Expressions.Expression<Func<T, bool>> filter,
        System.Linq.Expressions.Expression<Func<T, Guid>> key, int owners, CancellationToken cancellationToken)
        where T : class
    {
        if (owners == 0)
        {
            return [];
        }

        return await set.AsNoTracking().Where(filter).GroupBy(key)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
    }
}
