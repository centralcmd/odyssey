using Odyssey.Api.Identity;
using Odyssey.Dtos.Application;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Odyssey.Core.Profiles;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Profiles;

/// <summary>
/// Self-service profile endpoints (issue #316). Both operate strictly on the authenticated caller's own
/// row — there is no id in the path, so no IDOR and no cross-user access. Any authenticated user may
/// read/write their own profile; no permission claim is required (per-user owned data, not an
/// admin-gated resource).
/// </summary>
[ApiController]
[Authorize]
[Route("api/profile")]
public sealed class ProfileController : ControllerBase
{
    private readonly ProfileService service;
    private readonly UserProfileImageService images;

    public ProfileController(ProfileService service, UserProfileImageService images)
    {
        this.service = service;
        this.images = images;
    }

    // Exempt from the must-change-password block (issue #406): this is where the client reads the flag,
    // so a gated user must be able to fetch it or the gate page could not render. The write below is
    // deliberately not exempt.
    [HttpGet(Name = "GetProfile")]
    [PasswordChangeExempt]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ProfileDto))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [SwaggerOperation(Summary = "Read the current user's own profile and its completeness flag.")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        return Ok(await service.GetAsync(userId, cancellationToken));
    }

    [HttpPut(Name = "PutProfile")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ProfileDto))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [SwaggerOperation(Summary = "Set the current user's own profile (required fields must be present & valid).")]
    public async Task<IActionResult> Put(
        [FromBody] ProfileDto request,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await service.SaveAsync(userId, request, cancellationToken));
        }
        catch (ProfileValidationException exception)
        {
            return this.BadRequestProblem(exception.Message);
        }
    }

    // ── Profile picture (issue #94 §7) ────────────────────────────────────────
    //
    // Both writes hang off /api/profile, which already has no id and is already self-scoped. That is
    // the whole IDOR mitigation and it is STRUCTURAL rather than a check (§10.1): there is no user id
    // in the route or the body, so there is nothing to tamper with and no authorization comparison to
    // forget. Unlike the finance domain — a shared workspace with no per-row owner — a profile picture
    // is per-user owned data and a genuine IDOR surface, so it is treated as one.
    //
    // Claim-free, like PUT above: per-user owned data, not an admin-gated resource. What a bare
    // authenticated session confers here is exactly the §4 pipeline at one row per user, with no
    // caller-controlled metadata — <= 2 MB, one of three allow-listed still image types, magic-byte
    // checked, <= 1024 square, non-animated, metadata-stripped and re-validated.
    //
    // THAT IS AN INVARIANT, NOT A ONE-TIME JUDGEMENT (§10.16): a change widening any of it — a larger
    // cap, a new container type, caller-supplied metadata, more than one row — re-opens the
    // claim-gating question rather than inheriting this answer.
    //
    // NEITHER is [PasswordChangeExempt]. A user under an admin-initiated reset changes their password
    // first; the exemption is matched on method AND exact route, so this is the default rather than
    // something to arrange — but it is asserted rather than assumed.

    [HttpPost("image", Name = "PostProfileImage")]
    [Consumes("multipart/form-data")]
    // The transport gate resolves the GLOBAL cap, not this surface's min — so it is the coarse gate and
    // the action's own check below is the binding one.
    [UploadSizeLimit]
    [EnableRateLimiting(ProfileImageRateLimiting.WritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ProfileImageVersionDto))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Attach or replace the caller's own profile picture.",
        Description = @"Takes the image BYTES as multipart/form-data under the part name 'file' — never a
fileId. A caller-supplied id would let a self-service write point an identity row at arbitrary stored
bytes and read them back through the authenticated read, an arbitrary-file-read primitive from two
innocuous capabilities.

There is no user id in the route or the body: this always writes the caller's own row.

Returns 200 with the new ImageVersion — not 204, because the client needs the token to re-key the
image URL. The bytes are validated against a narrow allow-list (PNG, JPEG or WebP; magic bytes checked;
at most 1024 x 1024; not animated), stripped of all embedded metadata and re-validated before storage.")]
    public async Task<IActionResult> PostImage(
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        // Only `file` is bound. Any other form field — a userId, a fileId, a contentType, a sizeBytes —
        // is not a parameter of this action and reaches nothing (§10.3).
        if (file is null || file.Length == 0)
        {
            return this.BadRequestProblem("An image file is required.");
        }

        var effectiveMaxBytes = await images.GetEffectiveMaxBytesAsync(cancellationToken);
        if (file.Length > effectiveMaxBytes)
        {
            // Rejected before the body is copied into a managed array, and the message names the number
            // actually in force rather than a compiled-in one. Note what this does NOT prevent: the
            // body has already reached the server, because IFormFile.Length exists only after multipart
            // parsing and [UploadSizeLimit] admits the global ceiling. The rate limit above is what
            // bounds that, and it is sized against the global cap for exactly this reason.
            return this.BadRequestProblem(
                $"The image must be {FormatMegabytes(effectiveMaxBytes)} or smaller. "
                + "Crop a smaller area, or choose a different file.");
        }

        var bytes = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        var version = await images.SetAsync(userId, bytes, file.ContentType, cancellationToken);
        return Ok(new ProfileImageVersionDto { ImageVersion = version });
    }

    [HttpDelete("image", Name = "DeleteProfileImage")]
    [EnableRateLimiting(ProfileImageRateLimiting.DeletePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Remove the caller's own profile picture.",
        Description = @"Deletes the stored bytes as well as the row — one of the three destructive routes
that satisfy a GDPR Art. 17 erasure request (the others being the replace half of POST and the account
delete's cascade).

Its rate limit is a SEPARATE budget from the upload's: sharing one would mean a user who has exhausted
it uploading could not remove their picture, which throttles the erasure control itself.")]
    public async Task<IActionResult> DeleteImage(CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var outcome = await images.RemoveAsync(userId, cancellationToken);
        return outcome == ProfileImageRemoval.NotFound
            ? this.NotFoundProblem("You have no profile picture to remove.")
            : NoContent();
    }

    private static string FormatMegabytes(long bytes) =>
        $"{Math.Round(bytes / (1024d * 1024d), 1)} MB";
}
