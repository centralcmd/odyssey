using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Interop;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The one response shape every calendar <c>.ics</c> export and summary shares (issue #516 §3, A8), so the four exports
/// cannot disagree about headers, the not-found body or what a mid-stream byte-cap overrun does.
/// </summary>
internal static class CalendarIcsExportResponder
{
    public const string ExportRowsHeader = "X-Odyssey-Export-Rows";

    public static async Task<IActionResult> StreamAsync(
        ControllerBase controller, CalendarIcsService service, CalendarIcsExportScope scope, string notFound,
        CancellationToken cancellationToken)
    {
        var response = controller.Response;
        try
        {
            var found = await service.ExportStreamingAsync(scope, response.Body, (fileName, rowCount) =>
            {
                response.Headers.XContentTypeOptions = "nosniff";
                response.Headers.CacheControl = "no-store";
                response.ContentType = "text/calendar; charset=utf-8";
                var contentDisposition = new ContentDispositionHeaderValue("attachment");
                contentDisposition.SetHttpFileName(fileName);
                response.Headers[HeaderNames.ContentDisposition] = contentDisposition.ToString();
                response.Headers[ExportRowsHeader] = rowCount.ToString(CultureInfo.InvariantCulture);
            }, cancellationToken, includePlaceNames: controller.User.HasPermission(PermissionClaims.LocationsRead),
                userId: controller.User.ActingUserId());
            if (!found)
            {
                return controller.NotFoundProblem(notFound);
            }
        }
        catch (IcsExportSizeExceededException)
        {
            // The body has started, so no status can be sent: abort, so the download fails rather than ending early.
            controller.HttpContext.Abort();
        }

        return new EmptyResult();
    }

    public static async Task<ActionResult<CalendarIcsExportSummary>> SummarizeAsync(
        ControllerBase controller, CalendarIcsService service, CalendarIcsExportScope scope, string notFound,
        CancellationToken cancellationToken) =>
        await service.SummarizeExportAsync(scope, cancellationToken) is { } summary
            ? summary
            : controller.NotFoundProblem(notFound);
}
