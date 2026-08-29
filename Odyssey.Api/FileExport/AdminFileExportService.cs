using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;

namespace Odyssey.Api.FileExport;

/// <summary>
/// Builds the admin "export all files" / "export filtered files" ZIPs (issue #159, extended per the
/// Files.jsx design update for scoped export). Produces a point-in-time best-effort snapshot: the
/// file list is captured up front and every listed file must still have content, or the export
/// fails (admins expect a complete archive). File binaries are streamed into the ZIP one at a time —
/// the full set is never held in memory at once — under <c>files/{safeName}</c>, and a minimal
/// <c>file-map.json</c> (id → archive filename, nothing else) is written last.
/// </summary>
public sealed class AdminFileExportService
{
    private static readonly JsonSerializerOptions MapJsonOptions = new(JsonSerializerDefaults.Web);

    // Path separators and shell/zip-hostile characters are replaced; control chars too. Kept
    // explicit (not Path.GetInvalidFileNameChars) so behaviour does not vary by host OS.
    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly OdysseyContext context;

    public AdminFileExportService(OdysseyContext context)
    {
        this.context = context;
    }

    /// <summary>Total stored files and their combined size — a cheap summary for the Settings card / capability response.</summary>
    public async Task<(int Count, long TotalSizeBytes)> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var files = context.FileMetadata.AsNoTracking();
        var count = await files.CountAsync(cancellationToken);
        var totalSizeBytes = count == 0 ? 0L : await files.SumAsync(file => file.SizeBytes, cancellationToken);
        return (count, totalSizeBytes);
    }

    /// <summary>
    /// Captures the file list (deterministically ordered) and verifies every file's stored content is
    /// present. Throws <see cref="FileExportException"/> if any is missing, before a single byte of the
    /// response is written, so the caller can still return a clean error status.
    /// </summary>
    public Task<IReadOnlyList<FileExportItem>> PrepareAsync(CancellationToken cancellationToken) =>
        PrepareCoreAsync(context.FileMetadata.AsNoTracking(), cancellationToken);

    /// <summary>
    /// Same as <see cref="PrepareAsync"/>, but re-runs the Files page's own search/type filters
    /// server-side, unpaginated — the scoped "export filtered" action (Odyssey Design System ·
    /// Files.jsx). <paramref name="kinds"/> matches ANY of the given kinds (the page's Type filter is
    /// multi-select; the general list endpoint's single-value <c>Kind</c> query param doesn't apply
    /// here). A null/empty <paramref name="search"/> or <paramref name="kinds"/> is simply not filtered
    /// on that dimension — <c>null</c> for both degrades to the same result as <see cref="PrepareAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<FileExportItem>> PrepareFilteredAsync(
        string? search, IReadOnlyCollection<FileKind>? kinds, CancellationToken cancellationToken)
    {
        var q = context.FileMetadata.AsNoTracking().AsQueryable();

        var term = ListQuery.NormalizeSearch(search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(file => EF.Functions.Like(file.FileName, pattern));
        }

        // Mirrors FileService.ListAsync's kind → MIME-type mapping, OR-combined across every
        // selected kind rather than switched on a single value.
        if (kinds is { Count: > 0 })
        {
            var wanted = kinds.ToHashSet();
            q = q.Where(file =>
                (wanted.Contains(FileKind.Pdf) && file.ContentType == "application/pdf") ||
                (wanted.Contains(FileKind.Image) && file.ContentType.StartsWith("image/")) ||
                (wanted.Contains(FileKind.File) && file.ContentType != "application/pdf" && !file.ContentType.StartsWith("image/")));
        }

        return PrepareCoreAsync(q, cancellationToken);
    }

    private async Task<IReadOnlyList<FileExportItem>> PrepareCoreAsync(
        IQueryable<FileMetadata> query, CancellationToken cancellationToken)
    {
        var files = await query
            .OrderBy(file => file.Id)
            .Select(file => new FileExportItem(file.Id, file.FileName, file.ContentType, file.FileBlobId))
            .ToListAsync(cancellationToken);

        if (files.Count == 0)
        {
            return files;
        }

        var blobIds = files.Select(file => file.FileBlobId).ToList();
        var presentBlobIds = await context.FileBlob.AsNoTracking()
            .Where(blob => blobIds.Contains(blob.Id))
            .Select(blob => blob.Id)
            .ToListAsync(cancellationToken);

        var present = presentBlobIds.ToHashSet();
        var missing = files.Count(file => !present.Contains(file.FileBlobId));
        if (missing > 0)
        {
            throw new FileExportException($"{missing} file(s) are missing their stored content; export aborted.");
        }

        return files;
    }

    /// <summary>
    /// Streams the ZIP for the prepared <paramref name="files"/> into <paramref name="output"/>:
    /// one <c>files/{safeName}</c> entry per file (binaries loaded one at a time) plus
    /// <c>file-map.json</c>. Entry names are sanitized and de-duplicated so they cannot create unsafe
    /// archive paths.
    /// </summary>
    public async Task WriteZipAsync(Stream output, IReadOnlyList<FileExportItem> files, CancellationToken cancellationToken)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapEntries = new List<FileMapEntry>(files.Count);

        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entryName = UniqueEntryName(file, usedNames);

                var content = await context.FileBlob.AsNoTracking()
                    .Where(blob => blob.Id == file.FileBlobId)
                    .Select(blob => blob.Content)
                    .FirstOrDefaultAsync(cancellationToken);

                if (content is null)
                {
                    // Raced with a delete between PrepareAsync and now — fail the export.
                    throw new FileExportException($"File content for {file.FileId} is no longer available.");
                }

                var entry = archive.CreateEntry($"files/{entryName}", CompressionLevel.Optimal);
                await using (var entryStream = entry.Open())
                {
                    await entryStream.WriteAsync(content, cancellationToken);
                }

                mapEntries.Add(new FileMapEntry(file.FileId.ToString(), entryName));
            }

            var mapArchiveEntry = archive.CreateEntry("file-map.json", CompressionLevel.Optimal);
            await using var mapStream = mapArchiveEntry.Open();
            await JsonSerializer.SerializeAsync(mapStream, new FileMapDocument(mapEntries), MapJsonOptions, cancellationToken);
        }
    }

    private static string UniqueEntryName(FileExportItem file, HashSet<string> usedNames)
    {
        var baseName = SanitizeForZip(file.FileName);
        if (baseName.Length == 0)
        {
            baseName = $"file-{file.FileId}{ExtensionForContentType(file.ContentType)}";
        }

        var candidate = baseName;
        var counter = 1;
        while (!usedNames.Add(candidate))
        {
            counter++;
            candidate = AppendSuffix(baseName, counter);
        }

        return candidate;
    }

    /// <summary>
    /// Strips path separators, control characters, and leading/trailing dots/spaces. Returns an empty
    /// string for names that are empty or reduce to <c>.</c>/<c>..</c> so the caller can fall back to a
    /// <c>file-{id}</c> name — never lets <c>..</c> or a separator influence the archive path.
    /// </summary>
    private static string SanitizeForZip(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var chars = fileName.Select(c => InvalidNameChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim().Trim('.').Trim();

        return cleaned is "" or "." or ".." ? string.Empty : cleaned;
    }

    private static string AppendSuffix(string fileName, int counter)
    {
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return $"{stem} ({counter}){extension}";
    }

    private static string ExtensionForContentType(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf",
        "text/csv" => ".csv",
        "text/plain" => ".txt",
        "application/json" => ".json",
        "application/xml" or "text/xml" => ".xml",
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "application/zip" => ".zip",
        "application/msword" => ".doc",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.ms-excel" => ".xls",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        _ => string.Empty,
    };
}
