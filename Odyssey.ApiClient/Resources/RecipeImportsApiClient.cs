using System.Globalization;
using Odyssey.Dtos;
using Odyssey.Dtos.Recipes;

namespace Odyssey.ApiClient.Resources;

/// <summary>The list filters of <c>GET /api/recipe-imports</c> (issue #602 §5 item 4); <c>null</c> leaves a filter off.</summary>
public sealed record RecipeImportListFilter
{
    public IReadOnlyCollection<RecipeImportStatus>? Status { get; init; }

    /// <summary>Only imports with a draft recipe waiting for review.</summary>
    public bool? HasPendingDraft { get; init; }

    public bool IncludePurged { get; init; }

    public RecipeImportSortBy? SortBy { get; init; }

    public SortDirection? SortDir { get; init; }
}

/// <summary>A new import: the idempotency key, the text, Library file ids and new files, the consent and its version.</summary>
public sealed record RecipeImportSubmission(
    Guid ClientRequestId,
    string? Text,
    IReadOnlyList<Guid> ExistingFileIds,
    IReadOnlyList<ApiUpload> Uploads,
    string DisclosureVersion,
    bool ConsentAcknowledged);

/// <summary>
/// Typed client for AI recipe import (issue #602). A submission with new files goes multipart to <c>…/uploads</c>, one
/// without goes as JSON; both answer <c>202</c> for a new import and <c>200</c> for a repeated key. Results are returned,
/// never presented.
/// </summary>
public interface IRecipeImportsApiClient
{
    Task<ApiResult<RecipeImportOptions>> GetOptionsAsync(CancellationToken ct = default);

    Task<ApiResult<RecipeImportDetail>> CreateAsync(RecipeImportSubmission submission, CancellationToken ct = default);

    /// <summary>One window of the list; <paramref name="page"/> is 1-based.</summary>
    Task<ApiResult<PagedResult<RecipeImportSummary>>> ListAsync(
        RecipeImportListFilter? filter = null, int page = 1, int pageSize = 25, CancellationToken ct = default);

    Task<ApiResult<RecipeImportDetail>> GetAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<RecipeImportDraftItem>> UpdateDraftAsync(
        Guid id, Guid draftId, UpdateRecipeImportDraft request, CancellationToken ct = default);

    Task<ApiResult<AcceptRecipeImportDraftResult>> AcceptAsync(
        Guid id, Guid draftId, AcceptRecipeImportDraft request, CancellationToken ct = default);

    Task<ApiResult> DiscardAsync(Guid id, Guid draftId, CancellationToken ct = default);

    Task<ApiResult> CancelAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Library-area files of the accepted types, newest first — the composer's From Files list.</summary>
    Task<ApiResult<PagedResult<RecipeImportLibraryFile>>> ListLibraryFilesAsync(
        string? search = null, int offset = 0, int limit = 100, CancellationToken ct = default);
}

/// <inheritdoc cref="IRecipeImportsApiClient" />
public sealed class RecipeImportsApiClient(IOdysseyApi api) : IRecipeImportsApiClient
{
    private const string Base = "api/recipe-imports";

    public Task<ApiResult<RecipeImportOptions>> GetOptionsAsync(CancellationToken ct = default) =>
        api.GetAsync<RecipeImportOptions>($"{Base}/options", ct);

    public Task<ApiResult<RecipeImportDetail>> CreateAsync(RecipeImportSubmission submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Uploads.Count == 0)
        {
            return api.SendAsync<RecipeImportDetail>(HttpMethod.Post, Base, new CreateRecipeImport
            {
                ClientRequestId = submission.ClientRequestId,
                Text = submission.Text,
                ExistingFileIds = [.. submission.ExistingFileIds],
                ConsentAcknowledged = submission.ConsentAcknowledged,
                DisclosureVersion = submission.DisclosureVersion,
            }, ct);
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("clientRequestId", submission.ClientRequestId.ToString()),
            new("consentAcknowledged", submission.ConsentAcknowledged ? "true" : "false"),
            new("disclosureVersion", submission.DisclosureVersion),
        };
        if (!string.IsNullOrEmpty(submission.Text))
        {
            fields.Add(new("text", submission.Text));
        }

        fields.AddRange(submission.ExistingFileIds.Select(id => new KeyValuePair<string, string>("existingFileIds", id.ToString())));
        return api.UploadManyAsync<RecipeImportDetail>($"{Base}/uploads", submission.Uploads, "files", fields, ct);
    }

    public Task<ApiResult<PagedResult<RecipeImportSummary>>> ListAsync(
        RecipeImportListFilter? filter = null, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        filter ??= new RecipeImportListFilter();
        var query = PagedQuery.For(Base)
            .Window(page, pageSize)
            .AddMany("status", filter.Status?.Select(s => s.ToString()))
            .AddBool("hasPendingDraft", filter.HasPendingDraft)
            .AddBool("includePurged", filter.IncludePurged ? true : null)
            .Add("sortBy", filter.SortBy?.ToString())
            .Add("sortDir", filter.SortDir?.ToString())
            .Build();
        return api.GetPagedAsync<RecipeImportSummary>(query, ct);
    }

    public Task<ApiResult<RecipeImportDetail>> GetAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<RecipeImportDetail>($"{Base}/{id}", ct);

    public Task<ApiResult<RecipeImportDraftItem>> UpdateDraftAsync(
        Guid id, Guid draftId, UpdateRecipeImportDraft request, CancellationToken ct = default) =>
        api.SendAsync<RecipeImportDraftItem>(HttpMethod.Put, $"{Base}/{id}/drafts/{draftId}", request, ct);

    public Task<ApiResult<AcceptRecipeImportDraftResult>> AcceptAsync(
        Guid id, Guid draftId, AcceptRecipeImportDraft request, CancellationToken ct = default) =>
        api.SendAsync<AcceptRecipeImportDraftResult>(HttpMethod.Post, $"{Base}/{id}/drafts/{draftId}/accept", request, ct);

    public Task<ApiResult> DiscardAsync(Guid id, Guid draftId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Base}/{id}/drafts/{draftId}/discard", null, ct);

    public Task<ApiResult> CancelAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Base}/{id}/cancel", null, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);

    public Task<ApiResult<PagedResult<RecipeImportLibraryFile>>> ListLibraryFilesAsync(
        string? search = null, int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        var url = $"{Base}/library-files?offset={offset.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(search))
        {
            url += $"&search={Uri.EscapeDataString(search)}";
        }

        return api.GetPagedAsync<RecipeImportLibraryFile>(url, ct);
    }
}
