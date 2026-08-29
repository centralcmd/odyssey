using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the License / Terms of Service acceptance endpoints (issue #354 §7).
/// </summary>
/// <remarks>
/// Three tiers, mirroring the server: the two document reads are anonymous (the registration page
/// shows both before any session exists), status/respond operate only on the caller's own record, and
/// the three version-management calls require <c>users.manage</c>.
/// </remarks>
public interface ILegalApiClient
{
    /// <summary>The repository LICENSE text plus the SHA-256 digest acceptance is recorded against.</summary>
    Task<ApiResult<LicenseDocument>> GetLicenseAsync(CancellationToken ct = default);

    /// <summary>
    /// The current published Terms of Service. A <b>successful</b> result with a <c>null</c> value means
    /// no version has ever been published — a normal state, not a failure, so the caller must not treat
    /// the two the same.
    /// </summary>
    Task<ApiResult<TermsOfServiceDocument?>> GetCurrentTermsOfServiceAsync(CancellationToken ct = default);

    /// <summary>The calling user's own compliance state.</summary>
    Task<ApiResult<LegalComplianceStatus>> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Records an accept/decline. A <c>409</c> means the echoed <see cref="LegalDocumentResponse.TosVersionId"/>
    /// is stale and the caller should reload the current text before re-prompting.
    /// </summary>
    Task<ApiResult> RespondAsync(LegalDocumentResponse response, CancellationToken ct = default);

    /// <summary>Version history, metadata only — never the content.</summary>
    Task<ApiResult<List<ExistingTermsOfServiceVersion>>> GetVersionsAsync(CancellationToken ct = default);

    /// <summary>One version including its full text, fetched on demand.</summary>
    Task<ApiResult<TermsOfServiceVersionDetail>> GetVersionAsync(int id, CancellationToken ct = default);

    /// <summary>Publishes a new current version. Prior versions are retained untouched.</summary>
    Task<ApiResult<TermsOfServiceVersionDetail>> PublishVersionAsync(
        NewTermsOfServiceVersion request, CancellationToken ct = default);
}

/// <inheritdoc cref="ILegalApiClient" />
public sealed class LegalApiClient(IOdysseyApi api) : ILegalApiClient
{
    private const string Url = "api/legal";
    private const string VersionsUrl = $"{Url}/terms-of-service/versions";

    public Task<ApiResult<LicenseDocument>> GetLicenseAsync(CancellationToken ct = default) =>
        api.GetAsync<LicenseDocument>($"{Url}/license", ct);

    // The response body is literally `null` when nothing is published, which deserializes to a null
    // value on a successful result — hence the nullable T rather than a failure or an empty document.
    public Task<ApiResult<TermsOfServiceDocument?>> GetCurrentTermsOfServiceAsync(CancellationToken ct = default) =>
        api.GetAsync<TermsOfServiceDocument?>($"{Url}/terms-of-service/current", ct);

    public Task<ApiResult<LegalComplianceStatus>> GetStatusAsync(CancellationToken ct = default) =>
        api.GetAsync<LegalComplianceStatus>($"{Url}/status", ct);

    public Task<ApiResult> RespondAsync(LegalDocumentResponse response, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Url}/respond", response, ct);

    // Not GetAllAsync: this endpoint returns a bare array, not a PagedResult.
    public Task<ApiResult<List<ExistingTermsOfServiceVersion>>> GetVersionsAsync(CancellationToken ct = default) =>
        api.GetAsync<List<ExistingTermsOfServiceVersion>>(VersionsUrl, ct);

    public Task<ApiResult<TermsOfServiceVersionDetail>> GetVersionAsync(int id, CancellationToken ct = default) =>
        api.GetAsync<TermsOfServiceVersionDetail>($"{VersionsUrl}/{id}", ct);

    public Task<ApiResult<TermsOfServiceVersionDetail>> PublishVersionAsync(
        NewTermsOfServiceVersion request, CancellationToken ct = default) =>
        api.SendAsync<TermsOfServiceVersionDetail>(HttpMethod.Post, VersionsUrl, request, ct);
}
