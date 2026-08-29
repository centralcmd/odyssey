using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Odyssey.Api.Identity;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Core.Finance;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/file-analysis")]
public class FileAnalysisController : ControllerBase
{
    private readonly ILogger<FileAnalysisController> logger;
    private readonly FileAnalysisService fileAnalysisService;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IUserDisplayNameResolver displayNames;

    public FileAnalysisController(
        ILogger<FileAnalysisController> logger,
        FileAnalysisService fileAnalysisService,
        UserManager<ApplicationUser> userManager,
        IUserDisplayNameResolver displayNames)
    {
        this.logger = logger;
        this.fileAnalysisService = fileAnalysisService;
        this.userManager = userManager;
        this.displayNames = displayNames;
    }

    [HttpGet("audit", Name = "GetFileAnalysisAuditLog")]
    [Authorize(Policy = PermissionClaims.FileAnalysisAudit)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<FileAnalysisAuditEntry>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List the external-AI file-analysis audit trail.",
        Description = "Admin-only accountability log of every statement sent to the AI provider for analysis — " +
                      "who, which file, when, the lawful basis, and the result. Newest first.")]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] FileAnalysisAuditQueryParams query,
        CancellationToken cancellationToken = default)
    {
        // The whole audit set is fetched, enriched with the initiating user's display (only resolvable
        // here, where UserManager is available), then searched/filtered/sorted/paged in memory (issue
        // #277) — the search matches the user name/email that the Finance domain can't see. This page
        // is low-volume, so an in-memory pass is fine.
        var all = await fileAnalysisService.GetAuditLogAsync(cancellationToken);

        // RequestedByUserId is nullable: the attribution FK nulls it out when the requesting account is
        // deleted (see OdysseyContext's user-attribution keys). Such a job keeps its audit row and loses
        // only the name, so the id is dropped from the lookups here and the entry falls through to the
        // resolver's own "Unknown user" label below.
        var userIds = all
            .Select(entry => entry.RequestedByUserId)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The *name* label goes through the shared claim-aware resolver (issue #316); the audit's
        // dedicated Email column is intentionally kept (this surface is file-analysis.audit-gated), so
        // the email is read directly and independently of the resolver's users.read gate.
        var names = await displayNames.ResolveAsync(User, userIds, cancellationToken);
        var emailById = await userManager.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email })
            .ToDictionaryAsync(u => u.Id, u => u.Email, StringComparer.Ordinal, cancellationToken);

        IEnumerable<FileAnalysisAuditEntry> entries = all.Select(e => e with
        {
            User = new FileAnalysisAuditUser(
                e.RequestedByUserId is { } requestedBy
                    ? names.GetValueOrDefault(requestedBy, UserDisplayNameResolver.UnknownUser)
                    : UserDisplayNameResolver.UnknownUser,
                e.RequestedByUserId is { } requestedByForEmail
                    ? emailById.GetValueOrDefault(requestedByForEmail)
                    : null),
        });

        if (query.Statuses is { Length: > 0 } statuses)
        {
            var wanted = statuses.ToHashSet();
            entries = entries.Where(e => wanted.Contains(StatusBucket(e.Status)));
        }

        var term = query.Search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            entries = entries.Where(e => new[]
            {
                e.File?.Name, e.User?.Name, e.User?.Email, e.Account?.Name, e.RequestId,
            }.Any(field => field is not null && field.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        var ascending = query.SortDir == SortDirection.Asc;
        entries = query.SortBy switch
        {
            FileAnalysisAuditSortBy.Status => ascending
                ? entries.OrderBy(e => (int)e.Status).ThenBy(e => e.Id)
                : entries.OrderByDescending(e => (int)e.Status).ThenBy(e => e.Id),
            _ => ascending
                ? entries.OrderBy(e => e.At ?? DateTime.MinValue).ThenBy(e => e.Id)
                : entries.OrderByDescending(e => e.At ?? DateTime.MinValue).ThenBy(e => e.Id),
        };

        return Ok(Odyssey.Core.Pagination.ListQuery.ToPagedResult(entries.ToList(), query.Offset, query.Limit));
    }

    // Collapse the raw job status into the three outcome buckets the audit filter exposes.
    private static FileAnalysisAuditStatus StatusBucket(Odyssey.Dtos.Finance.FileAnalysisJobStatus status) => status switch
    {
        Odyssey.Dtos.Finance.FileAnalysisJobStatus.Completed => FileAnalysisAuditStatus.Completed,
        Odyssey.Dtos.Finance.FileAnalysisJobStatus.Failed or Odyssey.Dtos.Finance.FileAnalysisJobStatus.Cancelled => FileAnalysisAuditStatus.Failed,
        _ => FileAnalysisAuditStatus.Running,
    };

    [HttpGet("{analysisJobId}", Name = "GetFileAnalysisJob")]
    [Authorize(Policy = PermissionClaims.FileAnalysisRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingFileAnalysisJob))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the status and candidate transactions for an analysis job.",
        Description = "Returns the job metadata and all extracted candidate transactions.")]
    public async Task<IActionResult> GetJob(
        [FromRoute(Name = "analysisJobId")] [SwaggerParameter("Analysis Job ID", Required = true)]
        Guid analysisJobId, CancellationToken cancellationToken = default)
    {
        var job = await fileAnalysisService.GetJobAsync(analysisJobId, cancellationToken);
        if (job is null)
            return this.NotFoundProblem($"Analysis job {analysisJobId} not found.");
        return Ok(job);
    }

    [HttpPost("{analysisJobId}/match", Name = "MatchAnalysisCandidates")]
    [Authorize(Policy = PermissionClaims.FileAnalysisCreate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingFileAnalysisJob))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Run (or re-run) the AI match step for an extraction-completed analysis job.",
        Description = "Sends the user's contact and tag names to the AI provider to resolve each candidate's " +
                      "merchant/category, persists the matches, and returns the updated job. Synchronous. A provider " +
                      "failure or over-cap is recorded on matchStatus and never blocks importing the candidates.")]
    public async Task<IActionResult> MatchCandidates(
        [FromRoute(Name = "analysisJobId")] [SwaggerParameter("Analysis Job ID", Required = true)]
        Guid analysisJobId, CancellationToken cancellationToken = default)
    {
        var job = await fileAnalysisService.MatchAsync(analysisJobId, cancellationToken);
        return Ok(job);
    }

    [HttpPost("{analysisJobId}/import", Name = "ImportAnalysisCandidates")]
    [Authorize(Policy = PermissionClaims.FileAnalysisImport)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ImportResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Import selected candidate transactions from an analysis job.",
        Description = "Commits the specified candidates as real transactions. Optional field overrides (date, description, amount, currency) are applied before import.")]
    public async Task<IActionResult> ImportCandidates(
        [FromRoute(Name = "analysisJobId")] [SwaggerParameter("Analysis Job ID", Required = true)]
        Guid analysisJobId,
        [FromBody] [SwaggerParameter("ImportRequest", Required = true)]
        ImportRequest request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("User ID not found in claims.");

        var result = await fileAnalysisService.ImportCandidatesAsync(analysisJobId, request, userId, cancellationToken);
        return Ok(result);
    }
}
