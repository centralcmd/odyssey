using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsExternalLink
{
    /// <summary>The message after a successful copy.</summary>
    public const string CopiedText = "Link copied";

    /// <summary>The message after a failed copy.</summary>
    public const string CopyFailedText = "Couldn’t copy. Select the text instead.";

    /// <summary>How long a copy result stays on screen.</summary>
    public static readonly TimeSpan StatusDuration = TimeSpan.FromSeconds(5);

    /// <summary>The URL as received (publisher text). Empty renders "—".</summary>
    [Parameter] public string? Url { get; set; }

    /// <summary>The visible link text. An sr-only " (opens in a new tab)" is always appended.</summary>
    [Parameter] public string Text { get; set; } = "Open original";

    /// <summary>Show the "Copy link" icon button.</summary>
    [Parameter] public bool Copyable { get; set; } = true;

    /// <summary>
    /// Render the actions row (Open original, Copy link, the copy status). <c>false</c> shows the URL and host only,
    /// for a tile whose ⋯ menu carries Open original and Copy link; a safe URL is then itself the link (new tab, no
    /// opener, no referrer) and an unsafe one stays text.
    /// </summary>
    [Parameter] public bool Actions { get; set; } = true;

    /// <summary>Raised after a copy attempt with its result. The component has already announced it.</summary>
    [Parameter] public EventCallback<bool> OnCopy { get; set; }

    [Parameter] public string? Class { get; set; }

    private ElementReference _urlRef;
    private IJSObjectReference? _module;
    private CancellationTokenSource? _clear;
    private bool? _status;

    private string? StatusText => _status switch
    {
        true => CopiedText,
        false => CopyFailedText,
        null => null,
    };

    private string ClassName(string baseClass) => string.IsNullOrWhiteSpace(Class) ? baseClass : $"{baseClass} {Class}";

    /// <summary>
    /// The URL to put in an <c>href</c>, or <c>null</c>: only an absolute <c>http</c>/<c>https</c> URL with a
    /// host and no user info qualifies. <c>javascript:</c>, <c>data:</c>, relative and protocol-relative
    /// URLs (which .NET would otherwise read as <c>file:</c>) all return <c>null</c>.
    /// </summary>
    public static Uri? Target(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        var text = url.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || AuthorityHasAt(text))
        {
            return null;
        }
        return string.IsNullOrEmpty(uri.Host) ? null : uri;
    }

    // "https://@host" parses with an empty UserInfo; an '@' anywhere in the authority is still refused.
    private static bool AuthorityHasAt(string text)
    {
        var start = text.IndexOf("//", StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }
        var rest = text[(start + 2)..];
        var end = rest.IndexOfAny(['/', '?', '#', '\\']);
        var authority = end < 0 ? rest : rest[..end];
        return authority.Contains('@');
    }

    private async Task CopyAsync()
    {
        var ok = false;
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/external-link.js");
            ok = await _module.InvokeAsync<bool>("copy", Url ?? string.Empty, _urlRef);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            ok = false;
        }

        await ShowStatusAsync(ok);
        await OnCopy.InvokeAsync(ok);
    }

    private async Task ShowStatusAsync(bool ok)
    {
        _clear?.Cancel();
        var clear = _clear = new CancellationTokenSource();

        // Clear first, so a second identical result is read again.
        if (_status is not null)
        {
            _status = null;
            StateHasChanged();
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), Time, clear.Token);
            }
            catch (TaskCanceledException)
            {
                return; // Superseded by a newer result, or disposed.
            }
        }

        _status = ok;
        StateHasChanged();
        _ = ClearLaterAsync(clear.Token);
    }

    private async Task ClearLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(StatusDuration, Time, token);
            _status = null;
            await InvokeAsync(StateHasChanged);
        }
        catch (TaskCanceledException)
        {
            // Superseded by a newer result, or disposed.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _clear?.Cancel();
        try
        {
            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
