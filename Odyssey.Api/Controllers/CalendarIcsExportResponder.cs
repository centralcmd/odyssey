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

    // The caller's attachment access for one export (issue #521 §5.4): the mode, and the two read claims.
    private static CalendarIcsAttachmentAccess Access(ControllerBase controller, IcsAttachmentsMode mode) =>
        new(mode, controller.User.HasPermission(PermissionClaims.FilesRead), controller.User.HasPermission(PermissionClaims.PhotosRead));

    public static async Task<IActionResult> StreamAsync(
        ControllerBase controller, CalendarIcsService service, CalendarIcsExportScope scope, string notFound,
        CancellationToken cancellationToken, IcsAttachmentsMode attachments = IcsAttachmentsMode.Include)
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
                userId: controller.User.ActingUserId(), access: Access(controller, attachments));
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
        CancellationToken cancellationToken, IcsAttachmentsMode attachments = IcsAttachmentsMode.Include) =>
        await service.SummarizeExportAsync(scope, cancellationToken, Access(controller, attachments)) is { } summary
            ? summary
            : controller.NotFoundProblem(notFound);
}
