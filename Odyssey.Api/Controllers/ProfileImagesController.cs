using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Odyssey.Core.Profiles;
using Odyssey.Dtos.Authorization;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// Streams any user's profile picture (issue #94 §7).
///
/// <para>
/// <b>Its own plural resource, deliberately not under <c>/api/users</c>.</b> It <i>does</i> take a
/// target id — unlike the two write endpoints, which take none at all — and it must not appear to
/// inherit that controller's <c>users.read</c> gate: the picture identifies a person the caller
/// already meets by name on shared records, so gating it on <c>users.read</c> would make it visible
/// to administrators only, which is not the feature.
/// </para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/profile-images")]
public sealed class ProfileImagesController : ControllerBase
{
    private readonly UserProfileImageService service;

    public ProfileImagesController(UserProfileImageService service)
    {
        this.service = service;
    }

    /// <summary>
    /// Streams that user's picture inline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>?v=</c> query parameter is not bound at all</b> — there is no <c>v</c> on this
    /// signature. That makes "it does not affect the response" true by construction rather than by
    /// discipline, and keeps an arbitrary-length caller-supplied string out of request logging. It
    /// exists only so the URL string changes on a replace; the <c>ETag</c> remains the correctness
    /// mechanism.
    /// </para>
    /// <para>
    /// The <c>404</c> names <b>no identifier at all</b> and is byte-identical for "no such user",
    /// "that user has no picture" and "that account is administratively disabled" (§10.10), so it is
    /// not a user-existence oracle and the disabled case is indistinguishable from a deliberate
    /// removal. That ambiguity is what makes disabling a proportionate remedy for an abusive upload.
    /// The endpoint is not exempt from the password-change or onboarding gates — those render before
    /// any avatar does.
    /// </para>
    /// </remarks>
    [HttpGet("{userId}", Name = "GetProfileImage")]
    [Authorize(Policy = PermissionClaims.ProfileImagesRead)]
    [EnableRateLimiting(ProfileImageRateLimiting.ReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Stream a user's profile picture.",
        Description = @"Gated on profile-images.read, which every role holds — revoking it from a role is
the supported way to narrow who sees colleagues' pictures.

404 — with a body naming no identifier — when the user does not exist, has no picture, is
administratively disabled, or the stored content type is not a permitted profile picture. The '?v='
parameter is a cache key only and is never bound or parsed. Served inline with a strong ETag and
no-cache, so a removed picture 404s on the next revalidation rather than hiding behind a cached 200.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "userId")] string userId,
        CancellationToken cancellationToken = default)
    {
        // Metadata only — the blob is never touched here. no-cache makes revalidation the hot path, so
        // materialising the LONGBLOB to answer a conditional request would make every return visit pay
        // for a response that carries no body.
        var descriptor = await service.GetDescriptorAsync(userId, cancellationToken);
        if (descriptor is null)
        {
            return NoPicture();
        }

        ApplyImageHeaders(descriptor.ContentType);

        var etag = new EntityTagHeaderValue($"\"{descriptor.Sha256Hash}\"");
        if (IfNoneMatches(etag))
        {
            // The 304 that never reads the blob — the figure that matters most under no-cache.
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var content = await service.GetContentAsync(descriptor.ImageId, cancellationToken);
        if (content is null)
        {
            return NoPicture();
        }

        // The FileResult overload that TAKES an EntityTagHeaderValue, so ASP.NET Core performs
        // conditional-request handling itself. A manually assigned Response.Headers.ETag does not
        // engage that machinery at all, so copying that shape would return 200 every time.
        return File(content, descriptor.ContentType, lastModified: null, entityTag: etag);
    }

    /// <summary>
    /// One body for all three refusal reasons, naming no identifier. A user identifier is a different
    /// class of thing from the contact endpoint's GUID echo, and the generic message costs nothing
    /// here. The one residual, stated rather than closed: a caller who already holds a valid user id
    /// learns from the 200/404 split whether that person has uploaded a picture — a weak liveness
    /// signal inherent to serving the image at all, and not worth a constant-time response.
    /// </summary>
    private IActionResult NoPicture() => this.NotFoundProblem("No profile picture is available.");

    /// <summary>
    /// The product's <b>second</b> inline-served untrusted-bytes surface, carrying the same header set
    /// as the first.
    ///
    /// <para>
    /// <b><c>Cross-Origin-Resource-Policy: same-site</c>, not <c>same-origin</c>.</b> CORP is enforced
    /// on no-cors subresource loads — exactly how an <c>&lt;img src&gt;</c> loads — and compares
    /// scheme, host <b>and port</b>. The dev client serves from 5199 while the API listens on 5188, so
    /// <c>same-origin</c> would block every picture outside Docker, and silently: a load failure
    /// degrades to the monogram with nothing surfaced.
    /// </para>
    ///
    /// <para>
    /// <c>no-cache</c> (revalidate every time), not <c>no-store</c>: it is what makes revocation and
    /// erasure immediate — a removed picture 404s on the next revalidation rather than hiding behind a
    /// cached 200 — while still allowing the 304 that carries no body.
    /// </para>
    /// </summary>
    private void ApplyImageHeaders(string contentType)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentDisposition = "inline";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-site";
        Response.Headers.CacheControl = "private, no-cache";
        Response.ContentType = contentType;
    }

    /// <summary>
    /// Whether the request's <c>If-None-Match</c> matches the picture's strong <c>ETag</c>. Checked
    /// here, before the blob query, so a revalidation costs the metadata read and nothing else.
    /// </summary>
    private bool IfNoneMatches(EntityTagHeaderValue etag)
    {
        var candidates = Request.GetTypedHeaders().IfNoneMatch;
        if (candidates is null || candidates.Count == 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(etag, useStrongComparison: true))
            {
                return true;
            }
        }

        return false;
    }
}
