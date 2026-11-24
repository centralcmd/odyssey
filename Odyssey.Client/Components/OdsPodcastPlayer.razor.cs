using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Odyssey.Client.Components;

/// <summary>The in-browser podcast player view (issue #594; design system <c>PodcastPlayer</c>).</summary>
public partial class OdsPodcastPlayer
{
    /// <summary>The module that owns the page's one &lt;audio&gt; and the seek slider's keys.</summary>
    public const string ModulePath = "./js/podcast-player.js";

    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>The episode title; part of every accessible name.</summary>
    [Parameter, EditorRequired] public string Title { get; set; } = string.Empty;

    /// <summary>The podcast's language.</summary>
    [Parameter] public string? Lang { get; set; }

    [Parameter] public OdsPodcastPlayerState State { get; set; } = OdsPodcastPlayerState.Paused;

    /// <summary>Where playback is, in seconds. The slider's value text does not follow these ticks.</summary>
    [Parameter] public double CurrentSeconds { get; set; }

    /// <summary>The media element's duration once known, else the feed's; unknown ⇒ the slider is aria-disabled.</summary>
    [Parameter] public double? DurationSeconds { get; set; }

    /// <summary>0–100.</summary>
    [Parameter] public int Volume { get; set; } = 100;

    [Parameter] public bool Muted { get; set; }

    /// <summary>Overrides the state's status line ("Finished. Listened just now.").</summary>
    [Parameter] public string? Status { get; set; }

    [Parameter] public OdsPodcastPlayerSaveNote? SaveNote { get; set; }

    [Parameter] public OdsPodcastPlayerError? Error { get; set; }

    /// <summary>Named in the unsupported-format message ("MP3", "Ogg").</summary>
    [Parameter] public string? ContentTypeLabel { get; set; }

    [Parameter] public EventCallback OnToggle { get; set; }

    /// <summary>A committed seek in whole seconds: a key press, or the end of a drag.</summary>
    [Parameter] public EventCallback<int> OnSeek { get; set; }

    [Parameter] public EventCallback<int> OnVolume { get; set; }

    [Parameter] public EventCallback OnMute { get; set; }

    /// <summary>Try again: offered for a network failure only.</summary>
    [Parameter] public EventCallback OnRetry { get; set; }

    /// <summary>The ids the page moves focus to.</summary>
    [Parameter] public string? ToggleId { get; set; }

    [Parameter] public string? RetryId { get; set; }

    /// <summary>The error block's id (it takes focus when no Try again is offered).</summary>
    [Parameter] public string? AlertId { get; set; }

    [Parameter] public string? Class { get; set; }

    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];
    private ElementReference _seek;
    private IJSObjectReference? _module;
    private double? _scrub;
    private string _valueText = string.Empty;
    private OdsPodcastPlayerState? _lastState;
    private bool _lastKnown;
    private int _lastTotal;

    private string Name => string.IsNullOrWhiteSpace(Title) ? "(untitled)" : Title;

    private string SeekId => $"podplayer-seek-{_uid}";

    private bool Known => OdsPodcastPlayback.LengthKnown(DurationSeconds);

    private int Total => Known ? (int)Math.Floor(DurationSeconds!.Value) : 0;

    private int Current => (int)Math.Max(0, Math.Min(Known ? Total : double.MaxValue, Math.Floor(CurrentSeconds)));

    private int Shown => _scrub is { } s ? (int)s : Current;

    private bool Busy => State == OdsPodcastPlayerState.Loading;

    private bool PlayingNow => State is OdsPodcastPlayerState.Playing or OdsPodcastPlayerState.Buffering;

    private bool ToggleBlocked => Busy || (Error is { } e && !OdsPodcastPlayback.OffersRetry(e));

    private int VolumePercent => Math.Clamp(Volume, 0, 100);

    private string? Line
    {
        get
        {
            var line = Status ?? OdsPodcastPlayback.StatusText(State);
            var note = SaveNote is { } n ? OdsPodcastPlayback.SaveNoteText(n) : null;
            return string.Join(' ', new[] { line, note }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    protected override void OnParametersSet()
    {
        // The value text follows a seek, a pause and a newly known length — never a playback tick.
        if (_lastState is null || (_lastState != State && !PlayingNow) || Known != _lastKnown || Total != _lastTotal)
        {
            RefreshValueText(null);
        }

        _lastState = State;
        _lastKnown = Known;
        _lastTotal = Total;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await _module.InvokeVoidAsync("bindSeek", _seek, OdsPodcastPlayback.SeekStepSeconds, OdsPodcastPlayback.SeekPageSeconds);
        }
        catch (JSException)
        {
            // Without the module the native range keys (1 s steps) still work.
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private void RefreshValueText(int? at) => _valueText = OdsPodcastPlayback.ValueText(at ?? Shown, Known ? Total : null);

    private void OnSeekFocus() => RefreshValueText(Shown);

    private void OnScrub(ChangeEventArgs e)
    {
        if (Known && TryParse(e.Value, out var v))
        {
            _scrub = v;
        }
    }

    private async Task OnSeekCommitAsync(ChangeEventArgs e)
    {
        _scrub = null;
        if (!Known || !TryParse(e.Value, out var v))
        {
            return;
        }

        var to = Math.Clamp(v, 0, Total);
        RefreshValueText(to);
        await OnSeek.InvokeAsync(to);
    }

    private async Task OnVolumeChangeAsync(ChangeEventArgs e)
    {
        if (TryParse(e.Value, out var v))
        {
            await OnVolume.InvokeAsync(Math.Clamp(v, 0, 100));
        }
    }

    private Task ToggleAsync() => ToggleBlocked ? Task.CompletedTask : OnToggle.InvokeAsync();

    private static bool TryParse(object? value, out int result)
    {
        result = 0;
        if (value is null || !double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
        {
            return false;
        }

        result = (int)Math.Round(d);
        return true;
    }

    private static string Percent(int shown, int total) =>
        (total > 0 ? shown * 100.0 / total : 0).ToString("0.###", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("unbindSeek", _seek);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
            catch (JSException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }
}
