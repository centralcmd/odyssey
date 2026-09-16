using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// State and interop for <see cref="ContactAvatarDialog"/>. The markup, and the reasoning behind the
/// native range controls, the aria-hidden canvas and the one validation-failure rule, live in
/// ContactAvatarDialog.razor.
/// </summary>
public partial class ContactAvatarDialog : IAsyncDisposable
{
    /// <summary>Controls visibility. Bindable via <c>@bind-Open</c>.</summary>
    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>The contact whose image is being set. Its type decides the mask, the framing and the
    /// output format; its id is the endpoint the crop is POSTed to.</summary>
    [Parameter, EditorRequired] public ExistingContact? Contact { get; set; }

    /// <summary>Raised after a successful save, so the list can re-read the contact's new
    /// <c>AvatarFileId</c> and invalidate its session cache.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>Optional announcement hook for the page's own live region.</summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    private IJSObjectReference? _module;
    private ElementReference _canvas;
    private ElementReference _dropzoneHost;

    /// <summary>Identifies this dialog's decoded source inside the interop module's own map.</summary>
    private readonly string _handle = $"cav-{Guid.NewGuid():N}";

    private IReadOnlyList<OdsUploadFile> _files = [];
    private bool _hasImage;
    private bool _saving;
    private int _zoom = 100;
    private int _offsetX;
    private int _offsetY;

    /// <summary>Attributable to the source field — rendered on it, and focus moves there.</summary>
    private string? _fileError;

    /// <summary>Not attributable to any control — announced in place, focus stays put.</summary>
    private string? _serverError;

    private string? _live;

    /// <summary>Pending focus move for an attributable failure, applied after the render that shows it.</summary>
    private bool _focusDropzone;

    /// <summary>The effective stored cap: <c>min(instance cap, surface cap)</c>, resolved live.</summary>
    private UploadLimitsDto? _limits;

    /// <summary>Pointer-drag origin, or null when no drag is in progress.</summary>
    private (double X, double Y, int OffsetX, int OffsetY)? _drag;

    private bool IsPerson => Contact?.Type != ContactType.Organization;

    private string Noun => IsPerson ? "picture" : "logo";

    private static string AcceptAttribute => string.Join(',', ContactAvatarLimits.AllowedContentTypes);

    /// <summary>
    /// The crop is encoded as JPEG for a photograph and PNG for a logo — PNG keeps the transparency the
    /// contained frame relies on, and a wordmark re-encoded as JPEG picks up ringing around its edges.
    /// </summary>
    private string OutputContentType => IsPerson ? "image/jpeg" : "image/png";

    /// <summary>
    /// Interpolated from the shared constants, never typed as a literal — the source caps are the
    /// browser's own and are what this field actually enforces.
    /// </summary>
    private string SourceHint =>
        $"{ContactAvatarLimits.TypeLabel} · up to {FormatMegabytes(ContactAvatarLimits.MaxSourceBytes)} · "
        + $"{ContactAvatarLimits.MaxSourceDimension} × {ContactAvatarLimits.MaxSourceDimension} px · one image";

    private string Subtitle =>
        $"{ContactAvatarLimits.TypeLabel} · up to {FormatMegabytes(ContactAvatarLimits.MaxSourceBytes)} · "
        + $"stored at {ContactAvatarLimits.OutputDimension} × {ContactAvatarLimits.OutputDimension}";

    private static string Percent(int value) => $"{value} %";

    private string HorizontalText => _offsetX == 0
        ? "centred"
        : $"{Math.Abs(_offsetX)} % {(_offsetX < 0 ? "left of centre" : "right of centre")}";

    private string VerticalText => _offsetY == 0
        ? "centred"
        : $"{Math.Abs(_offsetY)} % {(_offsetY < 0 ? "above centre" : "below centre")}";

    private string StateText => _hasImage
        ? $"Showing {(_offsetX == 0 && _offsetY == 0 ? "the centre" : "an off-centre area")} of your {Noun} at {_zoom} %."
        : $"No image chosen yet. The {Noun} is "
          + (IsPerson
              ? "cropped to a square and shown as a circle."
              : "contained, never cropped, on a neutral ground.");

    protected override async Task OnInitializedAsync()
    {
        // A 503 from /api/upload-limits leaves the dialog usable with the cache's compiled fallback —
        // GetAsync never returns null and never caches a failure.
        _limits = (await UploadLimits.GetAsync()).TightenTo(ContactAvatarLimits.MaxAvatarMegabytes);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusDropzone)
        {
            _focusDropzone = false;
            var module = await ModuleAsync();
            await module.InvokeVoidAsync("focusWithin", _dropzoneHost, ".odc-upload-drop");
        }

        if (_hasImage)
        {
            await DrawAsync();
        }
    }

    private async Task<IJSObjectReference> ModuleAsync() =>
        _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/contact-avatar.js");

    // ── Source ────────────────────────────────────────────────────────────────────────────────────

    private async Task OnFilesChangedAsync(IReadOnlyList<OdsUploadFile> files)
    {
        _serverError = null;
        _files = files;

        if (files.Count == 0 || files[0].Source is not { } source)
        {
            _hasImage = false;
            var cleared = await ModuleAsync();
            await cleared.InvokeVoidAsync("release", _handle);
            return;
        }

        // The type and byte caps are checked HERE, where IBrowserFile already knows both, rather than
        // after streaming 20 MB into the browser's heap to find out.
        if (!ContactAvatarLimits.IsAllowedContentType(source.ContentType))
        {
            RejectSource($"That file is a {Describe(source.ContentType)}. Choose a {ContactAvatarLimits.TypeLabel} image.");
            return;
        }

        if (source.Size > ContactAvatarLimits.MaxSourceBytes)
        {
            RejectSource(
                $"That file is {FormatMegabytes(source.Size)}. "
                + $"Choose an image of {FormatMegabytes(ContactAvatarLimits.MaxSourceBytes)} or less.");
            return;
        }

        var module = await ModuleAsync();
        LoadResult result;
        try
        {
            // A DotNetStreamReference, not a byte[]: a byte[] crossing interop is base64-marshalled,
            // which would inflate the source by a third on the way over.
            await using var stream = source.OpenReadStream(ContactAvatarLimits.MaxSourceBytes);
            using var streamRef = new DotNetStreamReference(stream, leaveOpen: true);
            result = await module.InvokeAsync<LoadResult>(
                "load", _handle, streamRef, source.ContentType, ContactAvatarLimits.MaxSourceDimension);
        }
        catch (JSException)
        {
            RejectSource($"Unable to read that image. Choose a {ContactAvatarLimits.TypeLabel} file.");
            return;
        }

        if (!result.Ok)
        {
            RejectSource(result.Reason switch
            {
                "dimensions" =>
                    $"That image is {result.Width} × {result.Height} pixels. It must be at most "
                    + $"{ContactAvatarLimits.MaxSourceDimension} × {ContactAvatarLimits.MaxSourceDimension}.",
                _ => $"Unable to read that image. Choose a {ContactAvatarLimits.TypeLabel} file.",
            });
            return;
        }

        _fileError = null;
        _hasImage = true;
        _zoom = 100;
        _offsetX = 0;
        _offsetY = 0;
    }

    /// <summary>
    /// An attributable failure: the message renders on the source field (which links it with
    /// <c>aria-describedby</c> and sets <c>aria-invalid</c>) and focus moves there after the render.
    /// </summary>
    private void RejectSource(string message)
    {
        _fileError = message;
        _files = [];
        _hasImage = false;
        _focusDropzone = true;
    }

    // ── Crop ──────────────────────────────────────────────────────────────────────────────────────

    private Task SetZoom(string? raw) => SetCrop(zoom: ParseRange(raw, 100, 300, _zoom));

    private Task SetOffsetX(string? raw) => SetCrop(offsetX: ParseRange(raw, -100, 100, _offsetX));

    private Task SetOffsetY(string? raw) => SetCrop(offsetY: ParseRange(raw, -100, 100, _offsetY));

    private Task SetCrop(int? zoom = null, int? offsetX = null, int? offsetY = null)
    {
        _zoom = zoom ?? _zoom;
        _offsetX = offsetX ?? _offsetX;
        _offsetY = offsetY ?? _offsetY;
        return DrawAsync();
    }

    private Task ResetAsync() => SetCrop(zoom: 100, offsetX: 0, offsetY: 0);

    private static int ParseRange(string? raw, int min, int max, int fallback) =>
        int.TryParse(raw, out var value) ? Math.Clamp(value, min, max) : fallback;

    private async Task DrawAsync()
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync(
            "draw", _handle, _canvas, ContactAvatarLimits.OutputDimension, IsPerson, _zoom, _offsetX, _offsetY);
    }

    // Pointer drag — an addition to the three ranges, never a replacement, so the same area is
    // reachable with a keyboard alone.
    private void OnPointerDown(PointerEventArgs e)
    {
        if (_hasImage)
        {
            _drag = (e.ClientX, e.ClientY, _offsetX, _offsetY);
        }
    }

    private Task OnPointerMove(PointerEventArgs e)
    {
        if (_drag is not { } origin)
        {
            return Task.CompletedTask;
        }

        const double sensitivity = 0.55;
        return SetCrop(
            offsetX: (int)Math.Clamp(origin.OffsetX - ((e.ClientX - origin.X) * sensitivity), -100, 100),
            offsetY: (int)Math.Clamp(origin.OffsetY - ((e.ClientY - origin.Y) * sensitivity), -100, 100));
    }

    private void OnPointerUp() => _drag = null;

    // ── Save ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true to let the shell close; false keeps the dialog open with the crop state intact, so
    /// a rejection can be retried without re-picking the file.
    /// </summary>
    private async Task<bool> SaveAsync()
    {
        if (Contact is not { } contact)
        {
            return false;
        }

        if (!_hasImage)
        {
            RejectSource($"Choose an image to use as this contact's {Noun}.");
            return false;
        }

        _serverError = null;
        _saving = true;
        _live = $"Uploading {Noun}…";

        try
        {
            var module = await ModuleAsync();
            byte[] bytes;
            await using (var encoded = await module.InvokeAsync<IJSStreamReference>(
                "encode", _canvas, OutputContentType, ContactAvatarLimits.JpegQuality))
            await using (var stream = await encoded.OpenReadStreamAsync(ContactAvatarLimits.MaxSourceBytes))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                bytes = buffer.ToArray();
            }

            var cap = EffectiveMaxBytes;
            if (bytes.LongLength > cap)
            {
                // The local pre-check names the number ACTUALLY in force, resolved from the live cap —
                // not a compiled-in one, which would refuse at the old value after an administrator
                // lowered or raised it.
                _serverError =
                    $"Unable to save the {Noun}. The image must be {FormatMegabytes(cap)} or smaller. "
                    + "Zoom out, or choose a different file.";
                return false;
            }

            var upload = new ApiUpload(
                $"contact-{Noun}{ContactAvatarLimits.ExtensionFor(OutputContentType)}",
                OutputContentType,
                bytes.LongLength,
                () => new MemoryStream(bytes));

            var result = await ContactsApi.UploadAvatarAsync(contact.ContactId, upload);
            if (!result.IsSuccess)
            {
                // The server's own ProblemDetails text — it names the actual limit or the accepted
                // types — rather than a generic client string.
                _serverError = result.Problem?.Detail
                    ?? $"Unable to save the {Noun}. The server could not be reached. Check your connection and try again.";
                return false;
            }

            _live = IsPerson ? "Picture saved" : "Logo saved";
            await OnAnnounce.InvokeAsync(_live);
            return true;
        }
        catch (JSException)
        {
            _serverError = $"Unable to prepare the {Noun} for upload. Try a different image.";
            return false;
        }
        finally
        {
            _saving = false;
            if (_serverError is not null)
            {
                _live = null;
            }
        }
    }

    private long EffectiveMaxBytes =>
        Math.Min(
            _limits?.MaxUploadBytes ?? UploadLimitsCache.Fallback.MaxUploadBytes,
            ContactAvatarLimits.MaxAvatarBytes);

    private static string FormatMegabytes(long bytes) =>
        $"{Math.Round(bytes / (1024d * 1024d), 1)} MB";

    /// <summary>Bounded, because it echoes a value the browser reported for a file the user picked.</summary>
    private static string Describe(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType)
            ? "file of an unknown type"
            : contentType.Length > 64 ? contentType[..64] : contentType;

    /// <summary>What <c>contact-avatar.js</c>'s <c>load</c> reports back.</summary>
    private sealed record LoadResult(bool Ok, string? Reason, int Width, int Height);

    public async ValueTask DisposeAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("dispose", _handle);
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit is already gone; there is nothing left to release.
        }
    }
}
