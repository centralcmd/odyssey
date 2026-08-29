using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for <c>GET /api/file-analysis/disclosure</c> (issue #421 Wave 1): the processor
/// disclosure the analyze-file consent gate renders, for any authenticated caller — no permission
/// claim, because the gate is shown to ordinary users while the settings API is Admin-only.
///
/// <para>
/// A <c>503</c> (a degraded read server-side) surfaces as an ordinary failed
/// <see cref="ApiResult{T}"/>; the caller renders its compiled fallback for that attempt and keeps the
/// affirmation disabled, rather than presenting a fallback as authoritative disclosure.
/// </para>
/// </summary>
public interface IFileAnalysisDisclosureApiClient
{
    Task<ApiResult<FileAnalysisDisclosureDto>> GetAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IFileAnalysisDisclosureApiClient" />
public sealed class FileAnalysisDisclosureApiClient(IOdysseyApi api) : IFileAnalysisDisclosureApiClient
{
    private const string Url = "api/file-analysis/disclosure";

    public Task<ApiResult<FileAnalysisDisclosureDto>> GetAsync(CancellationToken ct = default) =>
        api.GetAsync<FileAnalysisDisclosureDto>(Url, ct);
}
