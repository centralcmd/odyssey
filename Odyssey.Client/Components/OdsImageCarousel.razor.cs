using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Odyssey.Client.Components;

/// <summary>The parameters, navigation and announcements behind <c>OdsImageCarousel</c> (see the .razor header).</summary>
public partial class OdsImageCarousel
{
    /// <summary>The images in display order.</summary>
    [Parameter] public IReadOnlyList<OdsCarouselImage> Images { get; set; } = [];

    /// <summary>The carousel's accessible name, e.g. "Images for ‹article title›".</summary>
    [Parameter] public string Label { get; set; } = "Images";

    /// <summary>The <c>lang</c> of the publisher's alt text (a validated BCP 47 tag), or <c>null</c>.</summary>
    [Parameter] public string? Lang { get; set; }

    /// <summary>The slide shown first (clamped).</summary>
    [Parameter] public int InitialIndex { get; set; }

    /// <summary>Raised after the user moves to another slide.</summary>
    [Parameter] public EventCallback<int> IndexChanged { get; set; }

    [Parameter] public string? Class { get; set; }

    private OdsLiveAnnouncer? _announcer;
    private readonly HashSet<int> _failed = [];
    private int _index;
    private bool _animate;
    private bool _initialised;
    private IReadOnlyList<OdsCarouselImage>? _lastImages;

    /// <summary>The current slide (for tests and hosts).</summary>
    public int Index => _index;

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(Images, _lastImages))
        {
            _lastImages = Images;
            _failed.Clear();
        }

        if (!_initialised)
        {
            _initialised = true;
            _index = InitialIndex;
        }

        _index = Math.Clamp(_index, 0, Math.Max(0, Images.Count - 1));
    }

    private string ClassName(string baseClass) => string.IsNullOrWhiteSpace(Class) ? baseClass : $"{baseClass} {Class}";

    private string Ratio => FrameRatio(Images.Count > 0 ? Images[0] : null);

    /// <summary>
    /// The carousel's one frame ratio, as a CSS <c>aspect-ratio</c>: the first image's width/height, each
    /// clamped to 1…4096, with the ratio clamped to 4:3 … 16:9. No usable dimensions give 4:3.
    /// </summary>
    public static string FrameRatio(OdsCarouselImage? first)
    {
        if (first is not { Width: > 0, Height: > 0 })
            return "4 / 3";
        var width = Math.Clamp(first.Width.Value, 1, 4096);
        var height = Math.Clamp(first.Height.Value, 1, 4096);
        var ratio = (double)width / height;
        if (ratio >= 16.0 / 9)
            return "16 / 9";
        if (ratio <= 4.0 / 3)
            return "4 / 3";
        return $"{width} / {height}";
    }

    /// <summary>The <c>alt</c> for an image: the publisher's, else the "no description" fallback.</summary>
    public static string AltFor(OdsCarouselImage image, int index, int count) =>
        HasAlt(image) ? image.Alt!.Trim()
        : count > 1 ? $"Image {index + 1} of {count}, no description from the publisher"
        : "Image, no description from the publisher";

    /// <summary>
    /// What the live region says on arriving at a slide: the position only. The alt text is read from the image
    /// itself, so it is never spoken twice (the design system's ImageCarousel).
    /// </summary>
    public static string SpokenFor(int index, int count) => $"Image {index + 1} of {count}";

    private static bool HasAlt(OdsCarouselImage image) => !string.IsNullOrWhiteSpace(image.Alt);

    private async Task GoAsync(int next)
    {
        if (next < 0)
        {
            await SayAsync("First image");
            return;
        }

        if (next > Images.Count - 1)
        {
            await SayAsync("Last image");
            return;
        }

        if (next == _index)
            return;

        _animate = true;
        _index = next;
        StateHasChanged();
        await IndexChanged.InvokeAsync(next);
        await SayAsync(SpokenFor(next, Images.Count));
    }

    private Task SayAsync(string message)
    {
        // The announcer queues and plays in the background; awaiting it would hold the click handler.
        _ = _announcer?.Announce(message);
        return Task.CompletedTask;
    }

    private Task OnKeyDown(KeyboardEventArgs e) => e.Key switch
    {
        "ArrowLeft" => GoAsync(_index - 1),
        "ArrowRight" => GoAsync(_index + 1),
        _ => Task.CompletedTask,
    };

    private void OnImageError(int index)
    {
        if (_failed.Add(index))
            StateHasChanged();
    }

    private static int? Dimension(int? value) => value is > 0 and <= 4096 ? value : null;
}
