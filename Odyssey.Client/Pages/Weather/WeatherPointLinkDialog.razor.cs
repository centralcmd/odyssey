using System.Net;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

public partial class WeatherPointLinkDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }
    [Parameter, EditorRequired] public ExistingLocation Location { get; set; } = default!;

    /// <summary><c>weather.points.read</c> + <c>weather.points.update</c>: the <i>Create a weather point</i> link.</summary>
    [Parameter] public bool CanCreatePoints { get; set; }

    [Parameter] public string? ReturnFocusToId { get; set; }

    /// <summary>A <c>200</c>: the page closes nothing more — the dialog has closed — and renders the outcome.</summary>
    [Parameter] public EventCallback<LocationWeatherPointLink> OnLinked { get; set; }

    /// <summary>A <c>404</c>, or a <c>422</c> keyed <c>id</c>: the place was merged or removed.</summary>
    [Parameter] public EventCallback OnGone { get; set; }

    /// <summary>The <i>Create a weather point</i> link: the dialog closes first, then the page navigates.</summary>
    [Parameter] public EventCallback OnCreatePoint { get; set; }

    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];
    private IReadOnlyList<OdsOption> _options = [];
    private string? _value;
    private string? _error;
    private string? _problem;
    private string _announce = "";
    private bool _loading = true;
    private bool _saving;

    private string InputId => $"wx-link-{_uid}";

    private bool Changing => Location.WeatherPoint is not null;

    private bool NoPoints => !_loading && _options.Count == 0;

    private bool Blocked => _loading || NoPoints || string.IsNullOrEmpty(_value);

    protected override async Task OnInitializedAsync()
    {
        _value = Location.WeatherPoint?.WeatherPointId.ToString("D");
        await LoadOptionsAsync();
    }

    private async Task LoadOptionsAsync()
    {
        _loading = true;
        _announce = "Loading weather points…";
        StateHasChanged();
        var result = await Weather.OptionsAsync(Location.LocationId);
        _loading = false;
        if (!result.IsSuccess || result.Value is null)
        {
            _problem = result.Problem?.Message ?? "The weather points couldn’t be loaded. Try again.";
            _announce = "The weather points couldn't be loaded.";
            return;
        }

        _options = OptionsFor(result.Value.Points, Location.WeatherPoint?.WeatherPointId);
        _announce = _options.Count == 0 ? "There are no weather points yet." : "";
    }

    /// <summary>
    /// Nearest first, as served. Each option: the name over a muted line in words — <i>2 km away</i>, and
    /// <i>history only</i> or <i>weather off</i> when its forecast is off — and the current one notes <i>linked now</i>.
    /// </summary>
    internal static IReadOnlyList<OdsOption> OptionsFor(IReadOnlyList<WeatherPointOption> points, Guid? current) =>
    [
        .. points.Select(o =>
        {
            var parts = new List<string> { WeatherVisuals.KmAway(o.DistanceKm) };
            if (!o.ForecastEnabled)
            {
                parts.Add(o.HistoryEnabled ? "history only" : "weather off");
            }
            var linked = o.WeatherPointId == current;
            return new OdsOption(o.WeatherPointId.ToString("D"), o.Name)
            {
                Sub = string.Join(" · ", parts),
                Note = linked ? "linked now" : null,
                AriaLabel = $"{o.Name}, {string.Join(", ", parts)}{(linked ? ", linked now" : "")}",
            };
        }),
    ];

    private void OnPicked(string? value)
    {
        _value = value;
        _error = null;
    }

    private async Task SaveAsync()
    {
        if (_saving)
        {
            return;
        }
        if (Blocked)
        {
            if (!_loading && !NoPoints)
            {
                _error = "Choose a weather point.";
                _announce = _error;
            }
            return;
        }

        _saving = true;
        _error = null;
        _problem = null;
        var result = await Locations.SetWeatherPointAsync(Location.LocationId, Guid.Parse(_value!));
        _saving = false;

        if (result.IsSuccess && result.Value is { } link)
        {
            await OpenChanged.InvokeAsync(false);
            await OnLinked.InvokeAsync(link);
            return;
        }

        if (result.Status == HttpStatusCode.NotFound
            || (result.Status == HttpStatusCode.UnprocessableEntity && result.Problem?.ErrorFor("id") is not null))
        {
            await OpenChanged.InvokeAsync(false);
            await OnGone.InvokeAsync();
            return;
        }

        if (result.Status == HttpStatusCode.UnprocessableEntity && result.Problem?.ErrorFor("weatherPointId") is not null)
        {
            // The point was deleted (or stopped being usable) while the dialog was open: say so on the picker,
            // keep the selection and reload the options.
            _error = "That weather point is no longer available. Choose another one.";
            _announce = _error;
            await LoadOptionsAsync();
            if (_options.Any(o => o.Value == _value) is false && _value is not null)
            {
                _options = [.. _options, new OdsOption(_value, "A weather point that is no longer available")];
            }
            return;
        }

        _problem = result.Problem?.Message ?? "The weather point couldn’t be linked. Try again.";
    }

    private async Task CreatePointAsync()
    {
        await OpenChanged.InvokeAsync(false);
        await OnCreatePoint.InvokeAsync();
    }

    private Task CloseAsync() => _saving ? Task.CompletedTask : OpenChanged.InvokeAsync(false);

    private Task OnOpenChanged(bool open) => open || !_saving ? OpenChanged.InvokeAsync(open) : Task.CompletedTask;
}
