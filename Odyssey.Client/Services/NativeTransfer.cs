using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Auth;

namespace Odyssey.Client.Services;

/// <summary>
/// Downloads and uploads the browser performs itself, for files too large for the WASM heap: a task export carries
/// its attached files inline and can reach gigabytes, while the typed API client reads a body into one array. The
/// download is a plain navigation to the export URL; the upload posts the <c>File</c> the user picked straight from
/// disk (<c>window.odysseyNativeTransfer</c> in <c>index.html</c>), with the antiforgery header the typed client
/// would have attached. The result comes back as the same <see cref="ApiResult{T}"/> the typed client returns, so a
/// page handles both paths alike.
/// </summary>
public interface INativeTransfer
{
    /// <summary>Starts remembering the file picked or dropped inside the element with <paramref name="containerId"/>.</summary>
    ValueTask WatchAsync(string containerId);

    /// <summary>Whether the browser holds <paramref name="file"/> for <paramref name="containerId"/>, so it can post it itself.</summary>
    Task<bool> HoldsAsync(string containerId, IBrowserFile file, CancellationToken ct = default);

    /// <summary>
    /// Posts <paramref name="file"/> to <paramref name="url"/> natively, or returns null when the browser does not hold
    /// that file for <paramref name="containerId"/> — the caller then uses the typed client.
    /// </summary>
    Task<ApiResult<T>?> TryUploadAsync<T>(string containerId, IBrowserFile file, string url, string? contentType, CancellationToken ct = default);

    /// <summary>
    /// The calendar import's upload (issue #517): posts <paramref name="file"/> with the form <paramref name="fields"/>
    /// beside it, calls <paramref name="onUploadComplete"/> once when the browser has sent the whole body (before the
    /// response), and reports whether the client aborted it and whether the body had been sent. Null when the browser
    /// does not hold the file. Cancelling <paramref name="ct"/> stops the callback, never the upload: only
    /// <see cref="AbortAsync"/> stops that.
    /// </summary>
    Task<NativeUploadOutcome<T>?> TryUploadAsync<T>(
        string containerId, IBrowserFile file, string url, string? contentType,
        IReadOnlyList<KeyValuePair<string, string>>? fields, Action? onUploadComplete, CancellationToken ct = default);

    /// <summary>Stops the upload in flight for <paramref name="containerId"/>, if any.</summary>
    ValueTask AbortAsync(string containerId);

    ValueTask ForgetAsync(string containerId);

    /// <summary>Lets the browser download <paramref name="url"/> to disk.</summary>
    ValueTask DownloadAsync(string url);
}

/// <summary>
/// A native upload's result plus what the browser saw of it (issue #517): <see cref="Aborted"/> is true only when the
/// client called abort; <see cref="UploadComplete"/> says the whole body had been sent; <see cref="JsonBody"/> is false
/// for a body that is not JSON (a proxy's HTML <c>504</c> page, say).
/// </summary>
public sealed record NativeUploadOutcome<T>(ApiResult<T> Result, bool Aborted, bool UploadComplete, bool JsonBody);

public sealed class NativeTransfer(IJSRuntime js, AntiforgeryTokenStore antiforgery) : INativeTransfer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ValueTask WatchAsync(string containerId) => js.InvokeVoidAsync("odysseyNativeTransfer.watch", containerId);

    public async Task<bool> HoldsAsync(string containerId, IBrowserFile file, CancellationToken ct = default) =>
        await js.InvokeAsync<bool>("odysseyNativeTransfer.has", ct, containerId, file.Name, file.Size);

    public async Task<ApiResult<T>?> TryUploadAsync<T>(
        string containerId, IBrowserFile file, string url, string? contentType, CancellationToken ct = default) =>
        (await TryUploadAsync<T>(containerId, file, url, contentType, null, null, ct))?.Result;

    public async Task<NativeUploadOutcome<T>?> TryUploadAsync<T>(
        string containerId, IBrowserFile file, string url, string? contentType,
        IReadOnlyList<KeyValuePair<string, string>>? fields, Action? onUploadComplete, CancellationToken ct = default)
    {
        if (!await HoldsAsync(containerId, file, ct))
        {
            return null;
        }

        var token = await antiforgery.GetTokenAsync(ct);
        var form = fields?.ToDictionary(f => f.Key, f => f.Value) ?? [];

        // The reference is released on every exit: the script disposes it when the request ends (success, error or
        // abort), this method when the call returns, and a cancelled ct — the caller going away — at once.
        using var reference = onUploadComplete is null ? null : DotNetObjectReference.Create(new UploadCompleteRelay(onUploadComplete));
        using var release = reference is null ? default : ct.Register(reference.Dispose);

        // No cancellation token here: a timeout on the interop call would abandon a multi-gigabyte upload mid-way.
        var response = await js.InvokeAsync<NativeResponse>(
            "odysseyNativeTransfer.upload", CancellationToken.None,
            containerId, url, "file", AntiforgeryHandler.HeaderName, token, contentType, form, reference);
        return new NativeUploadOutcome<T>(await ToResultAsync<T>(response), response.Aborted, response.UploadComplete, IsJson(response.Body));
    }

    public ValueTask AbortAsync(string containerId) => js.InvokeVoidAsync("odysseyNativeTransfer.abort", containerId);

    public ValueTask ForgetAsync(string containerId) => js.InvokeVoidAsync("odysseyNativeTransfer.forget", containerId);

    public ValueTask DownloadAsync(string url) => js.InvokeVoidAsync("downloadFromUrl", url, string.Empty);

    internal static async Task<ApiResult<T>> ToResultAsync<T>(NativeResponse response)
    {
        if (response.Status == 0)
        {
            return ApiResult<T>.Failure(new HttpRequestException(response.Body));
        }

        var status = (HttpStatusCode)response.Status;
        if (response.Status is >= 200 and < 300)
        {
            try
            {
                return ApiResult<T>.Success(JsonSerializer.Deserialize<T>(response.Body, Json), status);
            }
            catch (JsonException ex)
            {
                return ApiResult<T>.Failure(ex);
            }
        }

        using var message = new HttpResponseMessage(status)
        {
            Content = new StringContent(response.Body, Encoding.UTF8, "application/problem+json"),
        };
        return ApiResult<T>.Failure(status, await message.ReadProblemAsync());
    }

    internal static bool IsJson(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal sealed record NativeResponse(int Status, string Body, bool Aborted = false, bool UploadComplete = false);

    /// <summary>The target the script calls once the body has been sent.</summary>
    internal sealed class UploadCompleteRelay(Action onUploadComplete)
    {
        private int fired;

        [JSInvokable]
        public void OnUploadComplete()
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
            {
                onUploadComplete();
            }
        }
    }
}
