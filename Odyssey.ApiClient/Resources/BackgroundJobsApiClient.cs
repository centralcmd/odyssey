using System.Globalization;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>The run list's filters (issue #409 §5.2). Each enum filter holds at most one of each value.</summary>
public sealed record JobRunsFilter
{
    public IReadOnlyCollection<JobType>? JobTypes { get; init; }

    public IReadOnlyCollection<JobRunStatus>? Statuses { get; init; }

    public IReadOnlyCollection<JobRunTrigger>? Triggers { get; init; }

    /// <summary>A calendar day in <see cref="TimeZone"/>, inclusive.</summary>
    public DateOnly? StartedFrom { get; init; }

    /// <summary>A calendar day in <see cref="TimeZone"/>, inclusive.</summary>
    public DateOnly? StartedTo { get; init; }

    /// <summary>The caller's IANA zone; required by the server when a date is sent, ignored otherwise.</summary>
    public string? TimeZone { get; init; }

    public JobRunSortBy? SortBy { get; init; }

    public bool Ascending { get; init; }
}

/// <summary>
/// Typed client for the background-jobs endpoints (issue #409 §5): the overview, the shared run log, a run's detail
/// and a manual run. Every enum on these DTOs reads an unknown name as its <c>Unknown</c> member, and an unknown
/// <c>$kind</c> as the base <see cref="JobRunDetail"/>, so a newer server never breaks this client.
/// </summary>
public interface IBackgroundJobsApiClient
{
    Task<ApiResult<BackgroundJobsOverview>> GetOverviewAsync(CancellationToken ct = default);

    /// <summary>One page of the run log; <paramref name="pageSize"/> is at most 100.</summary>
    Task<ApiResult<PagedResult<ExistingJobRun>>> ListRunsAsync(
        int page, int pageSize, JobRunsFilter? filter = null, CancellationToken ct = default);

    /// <summary>One run, as its job's <see cref="JobRunDetail"/> subtype; <c>404</c> for an unknown id.</summary>
    Task<ApiResult<JobRunDetail>> GetRunAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Run a job now. <c>202</c>: started, <see cref="JobRunStarted.Id"/> names the run. <c>200</c>: nothing to do, the
    /// run with that id was finished <c>Skipped</c> at once. <c>403</c> without the job's claims; <c>409</c> with a
    /// <c>code</c> extension (<see cref="JobRunConflictCodes"/>); <c>429</c> with <c>retryAfterUtc</c>.
    /// </summary>
    Task<ApiResult<JobRunStarted>> RunNowAsync(JobType jobType, CancellationToken ct = default);
}

/// <inheritdoc cref="IBackgroundJobsApiClient" />
public sealed class BackgroundJobsApiClient(IOdysseyApi api) : IBackgroundJobsApiClient
{
    private const string Base = "api/background-jobs";

    public Task<ApiResult<BackgroundJobsOverview>> GetOverviewAsync(CancellationToken ct = default) =>
        api.GetAsync<BackgroundJobsOverview>($"{Base}/overview", ct);

    public Task<ApiResult<PagedResult<ExistingJobRun>>> ListRunsAsync(
        int page, int pageSize, JobRunsFilter? filter = null, CancellationToken ct = default)
    {
        var query = PagedQuery.For($"{Base}/runs").Window(page, pageSize);
        if (filter is not null)
        {
            query
                .AddMany("jobTypes", filter.JobTypes?.Select(value => value.ToString()))
                .AddMany("statuses", filter.Statuses?.Select(value => value.ToString()))
                .AddMany("triggers", filter.Triggers?.Select(value => value.ToString()))
                .Add("startedFrom", filter.StartedFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Add("startedTo", filter.StartedTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Add("tz", filter.StartedFrom is null && filter.StartedTo is null ? null : filter.TimeZone)
                .Sort(filter.SortBy?.ToString(), filter.Ascending);
        }

        return api.GetPagedAsync<ExistingJobRun>(query.Build(), ct);
    }

    public Task<ApiResult<JobRunDetail>> GetRunAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<JobRunDetail>($"{Base}/runs/{id}", ct);

    public Task<ApiResult<JobRunStarted>> RunNowAsync(JobType jobType, CancellationToken ct = default) =>
        api.SendAsync<JobRunStarted>(HttpMethod.Post, $"{Base}/{Uri.EscapeDataString(jobType.ToString())}/runs", null, ct);
}
