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
///
/// <para>
/// The width has no lower bound on purpose. The plot spans <c>x0 … W − 32</c> with a gutter of at
/// least 64, so below about 96px the plot range inverts; no surface lays a chart out that narrow (the
/// narrowest is the three-up <c>minmax(300px, 1fr)</c> tax overview), and a floor would reintroduce
/// the scaling this exists to remove.
/// </para>
/// </summary>
internal sealed class PlotWidthObserver(IJSRuntime js, Func<double, Task> onWidth) : IAsyncDisposable
{
    private IJSObjectReference? module;
    private bool importAttempted;
    private IJSObjectReference? handle;
    private DotNetObjectReference<PlotWidthObserver>? self;
    private string? observedId;
    private bool disposed;

    public async Task SyncAsync(ElementReference? plot)
    {
        var id = plot?.Id;
        if (id == observedId)
            return;

        // Claimed before any await: observe() measures synchronously, so the first width can arrive,
        // and re-render the chart, while the call below is still in flight. That render's SyncAsync
        // must see this element as already observed, or it would start a second observer and orphan
        // this one.
        observedId = id;
        await DetachAsync();
        if (plot is not { } element)
            return;

        IJSObjectReference? observed;
        try
        {
            if (!importAttempted)
            {
                importAttempted = true;
                module = await js.InvokeAsync<IJSObjectReference>("import", "./js/plot-width.js");
            }
            if (module is null)
                return;

            self ??= DotNetObjectReference.Create(this);
            observed = await module.InvokeAsync<IJSObjectReference>("observe", element, self);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            // Measuring is a refinement, not a dependency: a chart whose bridge fails to load keeps
            // the 1000-unit default box (scaled labels, as before) rather than raising the app's
            // global error bar from OnAfterRenderAsync.
            return;
        }

        // Superseded while awaiting (the plot was swapped or removed): this observer is already stale.
        if (observedId != id || disposed)
        {
            await DisconnectAsync(observed);
            return;
        }

        handle = observed;
    }

    [JSInvokable]
    public Task OnPlotWidth(double width) => width > 0 ? onWidth(width) : Task.CompletedTask;

    private Task DetachAsync()
    {
        var current = handle;
        handle = null;
        return DisconnectAsync(current);
    }

    private static async Task DisconnectAsync(IJSObjectReference? observed)
    {
        if (observed is null)
            return;

        await observed.InvokeVoidAsync("disconnect");
        await observed.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
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
