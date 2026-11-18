using System.Text.Json.Serialization;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages.JobRuns;

/// <summary>
/// What the Background jobs Search region shows and the run list is asked for (issue #410 §3.7). The three
/// multi-value filters hold enum names that passed the allow-list; the dates are local calendar days and are never
/// persisted.
/// </summary>
public sealed record JobRunFilters
{
    public const string DefaultSortKey = nameof(JobRunSortBy.StartedAt);

    public const int DefaultPageSize = JobRunsQueryParams.DefaultLimit;

    public static readonly OdsTableSort DefaultSort = new(DefaultSortKey, OdsSortDirection.Desc);

    public static readonly JobRunFilters Default = new();

    public IReadOnlyList<string> Jobs { get; init; } = [];

    public IReadOnlyList<string> Statuses { get; init; } = [];

    public IReadOnlyList<string> Triggers { get; init; } = [];

    public DateOnly? StartedFrom { get; init; }

    public DateOnly? StartedTo { get; init; }

    public OdsTableSort Sort { get; init; } = DefaultSort;

    public int PageSize { get; init; } = DefaultPageSize;

    public bool HasDates => StartedFrom is not null || StartedTo is not null;

    /// <summary>Anything differs from the defaults — what offers Reset filters.</summary>
    public bool IsDefault =>
        Jobs.Count == 0 && Statuses.Count == 0 && Triggers.Count == 0 && !HasDates
        && Sort == DefaultSort && PageSize == DefaultPageSize;

    /// <summary>A filter that narrows the rows (not sort or size): an empty page then means "no matches".</summary>
    public bool Narrows => Jobs.Count > 0 || Statuses.Count > 0 || Triggers.Count > 0 || HasDates;

    /// <summary>
    /// The request's filter. <paramref name="timeZone"/> is the browser's IANA zone, or <c>UTC</c> once the server
    /// rejected it; the client sends it only with a date (#410 S-N1 — <see cref="BackgroundJobsApiClient"/> drops it
    /// otherwise).
    /// </summary>
    public JobRunsFilter ToRequest(string timeZone) => new()
    {
        JobTypes = JobRunAllowList.Parse<JobType>(Jobs, BackgroundJobPresentation.Jobs),
        Statuses = JobRunAllowList.Parse<JobRunStatus>(Statuses, BackgroundJobPresentation.Statuses),
        Triggers = JobRunAllowList.Parse<JobRunTrigger>(Triggers, BackgroundJobPresentation.Triggers),
        StartedFrom = StartedFrom,
        StartedTo = StartedTo,
        TimeZone = HasDates ? timeZone : null,
        SortBy = Enum.TryParse<JobRunSortBy>(Sort.Key, ignoreCase: false, out var by) && Enum.IsDefined(by) ? by : JobRunSortBy.StartedAt,
        Ascending = Sort.Dir == OdsSortDirection.Asc,
    };

    /// <summary>What is persisted under <c>background-jobs-page</c>: never the dates, which go stale (F14).</summary>
    public BackgroundJobsPageState ToState() => new()
    {
        Jobs = [.. Jobs],
        Statuses = [.. Statuses],
        Triggers = [.. Triggers],
        SortKey = Sort.Key,
        SortDir = Sort.Dir == OdsSortDirection.Asc ? "asc" : "desc",
        PageSize = PageSize,
    };

    /// <summary>
    /// A stored state through the allow-list (FS3, S-N4): every name matched exactly, numeric strings and unknown
    /// names dropped, duplicates removed, an unknown sort or size reset.
    /// </summary>
    public static JobRunFilters FromState(BackgroundJobsPageState? state)
    {
        if (state is null)
        {
            return Default;
        }

        var sortKey = SortKeys.Contains(state.SortKey ?? string.Empty) ? state.SortKey! : DefaultSortKey;
        var dir = state.SortDir == "asc" ? OdsSortDirection.Asc : state.SortDir == "desc" ? OdsSortDirection.Desc : DefaultSort.Dir;
        return new JobRunFilters
        {
            Jobs = JobRunAllowList.Clean(state.Jobs, BackgroundJobPresentation.Jobs),
            Statuses = JobRunAllowList.Clean(state.Statuses, BackgroundJobPresentation.Statuses),
            Triggers = JobRunAllowList.Clean(state.Triggers, BackgroundJobPresentation.Triggers),
            Sort = SortKeys.Contains(state.SortKey ?? string.Empty) ? new OdsTableSort(sortKey, dir) : DefaultSort,
            PageSize = OdsPageSizes.Batch.Contains(state.PageSize) ? state.PageSize : DefaultPageSize,
        };
    }

    /// <summary>The sort keys the page offers — exactly the server's <see cref="JobRunSortBy"/> names.</summary>
    public static readonly IReadOnlySet<string> SortKeys = new HashSet<string>(Enum.GetNames<JobRunSortBy>(), StringComparer.Ordinal);

    /// <summary>The Sort select's fields, in the kit's order.</summary>
    public static readonly IReadOnlyList<OdsSortField<ExistingJobRun>> SortFields =
    [
        new() { Key = nameof(JobRunSortBy.StartedAt), Label = "Started", Type = OdsSortType.Date },
        new() { Key = nameof(JobRunSortBy.Duration), Label = "Duration", Type = OdsSortType.Number },
        new() { Key = nameof(JobRunSortBy.JobType), Label = "Job", Type = OdsSortType.Text },
        new() { Key = nameof(JobRunSortBy.Status), Label = "Status", Type = OdsSortType.Status },
    ];
}

/// <summary>The persisted shape of the page's filters (<see cref="JobRunFilters.ToState"/>).</summary>
public sealed record BackgroundJobsPageState
{
    public List<string>? Jobs { get; set; }

    public List<string>? Statuses { get; set; }

    public List<string>? Triggers { get; set; }

    public string? SortKey { get; set; }

    public string? SortDir { get; set; }

    public int PageSize { get; set; }

    /// <summary>Two states that persist the same choice — a rewrite is needed only when a stored one does not.</summary>
    [JsonIgnore]
    public string Fingerprint =>
        string.Join('|', string.Join(',', Jobs ?? []), string.Join(',', Statuses ?? []), string.Join(',', Triggers ?? []), SortKey, SortDir, PageSize);
}

/// <summary>
/// The exact-name allow-list every stored and linked filter value passes (issue #410 §3.7, FS3, S-N4): a value is kept
/// only when it is, character for character, the name of a member the page offers — so a numeric string such as
/// <c>"2"</c>, a differently cased name or <c>Unknown</c> is dropped silently — and each list is de-duplicated, so a
/// repeated link value never reaches the server's duplicate-value <c>400</c>.
/// </summary>
public static class JobRunAllowList
{
    public static IReadOnlyList<string> Clean<TEnum>(IEnumerable<string?>? values, IReadOnlyList<TEnum> allowed)
        where TEnum : struct, Enum
    {
        if (values is null)
        {
            return [];
        }

        var names = allowed.Select(v => v.ToString()).ToHashSet(StringComparer.Ordinal);
        return [.. values.Where(v => v is not null && names.Contains(v)).Select(v => v!).Distinct(StringComparer.Ordinal)];
    }

    public static IReadOnlyCollection<TEnum>? Parse<TEnum>(IEnumerable<string> values, IReadOnlyList<TEnum> allowed)
        where TEnum : struct, Enum
    {
        var parsed = Clean(values, allowed).Select(name => allowed.First(v => v.ToString() == name)).ToList();
        return parsed.Count == 0 ? null : parsed;
    }
}
