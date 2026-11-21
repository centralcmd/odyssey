using Odyssey.Dtos;
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
    /// date-range / attachment-presence / archival filters and sort (issue #277).
    /// <paramref name="from"/> / <paramref name="to"/> bound the ENTRY date and are inclusive on both
    /// ends. <paramref name="hasPhotos"/> / <paramref name="hasFiles"/> are AND-ed, so passing both
    /// wants entries carrying both; <c>null</c> does not filter.</summary>
    Task<ApiResult<List<JournalEntrySummary>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? contactIds = null,
        string? status = null,
        string? sortBy = null,
        string? sortDir = null,
        DateTime? from = null,
        DateTime? to = null,
        bool? hasPhotos = null,
        bool? hasFiles = null,
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
        DateTime? from = null,
        DateTime? to = null,
        bool? hasPhotos = null,
        bool? hasFiles = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<JournalEntrySummary>(
            PagedQuery.For(Base)
                .Add("search", search)
                .AddMany("tagIds", tagIds)
                .AddMany("contactIds", contactIds)
                .Add("status", status)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Add("from", from)
                .Add("to", to)
                .AddBool("hasPhotos", hasPhotos)
                .AddBool("hasFiles", hasFiles)
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

    /// <summary>
    /// Loads one task keeping the outcome: a caller that must tell "the task is gone" (404) from "not
    /// allowed" or "unreachable" reads the status here, which <see cref="GetAsync"/> collapses to null.
    /// </summary>
    Task<ApiResult<ExistingJournalTask>> GetResultAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// What the VTODO <c>.ics</c> export at <see cref="ExportIcsUrl"/> would contain, refused for the same reasons (a cap, or attached files without
    /// <c>files.read</c>) — asked before a browser-native download, which cannot report an error itself.
    /// </summary>
    Task<ApiResult<TaskIcsExportSummary>> SummarizeIcsExportAsync(
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? statuses = null,
        IReadOnlyCollection<string>? ids = null,
        CancellationToken ct = default);

    /// <summary>
    /// The absolute URL of the export, for a download the browser streams to disk itself — an export carrying
    /// attached files can be far larger than a client can hold in memory.
    /// </summary>
    string ExportIcsUrl(
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? statuses = null,
        IReadOnlyCollection<string>? ids = null);

    /// <summary>The absolute URL <see cref="ImportIcsAsync"/> posts to, for an upload the browser streams from disk.</summary>
    string ImportIcsUrl { get; }

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
    public const long MaxImportBytes = SystemSettingsBounds.TaskIcsMaxImportMegabytesMax * 1024L * 1024;

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
        (await GetResultAsync(id, ct)).Value;

    public Task<ApiResult<ExistingJournalTask>> GetResultAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<ExistingJournalTask>($"{Base}/{id}", ct);

    public Task<ApiResult<TaskIcsExportSummary>> SummarizeIcsExportAsync(
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? statuses = null,
        IReadOnlyCollection<string>? ids = null,
        CancellationToken ct = default) =>
        api.GetAsync<TaskIcsExportSummary>(ExportQuery("ics/summary", search, tagIds, statuses, ids), ct);

    public string ExportIcsUrl(
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? statuses = null,
        IReadOnlyCollection<string>? ids = null) =>
        new Uri(api.BaseAddress!, ExportQuery("ics", search, tagIds, statuses, ids)).ToString();

    public string ImportIcsUrl => new Uri(api.BaseAddress!, $"{Base}/ics").ToString();

    private static string ExportQuery(
        string path, string? search, IReadOnlyCollection<string>? tagIds, IReadOnlyCollection<string>? statuses,
        IReadOnlyCollection<string>? ids) =>
        PagedQuery.For($"{Base}/{path}")
            .Add("search", search)
            .AddMany("tagIds", tagIds)
            .AddMany("statuses", statuses)
            .AddMany("ids", ids)
            .Build();

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
/// <see cref="ITaskApiClient"/>'s ICS surface. An export carries the entries' files and photos inline, so the
/// browser page summarizes and then downloads through <see cref="ExportUrl"/>. The typed import remains for
/// non-browser consumers and for the page's small-file fallback.
/// </summary>
public interface IJournalIcsApiClient
{
    /// <summary>
    /// Runs the export's checks for the same filters <see cref="ExportUrl"/> takes — the row and byte caps, and
    /// <c>files.read</c>/<c>photos.read</c> for inline attachments — and reports the entries, attached files and photos
    /// it would carry, without producing it. An export carries its files inline and can far exceed the WASM heap, so a
    /// page summarizes first and then lets the browser download <see cref="ExportUrl"/> itself.
    /// </summary>
    Task<ApiResult<JournalEntryIcsExportSummary>> SummarizeExportAsync(
        string? search = null, IReadOnlyCollection<string>? tagIds = null, string? status = null,
        IReadOnlyCollection<string>? contactIds = null, DateTime? from = null, DateTime? to = null,
        bool? hasPhotos = null, bool? hasFiles = null,
        CancellationToken ct = default);

    /// <summary>The absolute export URL for the same filters, for a browser-native download.</summary>
    string ExportUrl(
        string? search = null, IReadOnlyCollection<string>? tagIds = null, string? status = null,
        IReadOnlyCollection<string>? contactIds = null, DateTime? from = null, DateTime? to = null,
        bool? hasPhotos = null, bool? hasFiles = null);

    /// <summary>The single-entry counterpart of <see cref="SummarizeExportAsync"/>; a <c>404</c> for an unknown id.</summary>
    Task<ApiResult<JournalEntryIcsExportSummary>> SummarizeExportOneAsync(Guid id, CancellationToken ct = default);

    /// <summary>The absolute single-entry export URL, for a browser-native download.</summary>
    string ExportOneUrl(Guid id);

    /// <summary>The absolute import URL, for a browser-native multipart upload of a large file.</summary>
    string ImportUrl { get; }

    /// <summary>Imports a VJOURNAL <c>.ics</c> file (multipart), creating/updating by UID.</summary>
    Task<ApiResult<JournalEntryIcsImportResult>> ImportAsync(ApiUpload file, CancellationToken ct = default);
}

/// <inheritdoc cref="IJournalIcsApiClient" />
public sealed class JournalIcsApiClient(IOdysseyApi api) : IJournalIcsApiClient
{
    private const string Base = "api/journal-entries";

    /// <summary>
    /// The shipped default (and ceiling) for <c>JournalIcsMaxImportMegabytes</c> — the real, effective
    /// cap is dynamic (System Settings, issue #343) and read via <c>IImportLimitsApiClient</c>; this
    /// constant exists solely as <c>ImportLimitsCache.Fallback</c>'s input for when that read fails.
    /// </summary>
    public const long MaxImportBytes = SystemSettingsBounds.JournalIcsMaxImportMegabytesMax * 1024L * 1024;

    public Task<ApiResult<JournalEntryIcsExportSummary>> SummarizeExportAsync(
        string? search = null, IReadOnlyCollection<string>? tagIds = null, string? status = null,
        IReadOnlyCollection<string>? contactIds = null, DateTime? from = null, DateTime? to = null,
        bool? hasPhotos = null, bool? hasFiles = null,
        CancellationToken ct = default) =>
        api.GetAsync<JournalEntryIcsExportSummary>(
            ExportQuery("vjournal/summary", search, tagIds, status, contactIds, from, to, hasPhotos, hasFiles), ct);

    public string ExportUrl(
        string? search = null, IReadOnlyCollection<string>? tagIds = null, string? status = null,
        IReadOnlyCollection<string>? contactIds = null, DateTime? from = null, DateTime? to = null,
        bool? hasPhotos = null, bool? hasFiles = null) =>
        Absolute(ExportQuery("vjournal", search, tagIds, status, contactIds, from, to, hasPhotos, hasFiles));

    public Task<ApiResult<JournalEntryIcsExportSummary>> SummarizeExportOneAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<JournalEntryIcsExportSummary>($"{Base}/{id}/vjournal/summary", ct);

    public string ExportOneUrl(Guid id) => Absolute($"{Base}/{id}/vjournal");

    public string ImportUrl => Absolute($"{Base}/vjournal");

    private string Absolute(string relative) => new Uri(api.BaseAddress!, relative).ToString();

    // The export endpoints bind the list's query model, so the export and its summary take the list's filter names.
    private static string ExportQuery(
        string path, string? search, IReadOnlyCollection<string>? tagIds, string? status,
        IReadOnlyCollection<string>? contactIds, DateTime? from, DateTime? to, bool? hasPhotos, bool? hasFiles) =>
        PagedQuery.For($"{Base}/{path}")
            .Add("search", search)
            .AddMany("tagIds", tagIds)
            .Add("status", status)
            .AddMany("contactIds", contactIds)
            .Add("from", from)
            .Add("to", to)
            .AddBool("hasPhotos", hasPhotos)
            .AddBool("hasFiles", hasFiles)
            .Build();

    public Task<ApiResult<JournalEntryIcsImportResult>> ImportAsync(ApiUpload file, CancellationToken ct = default) =>
        api.UploadAsync<JournalEntryIcsImportResult>(
            $"{Base}/vjournal",
            string.IsNullOrWhiteSpace(file.FileName) ? file with { FileName = "entries.ics" } : file,
            contentTypeOverride: "text/calendar",
            ct: ct);
}
