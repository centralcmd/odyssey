using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsModal.Dismissible"/> (issue #509 §4.1, v4 §6, AC 30): with it <c>false</c>, Escape and the scrim do
/// nothing and the close button is gone; switched back to <c>true</c> while open, Escape closes again. Escape is driven
/// through MudBlazor's own handler (the key interceptor's target), and the scrim through a real click on the overlay,
/// so the test sees what MudDialog itself decides rather than OdsModal's options alone.
/// </summary>
public class ModalDismissibleTests
{
    static ModalDismissibleTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static Task PressEscape(IRenderedComponent<ModalHost> cut)
    {
        var container = cut.FindComponent<MudDialogContainer>().Instance;
        var handler = typeof(MudDialogContainer).GetMethod("HandleEscapeAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?? throw new InvalidOperationException("MudDialogContainer no longer has HandleEscapeAsync; update this test's Escape seam.");
        return cut.InvokeAsync(() => (Task)handler.Invoke(container, null)!);
    }

    private static bool EscapeCloses(IRenderedComponent<ModalHost> cut) =>
        ((IMudDialogInstance)cut.FindComponent<MudDialogContainer>().Instance).Options.CloseOnEscapeKey == true;

    [Fact] // AC 30: existing dialogs are unchanged
    public async Task By_default_escape_closes_and_the_close_button_is_shown()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<ModalHost>(p => p.Add(h => h.Dismissible, true));

        Assert.Single(cut.FindAll(".ods-modal-x"));
        Assert.True(EscapeCloses(cut));

        await PressEscape(cut);

        cut.WaitForAssertion(() => Assert.Equal(1, cut.Instance.Closed));
        Assert.False(cut.Instance.Open);
    }

    [Fact] // AC 30
    public async Task Not_dismissible_blocks_escape_the_scrim_and_drops_the_close_button()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<ModalHost>(p => p.Add(h => h.Dismissible, false));

        Assert.Empty(cut.FindAll(".ods-modal-x"));
        Assert.False(EscapeCloses(cut));
        Assert.False(((IMudDialogInstance)cut.FindComponent<MudDialogContainer>().Instance).Options.BackdropClick);

        await PressEscape(cut);
        cut.Find(".mud-overlay").Click();

        Assert.Equal(0, cut.Instance.Closed);
        Assert.True(cut.Instance.Open);
        Assert.Single(cut.FindAll(".ods-modal-body-probe"));
    }

    [Fact] // AC 30 (v4 §6): toggled while open, true → false → true
    public async Task A_change_while_open_is_applied_to_the_open_dialog()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<ModalHost>(p => p.Add(h => h.Dismissible, true));
        Assert.True(EscapeCloses(cut));

        cut.Render(p => p.Add(h => h.Dismissible, false));

        cut.WaitForAssertion(() => Assert.False(EscapeCloses(cut)));
        Assert.Empty(cut.FindAll(".ods-modal-x"));
        await PressEscape(cut);
        cut.Find(".mud-overlay").Click();
        Assert.Equal(0, cut.Instance.Closed);

        cut.Render(p => p.Add(h => h.Dismissible, true));

        cut.WaitForAssertion(() => Assert.True(EscapeCloses(cut)));
        Assert.Single(cut.FindAll(".ods-modal-x"));
        await PressEscape(cut);
        cut.WaitForAssertion(() => Assert.Equal(1, cut.Instance.Closed));
    }

    /// <summary>An open modal with an <c>OnClose</c>, beside the provider its MudDialog teleports into.</summary>
    public sealed class ModalHost : ComponentBase
    {
        [Parameter] public bool Dismissible { get; set; } = true;

        public bool Open { get; private set; } = true;

        public int Closed { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<OdsModal>(2);
            builder.AddComponentParameter(3, nameof(OdsModal.Open), Open);
            builder.AddComponentParameter(4, nameof(OdsModal.OpenChanged), EventCallback.Factory.Create<bool>(this, v => Open = v));
            builder.AddComponentParameter(5, nameof(OdsModal.OnClose), EventCallback.Factory.Create(this, () => Closed++));
            builder.AddComponentParameter(6, nameof(OdsModal.Dismissible), Dismissible);
            builder.AddComponentParameter(7, nameof(OdsModal.Title), (RenderFragment)(b => b.AddContent(0, "Import feeds")));
            builder.AddComponentParameter(8, nameof(OdsModal.ChildContent), (RenderFragment)(b =>
            {
                b.OpenElement(0, "p");
                b.AddAttribute(1, "class", "ods-modal-body-probe");
                b.AddContent(2, "Body");
                b.CloseElement();
            }));
            builder.CloseComponent();
        }
    }
}
