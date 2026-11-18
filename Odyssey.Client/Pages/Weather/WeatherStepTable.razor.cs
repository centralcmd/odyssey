using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

public partial class WeatherStepTable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<WeatherStep> Steps { get; set; } = [];

    /// <summary>The day the rows are listed under (the hours panel); null in the event dialog.</summary>
    [Parameter] public WeatherHourlyDay? Day { get; set; }

    /// <summary>The visually hidden caption that names the table, when no host heading does.</summary>
    [Parameter] public string? Caption { get; set; }

    /// <summary>The id of the host heading that names the table, when it has one.</summary>
    [Parameter] public string? LabelledBy { get; set; }

    [Parameter] public string TimeHeader { get; set; } = "Time";

    /// <summary>The wrapper's name while it is a scroll region.</summary>
    [Parameter] public string RegionLabel { get; set; } = "Hourly weather table, scrolls sideways";

    /// <summary>
    /// Where focus goes if the wrapper holds it when it stops overflowing (it loses its tabindex then): the host's
    /// panel or section label, never <c>&lt;body&gt;</c>.
    /// </summary>
    [Parameter, EditorRequired] public string FocusFallbackId { get; set; } = string.Empty;

    private ElementReference _wrapper;
    private bool _overflows;
    private bool _observing;
    private IJSObjectReference? _module;
    private DotNetObjectReference<WeatherStepTable>? _self;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/weather-hours.js");
            _self = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("observeOverflow", _wrapper, FocusFallbackId, _self);
            _observing = true;
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
            // Prerendering or a test host without the module: the wrapper stays a plain div.
        }
    }

    [JSInvokable]
    public Task OnOverflowChanged(bool overflows)
    {
        if (_overflows == overflows)
            return Task.CompletedTask;
        _overflows = overflows;
        return InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                if (_observing)
                    await _module.InvokeVoidAsync("unobserveOverflow", _wrapper);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _self?.Dispose();
    }
}
