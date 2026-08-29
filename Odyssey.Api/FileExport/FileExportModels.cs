namespace Odyssey.Api.FileExport;

/// <summary>A single file to export — the columns needed to locate content and name the ZIP entry.</summary>
public sealed record FileExportItem(Guid FileId, string FileName, string ContentType, Guid FileBlobId);

/// <summary>One <c>file-map.json</c> row: maps a stored <see cref="FileId"/> to the name used in the ZIP.</summary>
public sealed record FileMapEntry(string FileId, string FileName);

/// <summary>The minimal <c>file-map.json</c> artifact — only id→archive-filename, no other metadata.</summary>
public sealed record FileMapDocument(IReadOnlyList<FileMapEntry> Files);

/// <summary>Client-facing summary (cheap file count + total size) for the admin file-export feature.</summary>
public sealed record FileExportSummary(int FileCount, long TotalSizeBytes);

/// <summary>
/// Raised when an export cannot be produced as a complete snapshot — e.g. a file's stored content
/// is missing. Surfaces as a generic <c>500</c> to the client; details are logged server-side.
/// </summary>
public sealed class FileExportException : Exception
{
    public FileExportException(string message) : base(message)
    {
    }
}
