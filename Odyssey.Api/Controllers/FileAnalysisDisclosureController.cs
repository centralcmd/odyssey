using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Dtos.Application;
using Odyssey.Core.Finance;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The processor disclosure the analyze-file consent gate renders (issue #421 Wave 1).
///
/// <para>
/// <strong>Authenticated but claim-free</strong>, following <see cref="ImportLimitsController"/>. That
/// is a deliberate widening and it is justified field by field: all five values are information the
/// application is obliged to <em>show</em> the user at the point of consent (GDPR Art. 13), so gating
/// them behind an admin claim while simultaneously displaying them in the gate would be incoherent.
/// The response is a purpose-built minimal projection, never the admin DTO, so it cannot leak the
/// security toggles, the volume caps or the last administrator's identity.
/// </para>
/// </summary>
[ApiController]
[Route("api/file-analysis/disclosure")]
[Authorize]
public sealed class FileAnalysisDisclosureController(
    IFileAnalysisSettingsLookup lookup) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileAnalysisDisclosureDto))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The processor disclosure shown in the analyze-file consent gate.")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var settings = await lookup.GetAsync(cancellationToken);

        // The LIVE read, deliberately not the snapshot above (issue #439 §5.1): the gate must reflect a
        // disable on the very next request, and the snapshot is cached for 30 seconds.
        var enabled = await lookup.IsEnabledAsync(cancellationToken);

        // A degraded read must never be presented as authoritative. That argument is stronger here
        // than for the import limits it borrows: this is legal disclosure text, and telling a user the
        // wrong processor or region is worse than telling them nothing. The client renders its own
        // compiled fallback on failure and keeps the affirmation disabled, so a 503 costs nothing.
        if (settings.IsDegraded)
        {
            return this.ServiceUnavailableProblem(
                "The analysis disclosure is temporarily unavailable while the server recovers a configuration problem.");
        }

        return Ok(new FileAnalysisDisclosureDto
        {
            Processor = settings.Processor,
            ProcessorRegion = settings.ProcessorRegion,
            LawfulBasis = settings.LawfulBasis,
            PrivacyNoticeUrl = settings.PrivacyNoticeUrl,
            // Non-null whenever IsDegraded is false — a degraded read is the only way these resolve to
            // null, and that path returned above.
            Model = settings.Model ?? string.Empty,
            Enabled = enabled,
            // Computed from the same snapshot the analyze path will recompute it from, so a gate opened
            // on this response and a transfer made against an unchanged snapshot agree by construction.
            DisclosureVersion = FileAnalysisDisclosureVersion.Compute(settings),
        });
    }
}
