using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsModal"/> against the design system's revised Modal (version 1795477868-c9a2): focus is set up once
/// per opening, never on a re-render (typing in a field must never move focus), and with one dialog opened over
/// another only the TOP one answers Escape and traps Tab.
/// </summary>
/// <remarks>
/// The design system keeps a shared dialog stack (<c>window.__odcDialogStack</c>) because its Modal listens on
/// <c>document</c>. MudBlazor does not need one: each dialog's key interceptor is connected to that dialog's OWN
/// container element (<c>mudKeyInterceptor.connect(ref, elementId, …)</c> listens on that element), and stacked
/// dialogs are sibling containers in the provider, so a key pressed in the dialog on top bubbles through its own
/// container and never through the one beneath. The focus trap likewise lives inside each container. These tests
/// pin that structure and that Escape on the top container closes only the top dialog, so no client-side stack is
/// added.
/// </remarks>
public class ModalStackTests
{
    static ModalStackTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static Task PressEscape(IRenderedComponent<StackHost> cut, MudDialogContainer container)
    {
        var handler = typeof(MudDialogContainer).GetMethod("HandleEscapeAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?? throw new InvalidOperationException("MudDialogContainer no longer has HandleEscapeAsync; update this test's Escape seam.");
        return cut.InvokeAsync(() => (Task)handler.Invoke(container, null)!);
    }

    [Fact]
    public async Task Each_open_dialog_binds_its_key_interceptor_to_its_own_container()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StackHost>(p => p.Add(h => h.InnerOpen, true));

        var containers = cut.FindAll(".mud-dialog-container");
        Assert.Equal(2, containers.Count);
        // Siblings, not nested: a key inside the top container cannot bubble through the other.
        Assert.False(containers[0].Contains(containers[1]));
        Assert.False(containers[1].Contains(containers[0]));

        var connected = ctx.JSInterop.Invocations
            .Where(i => i.Identifier.Contains("KeyInterceptor", StringComparison.OrdinalIgnoreCase) && i.Identifier.EndsWith("connect", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Arguments.OfType<string>().First())
            .Distinct()
            .ToList();
        Assert.Equal(2, connected.Count);
        Assert.All(connected, id => Assert.Single(cut.FindAll($"[id='{id}']")));
        Assert.All(connected, id => Assert.Contains(containers, c => c.Id == id || c.QuerySelector($"[id='{id}']") is not null));
    }

    [Fact]
    public async Task Escape_in_the_dialog_on_top_closes_only_that_dialog()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StackHost>(p => p.Add(h => h.InnerOpen, true));

        var containers = cut.FindComponents<MudDialogContainer>();
        Assert.Equal(2, containers.Count);
        await PressEscape(cut, containers[^1].Instance);

        cut.WaitForAssertion(() => Assert.False(cut.Instance.InnerOpen));
        Assert.True(cut.Instance.OuterOpen);
        Assert.Equal(1, cut.Instance.InnerClosed);
        Assert.Equal(0, cut.Instance.OuterClosed);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mud-dialog-container")));
        Assert.Single(cut.FindAll(".outer-probe"));
    }

    [Fact]
    public async Task Focus_is_set_up_once_per_opening_not_on_every_render()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StackHost>(p => p.Add(h => h.InnerOpen, false));
        int FocusCalls() => ctx.JSInterop.Invocations.Count(i => i.Identifier == "odsFocusFirstField");
        cut.WaitForAssertion(() => Assert.Equal(1, FocusCalls()));

        // A host re-render while open — what every keystroke in a bound field causes.
        for (var i = 0; i < 3; i++)
        {
            cut.Render(p => p.Add(h => h.Typed, $"abc{i}"));
        }

        Assert.Equal(1, FocusCalls());
        Assert.Equal(1, ctx.JSInterop.Invocations.Count(i => i.Identifier == "odsCaptureFocusOwner"));
    }

    /// <summary>An outer modal, and an inner one opened over it.</summary>
    public sealed class StackHost : ComponentBase
    {
        [Parameter] public bool InnerOpen { get; set; }

        [Parameter] public string Typed { get; set; } = string.Empty;

        public bool OuterOpen { get; private set; } = true;

        public int OuterClosed { get; private set; }

        public int InnerClosed { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            Modal(builder, 10, OuterOpen, v => OuterOpen = v, () => OuterClosed++, "Outer", "outer-probe");
            Modal(builder, 30, InnerOpen, v => InnerOpen = v, () => InnerClosed++, "Inner", "inner-probe");
        }

        private void Modal(RenderTreeBuilder builder, int seq, bool open, Action<bool> set, Action closed, string title, string probe)
        {
            builder.OpenComponent<OdsModal>(seq);
            builder.AddComponentParameter(seq + 1, nameof(OdsModal.Open), open);
            builder.AddComponentParameter(seq + 2, nameof(OdsModal.OpenChanged), EventCallback.Factory.Create<bool>(this, set));
            builder.AddComponentParameter(seq + 3, nameof(OdsModal.OnClose), EventCallback.Factory.Create(this, closed));
            builder.AddComponentParameter(seq + 4, nameof(OdsModal.Title), (RenderFragment)(b => b.AddContent(0, title)));
            builder.AddComponentParameter(seq + 5, nameof(OdsModal.ChildContent), (RenderFragment)(b =>
            {
                b.OpenElement(0, "p");
                b.AddAttribute(1, "class", probe);
                b.AddContent(2, Typed);
                b.CloseElement();
            }));
            builder.CloseComponent();
        }
    }
}
