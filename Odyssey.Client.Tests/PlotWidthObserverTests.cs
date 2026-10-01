using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="PlotWidthObserver"/>'s lifecycle against a mocked JS runtime, where each <c>observe</c>
/// call is held open until the test releases it (issue #274). bUnit cannot plan an
/// <see cref="IJSObjectReference"/> result, and the races below only exist while that call is in flight.
/// </summary>
public class PlotWidthObserverTests
{
    private sealed class Bridge
    {
        public Mock<IJSRuntime> Js { get; } = new();
        public Mock<IJSObjectReference> Module { get; } = new();
        public List<TaskCompletionSource<IJSObjectReference>> Pending { get; } = [];
        public List<Mock<IJSObjectReference>> Handles { get; } = [];
        public List<double> Widths { get; } = [];

        /// <summary>Set to hold the module import in flight until the test completes it.</summary>
        public TaskCompletionSource<IJSObjectReference>? HeldImport { get; }

        public Bridge(Exception? importFails = null, bool holdImport = false)
        {
            var import = Js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()));
            if (holdImport)
            {
                HeldImport = new TaskCompletionSource<IJSObjectReference>();
                import.Returns(new ValueTask<IJSObjectReference>(HeldImport.Task));
            }
            else if (importFails is null)
                import.Returns(new ValueTask<IJSObjectReference>(Module.Object));
            else
                import.Returns(ValueTask.FromException<IJSObjectReference>(importFails));

            Module.Setup(m => m.InvokeAsync<IJSObjectReference>("observe", It.IsAny<object?[]?>()))
                .Returns(() =>
                {
                    var pending = new TaskCompletionSource<IJSObjectReference>();
                    Pending.Add(pending);
                    return new ValueTask<IJSObjectReference>(pending.Task);
                });
        }

        public PlotWidthObserver Observer() => new(Js.Object, w => { Widths.Add(w); return Task.CompletedTask; });

        /// <summary>Completes the <paramref name="index"/>th observe call with a fresh handle.</summary>
        public Mock<IJSObjectReference> Release(int index, Exception? onDisconnect = null)
        {
            var handle = new Mock<IJSObjectReference>();
            var disconnect = handle.Setup(h => h.InvokeAsync<IJSVoidResult>("disconnect", It.IsAny<object?[]?>()));
            if (onDisconnect is not null)
                disconnect.Returns(ValueTask.FromException<IJSVoidResult>(onDisconnect));
            Handles.Add(handle);
            Pending[index].SetResult(handle.Object);
            return handle;
        }

        public static int Disconnects(Mock<IJSObjectReference> handle) =>
            handle.Invocations.Count(i => i.Method.Name == nameof(IJSObjectReference.InvokeAsync)
                                          && (string?)i.Arguments[0] == "disconnect");
    }

    private static readonly ElementReference PlotA = new("plot-a");
    private static readonly ElementReference PlotB = new("plot-b");

    /// <summary>
    /// <c>observe()</c> measures synchronously, so the first width re-renders the chart while the call is
    /// still in flight. The re-render's sync for the same plot must not start a second observer.
    /// </summary>
    [Fact]
    public async Task A_sync_during_an_in_flight_observe_does_not_start_a_second_one()
    {
        var bridge = new Bridge();
        var observer = bridge.Observer();

        var first = observer.SyncAsync(PlotA);
        await observer.OnPlotWidth(300);
        await observer.SyncAsync(PlotA);
        var handle = bridge.Release(0);
        await first;

        Assert.Single(bridge.Pending);
        Assert.Equal([300], bridge.Widths);

        await observer.DisposeAsync();
        Assert.Equal(1, Bridge.Disconnects(handle));
    }

    /// <summary>
    /// The plot swaps while the very first module import is still loading. The newer plot must wait on
    /// the same import and be observed — not find no module and stay at the default width — and the
    /// superseded plot must never be observed at all.
    /// </summary>
    [Fact]
    public async Task A_plot_swapped_during_the_first_import_is_observed_once_it_loads()
    {
        var bridge = new Bridge(holdImport: true);
        var observer = bridge.Observer();

        var first = observer.SyncAsync(PlotA);
        var second = observer.SyncAsync(PlotB);
        bridge.HeldImport!.SetResult(bridge.Module.Object);
        await first;
        bridge.Release(0);
        await second;

        var observed = Assert.Single(bridge.Module.Invocations, i => (string?)i.Arguments[0] == "observe");
        var args = Assert.IsType<object?[]>(observed.Arguments[1]);
        Assert.Equal(PlotB.Id, Assert.IsType<ElementReference>(args[0]).Id);
        Assert.Single(bridge.Js.Invocations);
    }

    /// <summary>A plot removed while its observe call is in flight gets that observer disconnected on arrival.</summary>
    [Fact]
    public async Task An_observer_that_arrives_after_its_plot_left_is_disconnected()
    {
        var bridge = new Bridge();
        var observer = bridge.Observer();

        var first = observer.SyncAsync(PlotA);
        await observer.SyncAsync(null);
        var handle = bridge.Release(0);
        await first;

        Assert.Equal(1, Bridge.Disconnects(handle));
    }

    /// <summary>The chart is disposed while its observe call is in flight: the late observer is disconnected.</summary>
    [Fact]
    public async Task An_observer_that_arrives_after_disposal_is_disconnected()
    {
        var bridge = new Bridge();
        var observer = bridge.Observer();

        var first = observer.SyncAsync(PlotA);
        await observer.DisposeAsync();
        var handle = bridge.Release(0, onDisconnect: new JSDisconnectedException("gone"));

        Assert.Null(await Record.ExceptionAsync(() => first));
        Assert.Equal(1, Bridge.Disconnects(handle));
    }

    /// <summary>Data → empty → data: the old observer is disconnected and the new plot observed.</summary>
    [Fact]
    public async Task Swapping_the_plot_disconnects_the_old_observer_and_observes_the_new()
    {
        var bridge = new Bridge();
        var observer = bridge.Observer();

        var first = observer.SyncAsync(PlotA);
        var handleA = bridge.Release(0);
        await first;

        await observer.SyncAsync(null);
        Assert.Equal(1, Bridge.Disconnects(handleA));

        var second = observer.SyncAsync(PlotB);
        var handleB = bridge.Release(1);
        await second;

        Assert.Equal(2, bridge.Pending.Count);
        Assert.Equal(0, Bridge.Disconnects(handleB));
    }

    [Fact]
    public async Task Disposing_tolerates_a_disconnected_runtime()
    {
        var bridge = new Bridge();
        var observer = bridge.Observer();
        var first = observer.SyncAsync(PlotA);
        var handle = bridge.Release(0, onDisconnect: new JSDisconnectedException("gone"));
        await first;

        var thrown = await Record.ExceptionAsync(async () => await observer.DisposeAsync());

        Assert.Null(thrown);
        Assert.Equal(1, Bridge.Disconnects(handle));
    }

    /// <summary>
    /// Measuring is a refinement: a bridge that fails to load is swallowed, so the chart keeps its default
    /// box instead of throwing out of <c>OnAfterRenderAsync</c> into the app's global error bar — and the
    /// import is not retried on every render.
    /// </summary>
    [Fact]
    public async Task A_bridge_that_fails_to_load_is_not_an_error_and_is_not_retried()
    {
        var bridge = new Bridge(importFails: new JSException("blocked"));
        var observer = bridge.Observer();

        Assert.Null(await Record.ExceptionAsync(() => observer.SyncAsync(PlotA)));
        await observer.SyncAsync(null);
        Assert.Null(await Record.ExceptionAsync(() => observer.SyncAsync(PlotB)));

        Assert.Single(bridge.Js.Invocations);
        Assert.Empty(bridge.Pending);
    }

    /// <summary>A zero width (the plot under <c>display:none</c>) never reaches the chart.</summary>
    [Fact]
    public async Task A_zero_width_is_not_forwarded()
    {
        var bridge = new Bridge();

        await bridge.Observer().OnPlotWidth(0);

        Assert.Empty(bridge.Widths);
    }
}
