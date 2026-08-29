using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Dtos.Application;
using Odyssey.Core.Journal;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The effective import/export volume caps (issue #343 §7 item 3), for any authenticated caller — no
/// permission claim, so the four import dialogs can pre-validate against the real limit regardless of
/// which claims the signed-in user holds. Deliberately narrower than <c>SystemSettingsController</c>'s
/// admin-gated <c>SystemSettingsDto</c> (§10 item 2): sixteen plain integers, no audit metadata, no
/// administrator identity.
/// </summary>
[ApiController]
[Route("api/import-limits")]
[Authorize]
public sealed class ImportLimitsController : ControllerBase
{
    private readonly IImportExportLimitsLookup lookup;

    public ImportLimitsController(IImportExportLimitsLookup lookup)
    {
        this.lookup = lookup;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ImportLimitsDto))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The effective import/export volume caps for the four bulk import/export surfaces.")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var limits = await lookup.GetAsync(cancellationToken);

        // A degraded read must never be presented as configuration (issue #343 arch N1, AC 27e) — the
        // import/export SERVICES still use the (monotonically) degraded numbers to keep enforcing a
        // cap, but this read-only display surface fails closed instead of showing an invented value.
        if (limits.IsDegraded)
        {
            return this.ServiceUnavailableProblem(
                "Import/export limits are temporarily unavailable while the server recovers a configuration problem.");
        }

        return Ok(new ImportLimitsDto
        {
            ContactVCardMaxExportRows = limits.ContactVCardMaxExportRows,
            ContactVCardMaxImportEntries = limits.ContactVCardMaxImportEntries,
            ContactVCardMaxImportMegabytes = (int)(limits.ContactVCardMaxImportBytes / (1024 * 1024)),
            ContactVCardMaxExportMegabytes = (int)(limits.ContactVCardMaxExportBytes / (1024 * 1024)),
            CalendarIcsMaxExportEvents = limits.CalendarIcsMaxExportEvents,
            CalendarIcsMaxImportEvents = limits.CalendarIcsMaxImportEvents,
            CalendarIcsMaxImportMegabytes = (int)(limits.CalendarIcsMaxImportBytes / (1024 * 1024)),
            CalendarIcsMaxExportMegabytes = (int)(limits.CalendarIcsMaxExportBytes / (1024 * 1024)),
            TaskIcsMaxExportTasks = limits.TaskIcsMaxExportTasks,
            TaskIcsMaxImportTasks = limits.TaskIcsMaxImportTasks,
            TaskIcsMaxImportMegabytes = (int)(limits.TaskIcsMaxImportBytes / (1024 * 1024)),
            TaskIcsMaxExportMegabytes = (int)(limits.TaskIcsMaxExportBytes / (1024 * 1024)),
            JournalIcsMaxExportRows = limits.JournalIcsMaxExportRows,
            JournalIcsMaxImportEntries = limits.JournalIcsMaxImportEntries,
            JournalIcsMaxImportMegabytes = (int)(limits.JournalIcsMaxImportBytes / (1024 * 1024)),
            JournalIcsMaxExportMegabytes = (int)(limits.JournalIcsMaxExportBytes / (1024 * 1024)),
        });
    }
}
