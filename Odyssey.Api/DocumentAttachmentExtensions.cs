using Microsoft.AspNetCore.Mvc;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;

namespace Odyssey.Api;

/// <summary>
/// The attach-time check and the download response shared by the contract, property and tax-statement
/// document surfaces (issue #210 §6, §7.9; issue #287 H4) — one implementation, so they cannot drift on
/// which content types attach or on the safe-download headers.
/// </summary>
public static class DocumentAttachmentExtensions
{
    /// <summary>
    /// Validates an attach target: the file must exist and its server-recorded content type must be on
    /// <see cref="DocumentContentTypes.Allowed"/>. Returns a problem result to short-circuit, or
    /// <c>null</c> when the file may be attached. <paramref name="surface"/> names the document kind in
    /// the rejection (<c>"contract"</c>, <c>"property"</c>, <c>"tax statement"</c>).
    /// </summary>
    public static async Task<IActionResult?> ValidateAttachableDocumentAsync(
        this ControllerBase controller,
        FileService fileService,
        Guid fileId,
        string surface,
        CancellationToken cancellationToken = default)
    {
        var metadata = await fileService.GetFileMetadataAsync(fileId, cancellationToken);
        if (metadata is null)
        {
            return controller.NotFoundProblem($"File ID {fileId} not found.");
        }

        if (!DocumentContentTypes.Allowed.Contains(metadata.ContentType))
        {
            return controller.BadRequestProblem(
                $"Content type '{metadata.ContentType}' is not allowed for {surface} documents.");
        }

        return null;
    }

    /// <summary>
    /// Streams a stored file as a forced download: the stored content type, an attachment disposition,
    /// <c>X-Content-Type-Options: nosniff</c> so a mislabeled upload cannot be rendered inline, and a
    /// strong <c>ETag</c> of the quoted SHA-256.
    /// </summary>
    public static async Task<IActionResult> StreamDocumentAsync(
        this ControllerBase controller,
        FileService fileService,
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        var (metadata, content) = await fileService.GetFileContentAsync(fileId, cancellationToken);
        if (metadata is null || content is null)
        {
            return controller.NotFound();
        }

        return controller.ServeDocument(metadata, content);
    }

    /// <summary>
    /// The download response every document endpoint shares (issue #448 §5.3), for a file on disk or a legacy one alike:
    /// <c>nosniff</c>; the strong <c>ETag</c> handed to the framework, so <c>If-None-Match</c> answers <c>304</c> and
    /// <c>If-Range</c> works; range processing; <c>Cache-Control: private, no-cache</c>; and the forced attachment
    /// disposition, written by <c>File(…, fileDownloadName)</c> and never by hand (issue #247), so a stored name holding a
    /// quote, CR/LF or non-ASCII text cannot break the header.
    /// </summary>
    public static IActionResult ServeDocument(this ControllerBase controller, FileMetadataResponse metadata, Stream content)
    {
        controller.Response.Headers.XContentTypeOptions = "nosniff";
        controller.Response.Headers.CacheControl = "private, no-cache";
        return controller.File(
            content, metadata.ContentType, metadata.FileName, lastModified: null,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{metadata.Sha256Hash}\""),
            enableRangeProcessing: true);
    }
}
