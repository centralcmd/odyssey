using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Odyssey.Api.ExchangeRateSync;
using Odyssey.Api.SystemSettings;
using Odyssey.Core.Jobs;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The background-jobs page (issue #409 §5): every job's health, the shared run log and its detail, and a manual run
/// of any job. Admin-only through existing claims — reads on <c>system-settings.read</c>, the manual run on
/// <c>system-settings.update</c> plus the job's own domain claims (S1). No new claim. A field produced by a job's own
/// domain is <c>null</c> without that job's detail-read claim (A1/S5).
/// </summary>
[ApiController]
[Route("api/background-jobs")]
[Authorize(Policy = PermissionClaims.SystemSettingsRead)]
public sealed class BackgroundJobsController(
    BackgroundJobsService jobs,
    IExchangeRateSyncDispatcher exchangeRateSyncDispatcher,
    IOptions<JsonOptions> json,
    ILogger<SystemSettingsService> audit) : ControllerBase
{
    [HttpGet("overview")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(BackgroundJobsOverview))]
    [SwaggerOperation(Summary = "Every background job's health, and the run log's totals.")]
    public async Task<IActionResult> GetOverview(CancellationToken cancellationToken = default) =>
        Ok(await jobs.GetOverviewAsync(Access(), cancellationToken));

    [HttpGet("runs")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingJobRun>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "One page of every job's runs, filtered by job, status, trigger and start date.")]
    public async Task<IActionResult> ListRuns([FromQuery] JobRunsQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await jobs.ListRunsAsync(query, Access(), cancellationToken));

    [HttpGet("runs/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(JobRunDetail))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "One run, with its job's own counters.")]
    public async Task<IActionResult> GetRun([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        await jobs.GetRunAsync(id, Access(), cancellationToken) is { } run
            // Serialised as the BASE type, so JobRunDetailJsonConverter writes the $kind discriminator: MVC's formatter
            // would pick the runtime subtype and bypass the converter.
            ? Content(JsonSerializer.Serialize<JobRunDetail>(run, json.Value.JsonSerializerOptions), "application/json")
            : this.NotFoundProblem($"Run {id} was not found.");

    /// <remarks>
    /// <c>{jobType}</c> is bound as a string and resolved through the catalogue, so an unknown value is a <c>404</c>
    /// (A14). The action policy is <c>system-settings.update</c>; the job's further claims are checked here, against
    /// the catalogue, and the claim list never leaves the server (S10).
    /// </remarks>
    [HttpPost("{jobType}/runs")]
    [Authorize(Policy = JobCatalogue.ManualRunBaseClaim)]
    [EnableRateLimiting(BackgroundJobsRateLimiting.RunPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted, Type = typeof(JobRunStarted))]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingJobRun))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Run a background job now.")]
    public async Task<IActionResult> PostRun([FromRoute] string jobType, CancellationToken cancellationToken = default)
    {
        if (!JobCatalogue.TryResolve(jobType, out var entry))
        {
            return this.NotFoundProblem("There is no such background job.");
        }

        // Before any claim check or insert (issue #444): a job that only runs consented work has no manual run.
        if (!entry.ManualRunSupported)
        {
            return this.ConflictProblem("This job has no manual run.",
                new Dictionary<string, object?> { ["code"] = JobRunConflictCodes.ManualRunUnsupported });
        }

        if (!CanRun(entry))
        {
            return this.ForbiddenProblem("You do not have permission to run this job.");
        }

        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var canReadDetail = CanReadDetail(entry);
        var outcome = await jobs.StartManualAsync(entry, userId, canReadDetail, cancellationToken);
        switch (outcome)
        {
            case ManualRunOutcome.Accepted accepted:
                Audit(userId, entry.JobType, accepted.RunId);
                if (entry.JobType == JobType.ExchangeRateSync)
                {
                    // Not the request's token: the run must not die with the HTTP connection. Under Testing this
                    // executes inline; in production the unclaimed row is the hand-off and this does nothing.
                    await exchangeRateSyncDispatcher.DispatchAsync(accepted.RunId, CancellationToken.None);
                }

                return Accepted(new JobRunStarted { Id = accepted.RunId });

            case ManualRunOutcome.Skipped skipped:
                Audit(userId, entry.JobType, skipped.Run.Id);
                return Ok(skipped.Run);

            case ManualRunOutcome.Cooldown cooldown:
                var seconds = Math.Max(1, (int)Math.Ceiling((cooldown.AvailableAtUtc - DateTime.UtcNow).TotalSeconds));
                Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return this.TooManyRequestsProblem(
                    "A manual run of this job was started less than a minute ago.",
                    new Dictionary<string, object?> { ["retryAfterUtc"] = cooldown.AvailableAtUtc });

            case ManualRunOutcome.Held held:
                var extensions = new Dictionary<string, object?> { ["code"] = JobRunConflictCodes.Held };
                // The held fields follow the job's detail-read claim (S-N6): omitted, not null, without it.
                if (canReadDetail)
                {
                    extensions["heldReason"] = held.Reason;
                    extensions["resumesAtUtc"] = held.ResumesAtUtc;
                }

                return this.ConflictProblem("The job is held, so it cannot run now.", extensions);

            default:
                return this.ConflictProblem(
                    "A run of this job is already in progress.",
                    new Dictionary<string, object?> { ["code"] = JobRunConflictCodes.AlreadyRunning });
        }
    }

    /// <summary>
    /// The accepted manual run's audit line (S7, ISO 27001 A.8.15): unconditional, at Information, through the logger
    /// the settings writes use, and independent of the row — which is pruned, and whose user id is nulled when the
    /// account is deleted. Ids and the job type only.
    /// </summary>
    private void Audit(string userId, JobType jobType, Guid runId) =>
        audit.LogInformation(
            "Background job manual run by {ActorUserId}: {JobType} run {RunId}.", userId, jobType, runId);

    private BackgroundJobAccess Access() => new(
        JobCatalogue.Entries.Where(CanReadDetail).Select(entry => entry.JobType).ToHashSet(),
        JobCatalogue.Entries.Where(entry => entry.ManualRunSupported && CanRun(entry)).Select(entry => entry.JobType).ToHashSet());

    private bool CanReadDetail(JobCatalogueEntry entry) =>
        entry.DetailReadClaim is not { } claim || User.HasPermission(claim);

    private bool CanRun(JobCatalogueEntry entry) =>
        User.HasPermission(JobCatalogue.ManualRunBaseClaim) && entry.ManualRunClaims.All(User.HasPermission);
}
