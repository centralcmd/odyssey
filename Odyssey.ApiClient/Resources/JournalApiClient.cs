using Odyssey.Dtos.Journal;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the journal-entry endpoints (issue #311). Writes go through
/// <see cref="IOdysseyApi"/> at the call sites, as the sibling Finance surfaces do. Cross-claim links
/// (contacts, files) come back as ids only — the page hydrates names via the respective claim-gated
/// endpoints (spec §9/§10).
/// </summary>
public interface IJournalApiClient
{
    /// <summary>Lists journal entries (lean summary projection) with server-side search, tag / contact /
    /// date-range / archival filters and sort (issue #277).</summary>
    Task<ApiResult<List<JournalEntrySummary>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? contactIds = null,
        string? status = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>Loads one entry with its full content + link id sets. Null on failure.</summary>
    Task<ExistingJournalEntry?> GetAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewJournalEntry entry, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, UpdateJournalEntry entry, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="IJournalApiClient" />
public sealed class JournalApiClient(IOdysseyApi api) : IJournalApiClient
{
    private const string Base = "api/journal-entries";

    public Task<ApiResult<List<JournalEntrySummary>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? contactIds = null,
        string? status = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<JournalEntrySummary>(
            PagedQuery.For(Base)
                .Add("search", search)
                .AddMany("tagIds", tagIds)
                .AddMany("contactIds", contactIds)
                .Add("status", status)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public async Task<ExistingJournalEntry?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingJournalEntry>($"{Base}/{id}", ct)).Value;

    public Task<ApiResult> CreateAsync(NewJournalEntry entry, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, entry, ct);

    public Task<ApiResult> UpdateAsync(Guid id, UpdateJournalEntry entry, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", entry, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);
}

/// <summary>
/// Typed client for the tasks endpoints (issue #311). The kanban status is exposed by the API as a
/// derived <see cref="JournalTaskStatus"/> (from StartedAt/CompletedAt/Archived timestamps); status
/// changes and board reordering are performed through the single PUT update (Status / Position), so
/// there is no dedicated status/position endpoint.
/// </summary>
public interface ITaskApiClient
{
    /// <summary>Lists tasks (lean summary projection) with server-side search, tag + derived-status filters
    /// and sort (incl. Position).</summary>
    Task<ApiResult<List<JournalTaskSummary>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>Loads one task with its full content + link id sets. Null on failure.</summary>
    Task<ExistingJournalTask?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Exports the tasks matching the given filters as a VTODO <c>.ics</c> file (issue #337).
    /// A non-success status yields a failure result rather than a body that would download as a fake
    /// <c>.ics</c>.</summary>
    Task<ApiResult<ApiFile>> ExportIcsAsync(
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? statuses = null,
        IReadOnlyCollection<string>? ids = null,
        CancellationToken ct = default);

    /// <summary>Imports a VTODO <c>.ics</c> file into the shared board (multipart).</summary>
    Task<ApiResult<TaskIcsImportResult>> ImportIcsAsync(ApiUpload file, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewJournalTask task, CancellationToken ct = default);

    /// <summary>
    /// Updates a task. Kanban status changes and board reordering also go through here (Status /
    /// Position on the update DTO) — there is no dedicated status or position endpoint.
    /// </summary>
    Task<ApiResult> UpdateAsync(Guid id, UpdateJournalTask task, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="ITaskApiClient" />
public sealed class TaskApiClient(IOdysseyApi api) : ITaskApiClient
{
    private const string Base = "api/tasks";

    /// <summary>
    /// The migration-seeded default for <c>TaskIcsMaxImportMegabytes</c> — the real, effective cap
    /// is dynamic (System Settings, issue #343) and read via <c>IImportLimitsApiClient</c>; this
    /// constant exists solely as <c>ImportLimitsCache.Fallback</c>'s input for when that read fails.
    /// </summary>
    public const long MaxImportBytes = 64L * 1024 * 1024;

    public Task<ApiResult<List<JournalTaskSummary>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<JournalTaskSummary>(
            PagedQuery.For(Base)
                .Add("search", search)
                .AddMany("tagIds", tagIds)
                .AddMany("statuses", statuses)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public async Task<ExistingJournalTask?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingJournalTask>($"{Base}/{id}", ct)).Value;

    public Task<ApiResult<ApiFile>> ExportIcsAsync(
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? statuses = null,
        IReadOnlyCollection<string>? ids = null,
        CancellationToken ct = default)
    {
        // The export endpoint binds the list filter surface minus paging (it's never paginated), so build
        // just search/tag/status (+ optional ids for the single-task row action) — same param names the
        // list query uses.
        var query = PagedQuery.For($"{Base}/ics")
            .Add("search", search)
            .AddMany("tagIds", tagIds)
            .AddMany("statuses", statuses)
            .AddMany("ids", ids)
            .Build();

        return api.GetFileAsync(query, "odyssey-tasks.ics", completenessMarker: "BEGIN:VTODO", ct: ct);
    }

    public Task<ApiResult<TaskIcsImportResult>> ImportIcsAsync(ApiUpload file, CancellationToken ct = default) =>
        api.UploadAsync<TaskIcsImportResult>(
            $"{Base}/ics",
            string.IsNullOrWhiteSpace(file.FileName) ? file with { FileName = "tasks.ics" } : file,
            contentTypeOverride: "text/calendar",
            ct: ct);

    public Task<ApiResult> CreateAsync(NewJournalTask task, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, task, ct);

    public Task<ApiResult> UpdateAsync(Guid id, UpdateJournalTask task, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", task, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);
}

/// <summary>
/// Typed client for the journal-entry VJOURNAL (.ics) export/import endpoints (issue #339). Sibling of
/// <see cref="ITaskApiClient"/>'s ICS surface: export returns raw bytes + a server-supplied filename for
/// a browser download; import posts the file as multipart and returns the parsed result.
/// </summary>
public interface IJournalIcsApiClient
{
    /// <summary>Exports the entries matching the given filters as a VJOURNAL <c>.ics</c> file. Omit all
    /// filters for "export all" (every status, including archived); pass search/tags/status to export the
    /// current filtered set.</summary>
    Task<ApiResult<ApiFile>> ExportAsync(
        string? search = null, IReadOnlyCollection<string>? tagIds = null, string? status = null,
        CancellationToken ct = default);

    /// <summary>Exports a single entry as a one-VJOURNAL <c>.ics</c> file.</summary>
    Task<ApiResult<ApiFile>> ExportOneAsync(Guid id, CancellationToken ct = default);

    /// <summary>Imports a VJOURNAL <c>.ics</c> file (multipart), creating/updating by UID.</summary>
    Task<ApiResult<JournalEntryIcsImportResult>> ImportAsync(ApiUpload file, CancellationToken ct = default);
}

/// <inheritdoc cref="IJournalIcsApiClient" />
public sealed class JournalIcsApiClient(IOdysseyApi api) : IJournalIcsApiClient
{
    private const string Base = "api/journal-entries";

    /// <summary>
    /// The migration-seeded default for <c>JournalIcsMaxImportMegabytes</c> — the real, effective
    /// cap is dynamic (System Settings, issue #343) and read via <c>IImportLimitsApiClient</c>; this
    /// constant exists solely as <c>ImportLimitsCache.Fallback</c>'s input for when that read fails.
    /// </summary>
    public const long MaxImportBytes = 64L * 1024 * 1024;

    public Task<ApiResult<ApiFile>> ExportAsync(
        string? search = null, IReadOnlyCollection<string>? tagIds = null, string? status = null,
        CancellationToken ct = default) =>
        api.GetFileAsync(
            PagedQuery.For($"{Base}/vjournal")
                .Add("search", search)
                .AddMany("tagIds", tagIds)
                .Add("status", status)
                .Build(),
            "odyssey-journal-entries.ics",
            completenessMarker: "BEGIN:VJOURNAL",
            ct: ct);

    public Task<ApiResult<ApiFile>> ExportOneAsync(Guid id, CancellationToken ct = default) =>
        api.GetFileAsync($"{Base}/{id}/vjournal", "odyssey-journal-entry.ics", ct: ct);

    public Task<ApiResult<JournalEntryIcsImportResult>> ImportAsync(ApiUpload file, CancellationToken ct = default) =>
        api.UploadAsync<JournalEntryIcsImportResult>(
            $"{Base}/vjournal",
            string.IsNullOrWhiteSpace(file.FileName) ? file with { FileName = "entries.ics" } : file,
            contentTypeOverride: "text/calendar",
            ct: ct);
}
