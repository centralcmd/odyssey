using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api.DataExport;

/// <summary>
/// Admin-only database JSON export (issue #160). Gated by the <c>data.export</c> permission policy.
/// </summary>
[ApiController]
[Route("api/admin/data-export")]
[Authorize(Policy = PermissionClaims.DataExport)]
public sealed class DataExportController : ControllerBase
{
    private readonly DataExportService dataExportService;
    private readonly ILogger<DataExportController> logger;

    public DataExportController(
        DataExportService dataExportService,
        ILogger<DataExportController> logger)
    {
        this.dataExportService = dataExportService;
        this.logger = logger;
    }

    /// <summary>
    /// Streams the export straight to the response body (issue #395). Nothing is buffered: there is no
    /// <c>byte[]</c> of the payload and no in-memory <see cref="DataExportDocument"/>, so peak memory
    /// is flat in the size of the database rather than two full copies of it. The trade is that the
    /// response is chunked — no <c>Content-Length</c>, so no browser progress bar — and that once the
    /// first byte is out, a failure can no longer become a ProblemDetails. A reader must therefore
    /// treat a body without the terminal <see cref="DataExportDocument.Complete"/> sentinel as a
    /// partial export and discard it; <c>DataExportApiClient.DownloadAsync</c> does that check before
    /// handing the file to the browser (issue #401).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DataExportDocument))]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var exportedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        var header = dataExportService.CreateHeader(exportedByUserId);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var contentDisposition = new ContentDispositionHeaderValue("attachment");
            contentDisposition.SetHttpFileName(header.FileName);

            Response.ContentType = "application/json; charset=utf-8";
            Response.Headers.CacheControl = "no-store";
            Response.Headers.ContentDisposition = contentDisposition.ToString();

            var summary = await dataExportService.WriteExportAsync(Response.Body, header, cancellationToken);
            stopwatch.Stop();

            // Metadata only — never the payload itself.
            logger.LogInformation(
                "Data export succeeded for user {UserId}: {ByteCount} bytes, {ElapsedMs} ms, row counts {@RowCounts}.",
                exportedByUserId,
                summary.ByteCount,
                stopwatch.ElapsedMilliseconds,
                summary.RowCounts);

            return new EmptyResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Data export cancelled by client for user {UserId}.", exportedByUserId);
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            // Past the first byte the status and headers are already sent, so this cannot become a
            // ProblemDetails — the client gets a truncated body with no completeness sentinel.
            logger.LogError(
                exception,
                "Data export failed for user {UserId} after {ElapsedMs} ms ({ResponseState}).",
                exportedByUserId,
                stopwatch.ElapsedMilliseconds,
                Response.HasStarted ? "response already started" : "nothing written");
            throw;
        }
    }
}
