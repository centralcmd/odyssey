using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api.FileExport;

/// <summary>
/// Admin-only "export all files" / "export filtered files" ZIP downloads (issue #159, extended per
/// the Files.jsx design update for scoped export). Gated by the <c>files.export-all</c> permission
/// policy — both actions share it; the filtered scope isn't a lesser capability, it's the same bulk
/// export narrowed by a filter the caller already controls client-side. Synchronous streaming option
/// (spec §7.2): the archive is written straight to the response, one file at a time.
/// </summary>
[ApiController]
[Route("api/admin/files")]
[Authorize(Policy = PermissionClaims.FilesExportAll)]
public sealed class AdminFileExportController : ControllerBase
{
    private readonly AdminFileExportService fileExportService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AdminFileExportController> logger;

    public AdminFileExportController(
        AdminFileExportService fileExportService,
        TimeProvider timeProvider,
        ILogger<AdminFileExportController> logger)
    {
        this.fileExportService = fileExportService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>A cheap file count + total size so the client can populate the export control.</summary>
    [HttpGet("export/summary")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileExportSummary))]
    public async Task<ActionResult<FileExportSummary>> GetSummary(CancellationToken cancellationToken)
    {
        var (fileCount, totalSizeBytes) = await fileExportService.GetSummaryAsync(cancellationToken);
        return Ok(new FileExportSummary(fileCount, totalSizeBytes));
    }

    [HttpGet("export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Export(CancellationToken cancellationToken) =>
        await WriteExportAsync(
            () => fileExportService.PrepareAsync(cancellationToken),
            fileNameSuffix: null,
            cancellationToken);

    /// <summary>
    /// Re-runs the Files page's own search + Type filter server-side, unpaginated (Odyssey Design
    /// System · Files.jsx "Export filtered"). <paramref name="filter"/>.Kind is multi-value — the page's
    /// Type filter is a multi-select, unlike the general list endpoint's single-value <c>Kind</c>.
    /// </summary>
    [HttpGet("export/filtered")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> ExportFiltered([FromQuery] FilesExportFilter filter, CancellationToken cancellationToken) =>
        await WriteExportAsync(
            () => fileExportService.PrepareFilteredAsync(filter.Search, filter.Kind, cancellationToken),
            fileNameSuffix: "-filtered",
            cancellationToken);

    private async Task<IActionResult> WriteExportAsync(
        Func<Task<IReadOnlyList<FileExportItem>>> prepare, string? fileNameSuffix, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        logger.LogInformation("File export requested by user {UserId}.", userId);

        // Validate the snapshot BEFORE writing any bytes, so a missing-content failure can still
        // return a clean 500 instead of a truncated download.
        IReadOnlyList<FileExportItem> files;
        try
        {
            files = await prepare();
        }
        catch (FileExportException exception)
        {
            logger.LogError(exception, "File export failed during preparation for user {UserId}.", userId);
            throw;
        }

        // ZipArchive writes its data descriptors / central directory synchronously; ASP.NET Core
        // disallows sync IO on the response body by default. Allow it for this request — file data
        // still streams one entry at a time, so the whole archive is never buffered.
        var bodyControl = HttpContext.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl is not null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        var timestamp = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"odyssey-files-export{fileNameSuffix}-{timestamp}Z.zip";
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        Response.Headers.CacheControl = "no-store";

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await fileExportService.WriteZipAsync(Response.Body, files, cancellationToken);
            stopwatch.Stop();
            logger.LogInformation(
                "File export completed for user {UserId}: {FileCount} files, {ElapsedMs} ms.",
                userId, files.Count, stopwatch.ElapsedMilliseconds);
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("File export cancelled by client for user {UserId}.", userId);
            throw;
        }
        catch (Exception exception) when (!Response.HasStarted)
        {
            logger.LogError(exception, "File export failed for user {UserId}.", userId);
            throw;
        }
        catch (Exception exception)
        {
            // The response is already streaming; the body is necessarily truncated. Log and abort.
            logger.LogError(exception, "File export failed mid-stream for user {UserId}.", userId);
            throw;
        }
    }
}
