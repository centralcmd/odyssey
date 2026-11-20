using Microsoft.JSInterop;
using Odyssey.ApiClient;

namespace Odyssey.Client.Services;

/// <summary>
/// The one way the client hands downloaded bytes to the browser's save dialog
/// (the <c>downloadFileFromBytes</c> function in <c>wwwroot/index.html</c>).
/// </summary>
public static class FileDownloads
{
    /// <summary>The media type sent when neither the response nor the caller names one.</summary>
    public const string FallbackContentType = "application/octet-stream";

    /// <summary>Saves <paramref name="bytes"/> as <paramref name="fileName"/>; a blank type becomes <see cref="FallbackContentType"/>.</summary>
    public static ValueTask SaveFileAsync(this IJSRuntime js, byte[] bytes, string fileName, string? contentType) =>
        js.InvokeVoidAsync(
            "downloadFileFromBytes", bytes, fileName,
            string.IsNullOrWhiteSpace(contentType) ? FallbackContentType : contentType);

    /// <summary>
    /// Saves a downloaded <see cref="ApiFile"/> under <paramref name="fileName"/>. The response's own
    /// content type wins, then <paramref name="knownContentType"/> (the stored metadata's), then
    /// <see cref="FallbackContentType"/>.
    /// </summary>
    public static ValueTask SaveFileAsync(this IJSRuntime js, ApiFile file, string fileName, string? knownContentType = null) =>
        js.SaveFileAsync(file.Bytes, fileName, file.ContentType ?? knownContentType);
}
