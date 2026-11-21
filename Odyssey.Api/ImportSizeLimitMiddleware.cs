using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Options;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;

namespace Odyssey.Api;

/// <summary>The import surfaces an <see cref="ImportSizeLimitAttribute"/> can name.</summary>
public enum ImportSurface
{
    Contacts,
    Calendars,
    Tasks,
    JournalEntries,

    /// <summary>The news OPML import (issue #508). Its arm also sets the upload-rate floor.</summary>
    NewsOpml,
}

/// <summary>
/// Tags a controller action as one whose request-body size <see cref="ImportSizeLimitMiddleware"/>
/// must cap to the configured, admin-editable limit for <see cref="Surface"/> (issue #343 §5/§7 item
/// 4). Controller action attributes are automatically part of the endpoint's
/// <c>Endpoint.Metadata</c>, so no separate <c>.WithMetadata(...)</c> call is needed — the middleware
/// reads it straight off <see cref="EndpointHttpContextExtensions.GetEndpoint"/>.
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
/// Tags an upload whose TRANSPORT limit is the admin upload cap (plus the envelope headroom) while its multipart
/// limit stays at the ceiling — the photo upload, whose service answers a file just over the cap with a 413 naming
/// it (#428 §5.1), which the multipart reader's own refusal inside model binding could not. A body well over the cap
/// is refused by the transport before it is spooled, also as a 413 naming the cap.
/// <para>
/// Every action that carries none of this, <see cref="ImportSizeLimitAttribute"/> and
/// <see cref="UploadSizeLimitAttribute"/> is held to <see cref="FileStorageOptions.DefaultMaxRequestBodyBytes"/>, so
/// raising the ceiling for large files never lets a JSON endpoint — sign-in included — accept a multi-gigabyte body.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UploadTransportLimitAttribute : Attribute;

/// <summary>
/// Applies the configured, admin-editable per-surface byte cap to the request transport — the
/// mechanism that makes the vCard import's size limit actually take effect (issue #343 §5, replacing
/// the dead <c>[RequestSizeLimit(500 MB)]</c> that never touched the global 65 MB multipart limit).
/// <para>
/// <b>Ordering is a constraint, not a position</b> (§5): registered after routing (so
/// <see cref="EndpointHttpContextExtensions.GetEndpoint"/> resolves), after <c>UseAuthentication</c>/
/// <c>UseAuthorization</c> (so an anonymous/unauthorized request is short-circuited before the
/// settings lookup runs), and before <c>UseAntiforgery()</c> — before anything that could read the
/// request body. Registered unconditionally, including in the Testing environment, so every test tier
/// exercises the same code path (the Testing environment is exempt from the antiforgery tagging that
/// makes this necessary, but the middleware itself is not conditioned on environment).
/// </para>
/// </summary>
public sealed class ImportSizeLimitMiddleware(RequestDelegate next, ILogger<ImportSizeLimitMiddleware> logger)
{
    /// <summary>
    /// The OPML upload's minimum data rate (issue #508 §5.2, B2, R2-3): one import holds the single OPML permit for as
    /// long as its body takes to arrive, and Kestrel's default of 240 B/s would let a slow client hold it for days. At
    /// 64 KiB/s a 64 MB upload finishes within about 17 minutes or is cut off.
    /// </summary>
    internal static readonly MinDataRate NewsOpmlMinBodyDataRate = new(bytesPerSecond: 64 * 1024, gracePeriod: TimeSpan.FromSeconds(10));

    /// <summary>
    /// Sets the request's minimum body data rate. The feature is absent under TestServer, and Kestrel refuses the
    /// setter on HTTP/2 and HTTP/3, where the server-wide rate applies instead; both are logged and skipped. The log lines
    /// carry no request value: the path and protocol are client-supplied (CodeQL cs/log-forging).
    /// </summary>
    internal void ApplyMinBodyDataRate(HttpContext context, MinDataRate rate)
    {
        if (context.Features.Get<IHttpMinRequestBodyDataRateFeature>() is not { } feature)
        {
            logger.LogDebug("IHttpMinRequestBodyDataRateFeature is unavailable; the upload-rate floor was not set.");
            return;
        }

        try
        {
            feature.MinDataRate = rate;
        }
        catch (NotSupportedException)
        {
            logger.LogDebug("The upload-rate floor is not supported on this protocol; the server-wide rate applies.");
        }
    }

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
            if (endpoint?.Metadata.GetMetadata<UploadTransportLimitAttribute>() is null)
            {
                LowerToDefault(context, fileStorageOptions.Value.DefaultMaxRequestBodyBytes);
                await next(context);
                return;
            }

            var uploadCap = (await uploadLimitsLookup.GetAsync(context.RequestAborted)).MaxUploadBytes;
            LowerToDefault(context, uploadCap + fileStorageOptions.Value.RequestEnvelopeHeadroomBytes);
            await NextAnsweringOversizeAsync(context, uploadCap);
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
                ImportSurface.NewsOpml => limits.NewsOpmlMaxImportBytes,
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

        if (importAttribute?.Surface == ImportSurface.NewsOpml)
        {
            // Before anything reads the body, like the size cap: the resource filter that holds the OPML permit runs
            // before model binding, so a floor set any later would let a slow body hold the permit regardless.
            ApplyMinBodyDataRate(context, NewsOpmlMinBodyDataRate);
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

        await NextAnsweringOversizeAsync(context, capBytes);
    }

    private async Task NextAnsweringOversizeAsync(HttpContext context, long capBytes)
    {
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

    // Only ever lowers: an endpoint attribute such as [RequestSizeLimit] still applies later, in the MVC filter.
    private static void LowerToDefault(HttpContext context, long defaultBytes)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false } && (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > defaultBytes))
        {
            feature.MaxRequestBodySize = defaultBytes;
        }
    }
}
