using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Odyssey.Dtos;

namespace Odyssey.ApiClient;

/// <summary>
/// The transport core every typed resource client is built on: JSON reads and writes, paged reads,
/// file downloads and multipart uploads, each returning an <see cref="ApiResult{T}"/> rather than
/// throwing or presenting anything.
/// </summary>
/// <remarks>
/// Deliberately has no opinion about how failures reach a human — see the remarks on
/// <see cref="ApiResult"/>. Authentication is not handled here either: it rides on the
/// <see cref="HttpClient"/> pipeline (cookies plus the antiforgery header, see
/// <c>Odyssey.ApiClient.Auth.AntiforgeryHandler</c>).
/// </remarks>
public interface IOdysseyApi
{
    /// <summary>The configured API base address, for callers that need to form an absolute URL
    /// (e.g. an <c>&lt;img src&gt;</c> pointing at a file-content endpoint).</summary>
    Uri? BaseAddress { get; }

    /// <summary>GETs and deserializes <typeparamref name="T"/>.</summary>
    Task<ApiResult<T>> GetAsync<T>(string url, CancellationToken ct = default);

    /// <summary>GETs a <see cref="PagedResult{T}"/> for a server-paginated list (issue #277).</summary>
    Task<ApiResult<PagedResult<T>>> GetPagedAsync<T>(string url, CancellationToken ct = default);

    /// <summary>
    /// GETs every matching row of a paginated list in one window and returns just the items — for
    /// reference-data and dropdown loads. Any window already on <paramref name="url"/> is overwritten.
    /// </summary>
    Task<ApiResult<List<T>>> GetAllAsync<T>(string url, CancellationToken ct = default);

    /// <summary>Sends a JSON write and discards any response body.</summary>
    Task<ApiResult> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct = default);

    /// <summary>Sends a JSON write and deserializes the response body as <typeparamref name="T"/>.</summary>
    Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct = default);

    /// <summary>
    /// GETs a file. <paramref name="defaultFileName"/> is used when the response carries no
    /// <c>Content-Disposition</c> filename.
    /// </summary>
    /// <param name="url">The request URL.</param>
    /// <param name="defaultFileName">Used when the response carries no <c>Content-Disposition</c> filename.</param>
    /// <param name="completenessMarker">
    /// Opt-in completeness check for the four Goal-8 bulk import/export surfaces (issue #343 §11): when
    /// supplied (e.g. <c>"BEGIN:VCARD"</c>), the downloaded body's occurrences of this marker are
    /// compared against the response's <c>X-Odyssey-Export-Rows</c> header. A missing header or a
    /// mismatched count is treated as a failed download (never a smaller-but-valid one) — a short
    /// count means the response was truncated mid-stream, and a missing header could mean anything
    /// from a CORS regression to a proxy stripping unlisted headers, so both fail closed. Every other
    /// caller of this method (single-record downloads, the admin data export, file attachments) leaves
    /// this <see langword="null"/> and is unaffected.
    /// </param>
    Task<ApiResult<ApiFile>> GetFileAsync(
        string url, string defaultFileName, string? completenessMarker = null, CancellationToken ct = default);

    /// <summary>
    /// POSTs a multipart upload under the form field <paramref name="fieldName"/> and deserializes the
    /// response as <typeparamref name="T"/>. <paramref name="contentTypeOverride"/> forces the part's
    /// media type (the ICS/vCard imports declare <c>text/calendar</c> / <c>text/vcard</c> regardless of
    /// what the browser reported).
    /// </summary>
    Task<ApiResult<T>> UploadAsync<T>(
        string url,
        ApiUpload file,
        string fieldName = "file",
        string? contentTypeOverride = null,
        CancellationToken ct = default);
}

/// <inheritdoc cref="IOdysseyApi" />
public sealed class OdysseyApi(HttpClient http) : IOdysseyApi
{
    public Uri? BaseAddress => http.BaseAddress;

    public async Task<ApiResult<T>> GetAsync<T>(string url, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return ApiResult<T>.Failure(response.StatusCode, await response.ReadProblemAsync());

            return ApiResult<T>.Success(await response.Content.ReadFromJsonAsync<T>(ct), response.StatusCode);
        }
        catch (Exception ex)
        {
            return ApiResult<T>.Failure(ex);
        }
    }

    public Task<ApiResult<PagedResult<T>>> GetPagedAsync<T>(string url, CancellationToken ct = default) =>
        GetAsync<PagedResult<T>>(url, ct);

    public async Task<ApiResult<List<T>>> GetAllAsync<T>(string url, CancellationToken ct = default)
    {
        var result = await GetPagedAsync<T>(ForceFullPage(url), ct);
        return result.IsSuccess
            ? ApiResult<List<T>>.Success([.. result.Value?.Items ?? []], result.Status)
            : result.CastFailure<List<T>>();
    }

    // "Load all" must own its window: overwrite any page/pageSize/offset/limit the caller left on the
    // URL with a single large window, so a reference-data load can never be silently truncated to the
    // resource default limit (issue #277 follow-up).
    private static string ForceFullPage(string url)
    {
        var parts = url.Split('?', 2);
        var kept = parts.Length == 2
            ? parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(pair => pair.Split('=', 2)[0] is not ("page" or "pageSize" or "offset" or "limit"))
            : [];
        var query = string.Join('&', new[] { "offset=0", $"limit={PagedQuery.LimitAll}" }.Concat(kept));
        return $"{parts[0]}?{query}";
    }

    public async Task<ApiResult> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendCoreAsync(method, url, body, ct);
            return response.IsSuccessStatusCode
                ? ApiResult.Success(response.StatusCode, response.Headers.Location)
                : ApiResult.Failure(response.StatusCode, await response.ReadProblemAsync());
        }
        catch (Exception ex)
        {
            return ApiResult.Failure(ex);
        }
    }

    public async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendCoreAsync(method, url, body, ct);
            if (!response.IsSuccessStatusCode)
                return ApiResult<T>.Failure(response.StatusCode, await response.ReadProblemAsync());

            return ApiResult<T>.Success(
                await response.Content.ReadFromJsonAsync<T>(ct), response.StatusCode, response.Headers.Location);
        }
        catch (Exception ex)
        {
            return ApiResult<T>.Failure(ex);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        // The request must stay alive until the send completes, hence the await inside the using
        // rather than returning the task.
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType());

        return await http.SendAsync(request, ct);
    }

    public async Task<ApiResult<ApiFile>> GetFileAsync(
        string url, string defaultFileName, string? completenessMarker = null, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return ApiResult<ApiFile>.Failure(response.StatusCode, await response.ReadProblemAsync());

            var file = await ReadFileAsync(response, defaultFileName, ct);

            if (completenessMarker is not null && !IsComplete(response, file, completenessMarker))
            {
                return ApiResult<ApiFile>.Failure(response.StatusCode, new ApiProblem
                {
                    Detail = "The download appears to be incomplete. Please try again.",
                });
            }

            return ApiResult<ApiFile>.Success(file, response.StatusCode);
        }
        catch (Exception ex)
        {
            return ApiResult<ApiFile>.Failure(ex);
        }
    }

    // issue #343 §11: a missing header or a mismatched count both fail closed. A missing header has no
    // legitimate case to protect against here — every server-side surface that sets completenessMarker
    // on its call also always sends X-Odyssey-Export-Rows — so "absent" is treated exactly like "short",
    // never as "unverifiable, allow it".
    private static bool IsComplete(HttpResponseMessage response, ApiFile file, string completenessMarker)
    {
        if (!response.Headers.TryGetValues("X-Odyssey-Export-Rows", out var values))
            return false;

        if (!int.TryParse(values.FirstOrDefault(), out var declaredCount))
            return false;

        var body = System.Text.Encoding.UTF8.GetString(file.Bytes);
        var actualCount = 0;
        var index = 0;
        while ((index = body.IndexOf(completenessMarker, index, StringComparison.Ordinal)) >= 0)
        {
            actualCount++;
            index += completenessMarker.Length;
        }

        return actualCount == declaredCount;
    }

    public async Task<ApiResult<T>> UploadAsync<T>(
        string url,
        ApiUpload file,
        string fieldName = "file",
        string? contentTypeOverride = null,
        CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            using var stream = file.OpenRead();
            var part = new StreamContent(stream);

            var mediaType = contentTypeOverride
                            ?? (string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
            part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            content.Add(part, fieldName, file.FileName);

            using var response = await http.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
                return ApiResult<T>.Failure(response.StatusCode, await response.ReadProblemAsync());

            return ApiResult<T>.Success(
                await response.Content.ReadFromJsonAsync<T>(ct), response.StatusCode, response.Headers.Location);
        }
        catch (Exception ex)
        {
            return ApiResult<T>.Failure(ex);
        }
    }

    /// <summary>
    /// Reads the body as bytes and resolves the filename, preferring the RFC 5987 <c>filename*</c>
    /// form over the quoted <c>filename</c>.
    /// </summary>
    private static async Task<ApiFile> ReadFileAsync(HttpResponseMessage response, string defaultFileName, CancellationToken ct)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = disposition?.FileNameStar
                       ?? disposition?.FileName?.Trim('"')
                       ?? defaultFileName;

        return new ApiFile(bytes, fileName, response.Content.Headers.ContentType?.MediaType);
    }
}
