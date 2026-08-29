using System.Net;
using System.Net.Http.Json;

namespace Odyssey.ApiClient.Resources;

/// <summary>The stored file count and total size, for populating the admin file-export control.</summary>
public sealed record FileExportSummary(int FileCount, long TotalSizeBytes);

/// <summary>How a download attempt resolved — drives the user-facing message on the Settings page.</summary>
public enum FileExportOutcome
{
    Success,
    Forbidden,
    Conflict,
    Failed,
}

/// <summary>A download attempt: the archive on success, otherwise the reason it failed.</summary>
public sealed record FileExportResult(FileExportOutcome Outcome, ApiFile? File);

/// <summary>
/// Typed client for the admin "export all files" endpoints (issue #159; extended with a scoped
/// "export filtered" per the Files.jsx design update). <see cref="GetSummaryAsync"/> fetches the
/// stored file count + total size for the Settings card (the endpoint is <c>files.export-all</c>-gated,
/// so availability is the permission alone); <see cref="DownloadAsync"/>/<see cref="DownloadFilteredAsync"/>
/// fetch the ZIP and map the HTTP status to a <see cref="FileExportOutcome"/> so the page can message
/// forbidden / conflict / generic failures distinctly.
/// </summary>
public interface IFileExportApiClient
{
    /// <summary>The stored file count + total size for the current user. Zeroes on any failure.</summary>
    Task<FileExportSummary> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>Downloads the files ZIP archive, or reports why it could not be produced.</summary>
    Task<FileExportResult> DownloadAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the files ZIP archive scoped to the given search/kind filter — the same filter the
    /// Files page's own list is currently showing, re-run server-side and unpaginated. <paramref name="kinds"/>
    /// matches ANY of the given kind labels ("PDF"/"Image"/"File", case-insensitive — the same labels
    /// the page's Type multi-select already uses).
    /// </summary>
    Task<FileExportResult> DownloadFilteredAsync(
        string? search = null, IReadOnlyCollection<string>? kinds = null, CancellationToken ct = default);
}

/// <inheritdoc cref="IFileExportApiClient" />
public sealed class FileExportApiClient(IOdysseyApi api) : IFileExportApiClient
{
    private const string ExportPath = "api/admin/files/export";

    public async Task<FileExportSummary> GetSummaryAsync(CancellationToken ct = default) =>
        (await api.GetAsync<FileExportSummary>($"{ExportPath}/summary", ct)).ValueOr(new FileExportSummary(0, 0));

    public Task<FileExportResult> DownloadAsync(CancellationToken ct = default) =>
        DownloadCoreAsync(ExportPath, "odyssey-files-export.zip", ct);

    public Task<FileExportResult> DownloadFilteredAsync(
        string? search = null, IReadOnlyCollection<string>? kinds = null, CancellationToken ct = default) =>
        DownloadCoreAsync(
            PagedQuery.For($"{ExportPath}/filtered").Add("search", search).AddMany("kind", kinds).Build(),
            "odyssey-files-export-filtered.zip",
            ct);

    private async Task<FileExportResult> DownloadCoreAsync(string url, string defaultFileName, CancellationToken ct)
    {
        var result = await api.GetFileAsync(url, defaultFileName, ct: ct);
        if (result.IsSuccess && result.Value is { } file)
            return new FileExportResult(FileExportOutcome.Success, file);

        var outcome = result.Status switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => FileExportOutcome.Forbidden,
            HttpStatusCode.Conflict => FileExportOutcome.Conflict,
            _ => FileExportOutcome.Failed,
        };
        return new FileExportResult(outcome, null);
    }
}
