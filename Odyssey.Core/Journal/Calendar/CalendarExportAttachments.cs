using System.Text;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Journal.Interop;
using Odyssey.Core.Journal.Originals;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Core.Journal;

/// <summary>
/// What a calendar <c>.ics</c> export carries besides its events (issue #521 §8.2): every file and photo the planned
/// owners link, each distinct piece of content written inline ONCE — by the first <c>VEVENT</c> actually written that
/// links it — and referenced after that. A series' links are counted once, however many rows it flattens to.
/// </summary>
internal sealed class CalendarExportAttachments
{
    public const string PhotoUriScheme = "odyssey-photo";

    /// <summary>One distinct piece of content, with the inline head it is written under.</summary>
    internal sealed record Item(Guid Id, bool IsPhoto, string Head, long EncodedBytes, long SizeBytes, string Sha256Hash)
    {
        public string Key => (IsPhoto ? "p:" : "f:") + Id.ToString("N");

        /// <summary>The reference form (A5): the journal's URI schemes, with the hash and size as additive parameters.</summary>
        public string ReferenceLine =>
            CalendarIcsLines.Fold(
                $"ATTACH;VALUE=URI{(IsPhoto ? ";X-ODYSSEY-KIND=PHOTO" : string.Empty)};X-ODYSSEY-SHA256={Sha256Hash};SIZE={SizeBytes}:"
                + $"{(IsPhoto ? PhotoUriScheme : ImportLimits.FileUriScheme)}:{Id:D}");
    }

    private readonly Dictionary<Guid, List<Guid>> filesByEvent;
    private readonly Dictionary<Guid, List<Guid>> photosByEvent;
    private readonly Dictionary<Guid, List<Guid>> filesBySeries;
    private readonly Dictionary<Guid, List<Guid>> photosBySeries;
    private readonly Dictionary<Guid, Item> files;
    private readonly Dictionary<Guid, Item> photos;
    private readonly HashSet<string> written = [];

    private CalendarExportAttachments(
        Dictionary<Guid, List<Guid>> filesByEvent, Dictionary<Guid, List<Guid>> photosByEvent,
        Dictionary<Guid, List<Guid>> filesBySeries, Dictionary<Guid, List<Guid>> photosBySeries,
        Dictionary<Guid, Item> files, Dictionary<Guid, Item> photos)
    {
        this.filesByEvent = filesByEvent;
        this.photosByEvent = photosByEvent;
        this.filesBySeries = filesBySeries;
        this.photosBySeries = photosBySeries;
        this.files = files;
        this.photos = photos;
    }

    public static CalendarExportAttachments None { get; } = new([], [], [], [], [], []);

    /// <summary>Distinct files written inline.</summary>
    public int FileCount => files.Count;

    public int PhotoCount => photos.Count;

    /// <summary>Raw content bytes of the distinct files and photos (v3 §E).</summary>
    public long ContentBytes => files.Values.Sum(f => f.SizeBytes) + photos.Values.Sum(p => p.SizeBytes);

    /// <summary>The encoded bytes the inline content will take: what the byte cap is judged against before any is sent.</summary>
    public long EncodedBytes => files.Values.Sum(f => f.EncodedBytes) + photos.Values.Sum(p => p.EncodedBytes);

    /// <summary>
    /// Plans the links of <paramref name="eventIds"/> (standalone rows: their own links, or their series' for a generated
    /// row) and <paramref name="patternIds"/> (collapsed masters). Refuses with
    /// <c>403 calendar_ics_export_attachments_forbidden</c> when anything is linked of a kind the caller may not read.
    /// </summary>
    public static async Task<CalendarExportAttachments> PlanAsync(
        OdysseyContext context, IReadOnlyCollection<Guid> eventIds, IReadOnlyCollection<Guid> patternIds,
        bool canReadFiles, bool canReadPhotos, bool canWriteFiles, bool canWritePhotos, CancellationToken cancellationToken)
    {
        // A generated row written standalone carries its series' links (SF7, A8): split the rows by owner.
        var ownEvents = new List<Guid>();
        var seriesIds = patternIds.ToHashSet();
        var seriesOfEvent = new Dictionary<Guid, Guid>();
        foreach (var chunk in eventIds.Chunk(ExportChunking.ChunkSize))
        {
            var rows = await context.CalendarEvents.AsNoTracking()
                .Where(e => chunk.Contains(e.CalendarEventId))
                .Select(e => new { e.CalendarEventId, e.RecurrencePatternId })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                if (row.RecurrencePatternId is { } patternId)
                {
                    seriesOfEvent[row.CalendarEventId] = patternId;
                    seriesIds.Add(patternId);
                }
                else
                {
                    ownEvents.Add(row.CalendarEventId);
                }
            }
        }

        var filesByEvent = new Dictionary<Guid, List<Guid>>();
        var photosByEvent = new Dictionary<Guid, List<Guid>>();
        foreach (var chunk in ownEvents.Chunk(ExportChunking.ChunkSize))
        {
            foreach (var g in (await context.CalendarEventAttachments.AsNoTracking()
                         .Where(a => chunk.Contains(a.CalendarEventId))
                         .Select(a => new { a.CalendarEventId, a.FileId, a.CreatedAt })
                         .ToListAsync(cancellationToken)).GroupBy(a => a.CalendarEventId))
            {
                filesByEvent[g.Key] = g.OrderBy(a => a.CreatedAt).ThenBy(a => a.FileId).Select(a => a.FileId).ToList();
            }

            foreach (var g in (await context.CalendarEventPhotos.AsNoTracking()
                         .Where(p => chunk.Contains(p.CalendarEventId))
                         .Select(p => new { p.CalendarEventId, p.PhotoId, p.Position })
                         .ToListAsync(cancellationToken)).GroupBy(p => p.CalendarEventId))
            {
                photosByEvent[g.Key] = g.OrderBy(p => p.Position).Select(p => p.PhotoId).ToList();
            }
        }

        var filesBySeries = new Dictionary<Guid, List<Guid>>();
        var photosBySeries = new Dictionary<Guid, List<Guid>>();
        foreach (var chunk in seriesIds.Chunk(ExportChunking.ChunkSize))
        {
            foreach (var g in (await context.RecurrencePatternAttachments.AsNoTracking()
                         .Where(a => chunk.Contains(a.RecurrencePatternId))
                         .Select(a => new { a.RecurrencePatternId, a.FileId, a.CreatedAt })
                         .ToListAsync(cancellationToken)).GroupBy(a => a.RecurrencePatternId))
            {
                filesBySeries[g.Key] = g.OrderBy(a => a.CreatedAt).ThenBy(a => a.FileId).Select(a => a.FileId).ToList();
            }

            foreach (var g in (await context.RecurrencePatternPhotos.AsNoTracking()
                         .Where(p => chunk.Contains(p.RecurrencePatternId))
                         .Select(p => new { p.RecurrencePatternId, p.PhotoId, p.Position })
                         .ToListAsync(cancellationToken)).GroupBy(p => p.RecurrencePatternId))
            {
                photosBySeries[g.Key] = g.OrderBy(p => p.Position).Select(p => p.PhotoId).ToList();
            }
        }

        // Rows of a series are owned by the series: their entries point at the series' lists.
        foreach (var (eventId, patternId) in seriesOfEvent)
        {
            if (filesBySeries.TryGetValue(patternId, out var f))
            {
                filesByEvent[eventId] = f;
            }

            if (photosBySeries.TryGetValue(patternId, out var p))
            {
                photosByEvent[eventId] = p;
            }
        }

        var linkedFiles = filesByEvent.Values.Concat(filesBySeries.Values).SelectMany(l => l).Distinct().ToList();
        var linkedPhotos = photosByEvent.Values.Concat(photosBySeries.Values).SelectMany(l => l).Distinct().ToList();
        var missing = new List<string>();
        if (linkedFiles.Count > 0 && !canReadFiles)
        {
            missing.Add(PermissionClaims.FilesRead);
        }

        if (linkedPhotos.Count > 0 && !canReadPhotos)
        {
            missing.Add(PermissionClaims.PhotosRead);
        }

        if (missing.Count > 0)
        {
            throw CalendarAttachmentsException.ExportForbidden(missing);
        }

        var files = new Dictionary<Guid, Item>();
        if (canWriteFiles)
        {
            foreach (var chunk in linkedFiles.Chunk(ExportChunking.ChunkSize))
            {
                var rows = await context.FileMetadata.AsNoTracking()
                    .Where(FileReferenceCounter.VisibleInFilesStore(context))
                    .Where(f => chunk.Contains(f.Id))
                    .Select(f => new { f.Id, f.FileName, f.ContentType, f.Sha256Hash, f.SizeBytes })
                    .ToListAsync(cancellationToken);
                foreach (var row in rows)
                {
                    files[row.Id] = Planned(new IcsBinaryAttachmentHeader(row.Id, row.FileName, row.ContentType, row.Sha256Hash, row.SizeBytes));
                }
            }
        }

        var photos = new Dictionary<Guid, Item>();
        if (canWritePhotos)
        {
            foreach (var chunk in linkedPhotos.Chunk(ExportChunking.ChunkSize))
            {
                var described = await PhotoOriginalSource
                    .Describe(context, context.Photos.AsNoTracking().Where(p => chunk.Contains(p.PhotoId)))
                    .ToListAsync(cancellationToken);
                var labels = await context.Photos.AsNoTracking()
                    .Where(p => chunk.Contains(p.PhotoId))
                    .Select(p => new { p.PhotoId, p.Title, p.Caption })
                    .ToDictionaryAsync(p => p.PhotoId, cancellationToken);
                foreach (var row in described.Where(d => d.SizeBytes is not null && d.Sha256Hash is not null && d.ContentType is not null))
                {
                    var label = labels.GetValueOrDefault(row.PhotoId);
                    photos[row.PhotoId] = Planned(new IcsBinaryAttachmentHeader(
                        row.PhotoId, row.FileName, row.ContentType, row.Sha256Hash, row.SizeBytes!.Value,
                        IsPhoto: true, Title: label?.Title, Caption: label?.Caption));
                }
            }
        }

        return new CalendarExportAttachments(filesByEvent, photosByEvent, filesBySeries, photosBySeries, files, photos);
    }

    private static Item Planned(IcsBinaryAttachmentHeader header)
    {
        var head = IcsBinaryAttachmentWriter.BuildHead(header);
        return new Item(header.Id!.Value, header.IsPhoto, head, IcsBinaryAttachmentWriter.EncodedLength(head, header.Size),
            header.Size, header.Sha256Hash ?? string.Empty);
    }

    /// <summary>One VEVENT's attachments, in write order: files by link date, then photos by position.</summary>
    public sealed record Owner(IReadOnlyList<Guid> FileIds, IReadOnlyList<Guid> PhotoIds)
    {
        public bool IsEmpty => FileIds.Count == 0 && PhotoIds.Count == 0;
    }

    public Owner ForEvent(Guid eventId) =>
        new(filesByEvent.GetValueOrDefault(eventId) ?? [], photosByEvent.GetValueOrDefault(eventId) ?? []);

    public Owner ForSeries(Guid patternId) =>
        new(filesBySeries.GetValueOrDefault(patternId) ?? [], photosBySeries.GetValueOrDefault(patternId) ?? []);

    /// <summary>The bytes an owner's lines would add if written now: inline for content not written yet, else a reference.</summary>
    public long BytesFor(Owner owner)
    {
        long total = 0;
        var seen = new HashSet<string>(written);
        foreach (var item in Items(owner))
        {
            total += item switch
            {
                Item known when seen.Add(known.Key) => known.EncodedBytes,
                Item known => Encoding.UTF8.GetByteCount(known.ReferenceLine),
                _ => 0,
            };
        }

        return total + BareLines(owner).Sum(l => (long)Encoding.UTF8.GetByteCount(l));
    }

    private IEnumerable<Item> Items(Owner owner) =>
        owner.FileIds.Select(id => files.GetValueOrDefault(id)).OfType<Item>()
            .Concat(owner.PhotoIds.Select(id => photos.GetValueOrDefault(id)).OfType<Item>());

    // Links whose content cannot travel (not visible, or no store): the bare reference, as the journal writes it.
    private IEnumerable<string> BareLines(Owner owner) =>
        owner.FileIds.Where(id => !files.ContainsKey(id)).Select(id => $"ATTACH:{ImportLimits.FileUriScheme}:{id:D}\r\n")
            .Concat(owner.PhotoIds.Where(id => !photos.ContainsKey(id)).Select(id => $"ATTACH:{PhotoUriScheme}:{id:D}\r\n"));

    /// <summary>
    /// Writes an owner's lines (decided at write time, A8): the first time a piece of content is written it goes inline,
    /// streamed from its store; after that it is a reference line.
    /// </summary>
    public async Task WriteAsync(
        Stream output, Owner owner, StoredFileContentSource? fileContent, PhotoOriginalSource? photoContent,
        OdysseyContext context, CancellationToken cancellationToken)
    {
        foreach (var item in Items(owner))
        {
            if (!written.Add(item.Key))
            {
                await IcsChunkSerializer.WriteAsync(output, item.ReferenceLine, cancellationToken);
                continue;
            }

            Stream? content = null;
            if (item.IsPhoto)
            {
                content = photoContent is null ? null : (await photoContent.OpenAsync(item.Id, cancellationToken))?.Content;
            }
            else if (fileContent is not null)
            {
                content = (await fileContent.OpenAsync(
                    context.FileMetadata.Where(FileReferenceCounter.VisibleInFilesStore(context)).Where(f => f.Id == item.Id),
                    cancellationToken))?.Content;
            }

            if (content is null)
            {
                // Removed since the plan was made: keep the reference rather than drop the link silently.
                written.Remove(item.Key);
                await IcsChunkSerializer.WriteAsync(
                    output, $"ATTACH:{(item.IsPhoto ? PhotoUriScheme : ImportLimits.FileUriScheme)}:{item.Id:D}\r\n", cancellationToken);
                continue;
            }

            await using (content)
            {
                await IcsBinaryAttachmentWriter.WriteAsync(output, item.Head, content, cancellationToken);
            }
        }

        foreach (var line in BareLines(owner))
        {
            await IcsChunkSerializer.WriteAsync(output, line, cancellationToken);
        }
    }
}

/// <summary>Content-line helpers for the lines the calendar writes itself.</summary>
internal static class CalendarIcsLines
{
    /// <summary>Folds an ASCII content line at 75 octets (RFC 5545 §3.1) and ends it with CRLF.</summary>
    public static string Fold(string line)
    {
        var builder = new StringBuilder(line.Length + (line.Length / 74 * 3) + 2);
        var first = Math.Min(IcsBinaryAttachmentWriter.MaxLineOctets, line.Length);
        builder.Append(line, 0, first);
        for (var i = first; i < line.Length; i += IcsBinaryAttachmentWriter.MaxLineOctets - 1)
        {
            builder.Append("\r\n ").Append(line, i, Math.Min(IcsBinaryAttachmentWriter.MaxLineOctets - 1, line.Length - i));
        }

        return builder.Append("\r\n").ToString();
    }
}
