using Microsoft.AspNetCore.Mvc;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;

namespace Odyssey.Api;

/// <summary>
/// The attach-time check and the download response shared by the contract and property document
/// surfaces (issue #210 §6, §7.9) — one implementation, so the two cannot drift on which content types
/// attach or on the safe-download headers.
/// </summary>
public static class DocumentAttachmentExtensions
{
    /// <summary>
    /// Validates an attach target: the file must exist and its server-recorded content type must be on
    /// <see cref="DocumentContentTypes.Allowed"/>. Returns a problem result to short-circuit, or
    /// <c>null</c> when the file may be attached. <paramref name="surface"/> names the document kind in
    /// the rejection (<c>"contract"</c>, <c>"property"</c>).
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

        controller.Response.Headers.XContentTypeOptions = "nosniff";
        controller.Response.Headers.ETag = $"\"{metadata.Sha256Hash}\"";
        return controller.File(content, metadata.ContentType, metadata.FileName);
    }
}
