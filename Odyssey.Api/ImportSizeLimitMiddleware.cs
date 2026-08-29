using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;

namespace Odyssey.Api;

/// <summary>The four import surfaces an <see cref="ImportSizeLimitAttribute"/> can name.</summary>
public enum ImportSurface
{
    Contacts,
    Calendars,
    Tasks,
    JournalEntries,
}

/// <summary>
/// Tags a controller action as one whose request-body size <see cref="ImportSizeLimitMiddleware"/>
/// must cap to the configured, admin-editable limit for <see cref="Surface"/> (issue #343 §5/§7 item
/// 4). Controller action attributes are automatically part of the endpoint's
/// <c>Endpoint.Metadata</c>, so no separate <c>.WithMetadata(...)</c> call is needed — the middleware
/// reads it straight off <see cref="HttpContext.GetEndpoint"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ImportSizeLimitAttribute(ImportSurface surface) : Attribute
{
    public ImportSurface Surface { get; } = surface;
}

/// <summary>
/// Tags a file-upload action as one whose request-body size must be capped to the admin-editable
/// upload limit (issue #421 Wave 4). Handled by the same middleware as
/// <see cref="ImportSizeLimitAttribute"/>: the mechanism is identical — resolve a live cap, rewrite the
/// transport limit and the multipart limit for this request — and only the source of the number
/// differs. A second middleware would have had to re-derive the ordering constraint below, which is
/// the part that is easy to get wrong.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UploadSizeLimitAttribute : Attribute;

/// <summary>
/// Applies the configured, admin-editable per-surface byte cap to the request transport — the
/// mechanism that makes the vCard import's size limit actually take effect (issue #343 §5, replacing
/// the dead <c>[RequestSizeLimit(500 MB)]</c> that never touched the global 65 MB multipart limit).
/// <para>
/// <b>Ordering is a constraint, not a position</b> (§5): registered after routing (so
/// <see cref="HttpContext.GetEndpoint"/> resolves), after <c>UseAuthentication</c>/
/// <c>UseAuthorization</c> (so an anonymous/unauthorized request is short-circuited before the
/// settings lookup runs), and before <c>UseAntiforgery()</c> — before anything that could read the
/// request body. Registered unconditionally, including in the Testing environment, so every test tier
/// exercises the same code path (the Testing environment is exempt from the antiforgery tagging that
/// makes this necessary, but the middleware itself is not conditioned on environment).
/// </para>
/// </summary>
public sealed class ImportSizeLimitMiddleware(RequestDelegate next, ILogger<ImportSizeLimitMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IImportExportLimitsLookup limitsLookup,
        IUploadLimitsLookup uploadLimitsLookup,
        IOptions<FormOptions> configuredFormOptions,
        IOptions<FileStorageOptions> fileStorageOptions)
    {
        var endpoint = context.GetEndpoint();
        var importAttribute = endpoint?.Metadata.GetMetadata<ImportSizeLimitAttribute>();
        var uploadAttribute = endpoint?.Metadata.GetMetadata<UploadSizeLimitAttribute>();
        if (importAttribute is null && uploadAttribute is null)
        {
            await next(context);
            return;
        }

        long capBytes;
        if (importAttribute is not null)
        {
            var limits = await limitsLookup.GetAsync(context.RequestAborted);
            capBytes = importAttribute.Surface switch
            {
                ImportSurface.Contacts => limits.ContactVCardMaxImportBytes,
                ImportSurface.Calendars => limits.CalendarIcsMaxImportBytes,
                ImportSurface.Tasks => limits.TaskIcsMaxImportBytes,
                ImportSurface.JournalEntries => limits.JournalIcsMaxImportBytes,
                _ => throw new InvalidOperationException($"Unhandled import surface '{importAttribute.Surface}'."),
            };
        }
        else
        {
            capBytes = (await uploadLimitsLookup.GetAsync(context.RequestAborted)).MaxUploadBytes;
        }

        // The headroom applies to the TRANSPORT limit only, so a full-size file isn't rejected by its
        // own multipart boundary bytes — the MultipartBodyLengthLimit below and the service's own
        // content-length check use the bare cap, so the operator's configured number stays the limit
        // on file content rather than silently becoming cap + headroom (§5 "arch N3").
        var maxBodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBodySizeFeature is { IsReadOnly: false })
        {
            maxBodySizeFeature.MaxRequestBodySize = capBytes + fileStorageOptions.Value.RequestEnvelopeHeadroomBytes;
        }
        else
        {
            // Absent under TestServer, and read-only once the body is already in flight — a missing or
            // read-only feature is skipped, never assumed present (§5 "arch N4", AC 51).
            logger.LogDebug(
                "IHttpMaxRequestBodySizeFeature is unavailable or read-only for {Path}; the transport request-body limit was not adjusted.",
                context.Request.Path);
        }

        var configured = configuredFormOptions.Value;
        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
        {
            BufferBody = configured.BufferBody,
            MemoryBufferThreshold = configured.MemoryBufferThreshold,
            BufferBodyLengthLimit = configured.BufferBodyLengthLimit,
            ValueCountLimit = configured.ValueCountLimit,
            KeyLengthLimit = configured.KeyLengthLimit,
            ValueLengthLimit = configured.ValueLengthLimit,
            MultipartBoundaryLengthLimit = configured.MultipartBoundaryLengthLimit,
            MultipartHeadersCountLimit = configured.MultipartHeadersCountLimit,
            MultipartHeadersLengthLimit = configured.MultipartHeadersLengthLimit,
            MultipartBodyLengthLimit = capBytes,
        }));

        try
        {
            await next(context);
        }
        catch (BadHttpRequestException oversize)
            when (oversize.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel and the multipart reader both signal "over the limit" by throwing this, which
            // otherwise reaches the global handler as an unhandled fault and is reported as a 500 with
            // an error id — telling the user the server broke when in fact they were told a rule.
            // 413 is the answer, and it names the cap so the client can say which number it hit.
            logger.LogInformation(
                "Rejected an oversize request body on {Path}; the cap is {CapBytes} bytes.",
                context.Request.Path, capBytes);

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Payload too large",
                Detail = $"The request body exceeds the maximum of {capBytes} bytes.",
            });
        }
    }
}
