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

    /// <summary>
    /// Posts <paramref name="file"/> to <paramref name="url"/> natively, or returns null when the browser does not hold
    /// that file for <paramref name="containerId"/> — the caller then uses the typed client.
    /// </summary>
    Task<ApiResult<T>?> TryUploadAsync<T>(string containerId, IBrowserFile file, string url, string? contentType, CancellationToken ct = default);

    ValueTask ForgetAsync(string containerId);

    /// <summary>Lets the browser download <paramref name="url"/> to disk.</summary>
    ValueTask DownloadAsync(string url);
}

public sealed class NativeTransfer(IJSRuntime js, AntiforgeryTokenStore antiforgery) : INativeTransfer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ValueTask WatchAsync(string containerId) => js.InvokeVoidAsync("odysseyNativeTransfer.watch", containerId);

    public async Task<ApiResult<T>?> TryUploadAsync<T>(
        string containerId, IBrowserFile file, string url, string? contentType, CancellationToken ct = default)
    {
        if (!await js.InvokeAsync<bool>("odysseyNativeTransfer.has", ct, containerId, file.Name, file.Size))
        {
            return null;
        }

        var token = await antiforgery.GetTokenAsync(ct);
        // No cancellation token here: a timeout on the interop call would abandon a multi-gigabyte upload mid-way.
        var response = await js.InvokeAsync<NativeResponse>(
            "odysseyNativeTransfer.upload", CancellationToken.None,
            containerId, url, "file", AntiforgeryHandler.HeaderName, token, contentType);
        return await ToResultAsync<T>(response);
    }

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

    internal sealed record NativeResponse(int Status, string Body);
}
