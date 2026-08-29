using Odyssey.Dtos.Finance;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the file endpoints — upload, attach to an account/transaction, fetch
/// content, update metadata. Centralizes the multipart construction and content-stream
/// reading every file-touching page used to hand-roll. Upload/attach <b>throw</b> on
/// failure (callers run per-file loops that count or report failures); the read methods
/// return <c>null</c> on failure and leave user messaging to the caller.
/// </summary>
public interface IFilesApiClient
{
    /// <summary>Uploads the raw file (multipart) and returns its stored metadata. Throws on failure.</summary>
    Task<FileUploadResponse> UploadAsync(ApiUpload file, string? fileName = null, CancellationToken ct = default);

    /// <summary>Attaches an already-uploaded file to an account with the given document type and optional document-validity metadata. Throws on failure.</summary>
    Task AttachToAccountAsync(Guid accountId, Guid fileId, AccountFileType type,
        DateTime? validFrom = null, DateTime? validTo = null, DateTime? issuedAt = null, Guid? issuedBy = null,
        CancellationToken ct = default);

    /// <summary>Attaches an already-uploaded file to a transaction with the given document type. Throws on failure.</summary>
    Task AttachToTransactionAsync(Guid transactionId, Guid fileId, TransactionFileType type, CancellationToken ct = default);

    /// <summary>Fetches a file's stored metadata (name/type/size/…). Returns <c>null</c> on any failure
    /// (missing, or the caller lacks <c>files.read</c>) — callers treat that as "Unavailable".</summary>
    Task<FileMetadataResponse?> GetMetadataAsync(Guid fileId, CancellationToken ct = default);

    /// <summary>The absolute URL of a file's content endpoint, resolved against the configured API base
    /// (the same way the typed clients form request URLs) so it works both behind the nginx <c>/api/</c>
    /// proxy and against an absolute API host. Suitable as an <c>&lt;img src&gt;</c>; the auth cookie rides
    /// along same-origin. Requires <c>files.read</c>.</summary>
    string ContentUrl(Guid fileId);

    /// <summary>Fetches a file's bytes + content type. Returns <c>null</c> on failure.</summary>
    Task<ApiFile?> GetContentAsync(Guid fileId, CancellationToken ct = default);

    /// <summary>Updates a file's name/description and returns the stored metadata. Returns <c>null</c> on failure.</summary>
    Task<FileMetadataResponse?> UpdateMetadataAsync(Guid fileId, string? description, string fileName, CancellationToken ct = default);

    /// <summary>One page of the flat file list (the Files page), with search, kind filter and sort.</summary>
    Task<ApiResult<PagedResult<FileListItem>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? kinds = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default);

    /// <summary>Every file in one window.</summary>
    Task<ApiResult<List<FileListItem>>> ListAllAsync(CancellationToken ct = default);

    /// <summary>Deletes a file outright. Attachments to accounts/transactions are detached separately.</summary>
    Task<ApiResult> DeleteAsync(Guid fileId, CancellationToken ct = default);
}

/// <inheritdoc cref="IFilesApiClient" />
public sealed class FilesApiClient(IOdysseyApi api) : IFilesApiClient
{
    /// <summary>The 64 MB cap each upload page used to declare locally.</summary>
    public const long DefaultMaxFileSizeBytes = 64L * 1024 * 1024;

    private const string Base = "api/files";

    public async Task<FileUploadResponse> UploadAsync(ApiUpload file, string? fileName = null, CancellationToken ct = default)
    {
        var upload = string.IsNullOrWhiteSpace(fileName) ? file : file with { FileName = fileName };
        var result = await api.UploadAsync<FileUploadResponse>("api/files", upload, ct: ct);
        if (!result.IsSuccess)
            throw new Exception($"Upload failed for {upload.FileName}: {result.Error}");

        return result.Value ?? throw new Exception($"Empty response when uploading {upload.FileName}.");
    }

    public async Task AttachToAccountAsync(Guid accountId, Guid fileId, AccountFileType type,
        DateTime? validFrom = null, DateTime? validTo = null, DateTime? issuedAt = null, Guid? issuedBy = null,
        CancellationToken ct = default)
    {
        var result = await api.SendAsync(HttpMethod.Post, $"api/accounts/{accountId}/files",
            new AttachAccountFileRequest(fileId, type, validFrom, validTo, issuedAt, issuedBy), ct);
        if (!result.IsSuccess)
            throw new Exception($"Failed to attach file: {result.Error}");
    }

    public async Task AttachToTransactionAsync(Guid transactionId, Guid fileId, TransactionFileType type, CancellationToken ct = default)
    {
        var result = await api.SendAsync(HttpMethod.Post, $"api/transactions/{transactionId}/files",
            new AttachTransactionFileRequest(fileId, type), ct);
        if (!result.IsSuccess)
            throw new Exception($"Failed to attach file: {result.Error}");
    }

    public async Task<FileMetadataResponse?> GetMetadataAsync(Guid fileId, CancellationToken ct = default) =>
        (await api.GetAsync<FileMetadataResponse>($"api/files/{fileId}", ct)).Value;

    public string ContentUrl(Guid fileId) =>
        new Uri(api.BaseAddress!, $"api/files/{fileId}/content").ToString();

    public async Task<ApiFile?> GetContentAsync(Guid fileId, CancellationToken ct = default) =>
        (await api.GetFileAsync($"api/files/{fileId}/content", fileId.ToString(), ct: ct)).Value;

    public async Task<FileMetadataResponse?> UpdateMetadataAsync(Guid fileId, string? description, string fileName, CancellationToken ct = default) =>
        (await api.SendAsync<FileMetadataResponse>(HttpMethod.Put, $"api/files/{fileId}/metadata",
            new UpdateFileMetadataRequest(description, fileName), ct)).Value;

    public Task<ApiResult<PagedResult<FileListItem>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? kinds = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default) =>
        api.GetPagedAsync<FileListItem>(
            PagedQuery.For(Base)
                .Window(page, pageSize)
                .Add("search", search)
                .AddMany("kind", kinds)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public Task<ApiResult<List<FileListItem>>> ListAllAsync(CancellationToken ct = default) =>
        api.GetAllAsync<FileListItem>(PagedQuery.For(Base).Build(), ct);

    public Task<ApiResult> DeleteAsync(Guid fileId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{fileId}", null, ct);
}
