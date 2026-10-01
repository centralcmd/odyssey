using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Odyssey.Client.Components;

/// <summary>
/// Reports the rendered pixel width of a chart's plot box (<c>.odc-lc-plot</c>) so the chart can set
/// its viewBox width to it (Odyssey Design System · LineChart / StepChart, issue #274).
///
/// <para>
/// The axis labels are sized in SVG user units. Under the old fixed 1000-unit viewBox a chart
/// rendered 350px wide scaled them by 0.35, so the three-up <c>/tax-statements</c> cards drew a 10px
/// label at about 3.5px. With the viewBox tracking the measured width, one unit is one pixel at any
/// width. The width is never clamped to a minimum: a clamp brings the scaling back.
/// </para>
///
/// <para>
/// <see cref="SyncAsync"/> is called after every render with the plot element, or <c>null</c> while
/// the chart shows its empty state, so an observer follows the element across an empty → data swap.
/// </para>
/// </summary>
internal sealed class PlotWidthObserver(IJSRuntime js, Func<double, Task> onWidth) : IAsyncDisposable
{
    private IJSObjectReference? module;
    private bool importAttempted;
    private IJSObjectReference? handle;
    private DotNetObjectReference<PlotWidthObserver>? self;
    private string? observedId;

    public async Task SyncAsync(ElementReference? plot)
    {
        if (plot?.Id == observedId)
            return;

        await DetachAsync();
        if (plot is not { } element)
            return;

        if (!importAttempted)
        {
            importAttempted = true;
            module = await js.InvokeAsync<IJSObjectReference>("import", "./js/plot-width.js");
        }
        if (module is null)
            return;

        self ??= DotNetObjectReference.Create(this);
        handle = await module.InvokeAsync<IJSObjectReference>("observe", element, self);
        observedId = element.Id;
    }

    [JSInvokable]
    public Task OnPlotWidth(double width) => width > 0 ? onWidth(width) : Task.CompletedTask;

    private async Task DetachAsync()
    {
        observedId = null;
        if (handle is null)
            return;

        var current = handle;
        handle = null;
        await current.InvokeVoidAsync("disconnect");
        await current.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DetachAsync();
            if (module is not null)
                await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The runtime is already gone — nothing left to disconnect.
        }

        self?.Dispose();
    }
}
