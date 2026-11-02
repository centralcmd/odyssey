using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Services;

/// <summary>
/// The location map's basemap configuration (<c>GET /api/map-styles</c>, issue #370), fetched at most once
/// per session (issue #371 §4, F6).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> shaped like <see cref="UploadLimitsCache"/>: there is <b>no fallback value</b>.
/// A client-held default style URL would be a client-side copy of server configuration, so a failure
/// returns <see langword="null"/> and the map shows its <c>config-unavailable</c> state. A failure is never
/// cached, so <b>Try again</b> refetches.
/// </para>
/// <para>
/// It never goes through <c>OrToast</c>: a map failure is an in-place state, not a toast. The shared
/// in-flight fetch runs with <see cref="CancellationToken.None"/> and each caller awaits it with its own
/// token, so one map's cancellation never cancels another's fetch.
/// </para>
/// </remarks>
public interface IMapStylesCache
{
    /// <summary>The configuration, or <see langword="null"/> when it could not be loaded or failed the defensive check.</summary>
    Task<MapStyles?> GetAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IMapStylesCache" />
public sealed class MapStylesCache(IMapStylesApiClient api) : IMapStylesCache
{
    private Task<MapStyles?>? pending;

    public async Task<MapStyles?> GetAsync(CancellationToken ct = default)
    {
        var task = pending ??= LoadAsync();
        var result = await task.WaitAsync(ct);

        if (result is null && ReferenceEquals(pending, task))
        {
            pending = null;
        }

        return result;
    }

    private async Task<MapStyles?> LoadAsync()
    {
        var result = await api.GetAsync(CancellationToken.None);
        return result.IsSuccess && result.Value is { } styles && IsUsable(styles) ? styles : null;
    }

    /// <summary>
    /// Defence in depth behind the API's start-up validation (#370 §8, R2-S3): each style URL is an
    /// <c>https:</c> URL or a root-relative path, and the attribution link is <c>https:</c>. Anything else
    /// is treated as unavailable rather than handed to MapLibre or rendered as a link.
    /// </summary>
    public static bool IsUsable(MapStyles styles) =>
        IsStyleUrl(styles.LightStyleUrl)
        && IsStyleUrl(styles.DarkStyleUrl)
        && IsHttps(styles.AttributionUrl)
        && !string.IsNullOrWhiteSpace(styles.Attribution);

    private static bool IsStyleUrl(string? value) =>
        value is { Length: > 0 }
        && ((value[0] == '/' && (value.Length == 1 || value[1] is not ('/' or '\\'))) || IsHttps(value));

    private static bool IsHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
