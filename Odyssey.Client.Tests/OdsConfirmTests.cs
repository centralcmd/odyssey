using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsConfirm"/>: the yes/no prompt twenty delete and detach actions await. Every path that
/// is not the danger button must answer <c>false</c>, or a dismissed prompt would delete.
/// </summary>
public class OdsConfirmTests
{
    private const string Title = "Delete account";
    private const string Body = "Delete 'Main'? This cannot be undone.";

    [Fact]
    public async Task The_danger_button_answers_true_and_closes()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>();
        var answer = await Ask(cut);

        Danger(cut).Click();

        Assert.True(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        cut.WaitForAssertion(() => Assert.False(Dialog(cut).Open));
    }

    [Fact]
    public async Task Cancel_answers_false()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>();
        var answer = await Ask(cut);

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>Esc, the scrim and the close button all reach the dialog as OpenChanged(false).</summary>
    [Fact]
    public async Task Any_other_dismissal_answers_false()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>();
        var answer = await Ask(cut);

        await cut.InvokeAsync(() => Dialog(cut).OpenChanged.InvokeAsync(false));

        Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_second_question_answers_the_first_false()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>();
        var first = await Ask(cut);

        var second = await Ask(cut, "Detach file", "Detach 'deed.pdf'?", "Detach", "link_off");

        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(second.IsCompleted);
        Assert.Equal("Detach file", Dialog(cut).Title);
    }

    [Fact]
    public async Task Disposing_answers_an_open_question_false()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>();
        var answer = await Ask(cut);

        await cut.InvokeAsync(() => cut.Instance.Confirm.Dispose());

        Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_prompt_shows_the_question_and_reads_as_a_described_alertdialog()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>();
        await Ask(cut, Title, Body, "Detach", "link_off");

        var dialog = Dialog(cut);
        Assert.Equal(Title, dialog.Title);
        Assert.Equal("Detach", dialog.ConfirmLabel);
        Assert.Equal("link_off", dialog.Icon);
        Assert.Equal("link_off", dialog.SubmitIcon);

        var body = cut.Find(".odc-confirm-body");
        Assert.Equal(Body, body.TextContent);
        var modal = cut.FindComponent<OdsModal>().Instance;
        Assert.Equal(OdsModalRole.AlertDialog, modal.Role);
        Assert.Equal(body.Id, modal.DescribedById);
        Assert.False(string.IsNullOrEmpty(body.Id));
    }

    private static async Task<Task<bool>> Ask(
        IRenderedComponent<Host> cut, string title = Title, string body = Body, string label = "Delete", string icon = "delete")
    {
        Task<bool> answer = null!;
        await cut.InvokeAsync(() => { answer = cut.Instance.Confirm.AskAsync(title, body, label, icon); });
        cut.WaitForAssertion(() => Assert.True(Dialog(cut).Open));
        return answer;
    }

    private static OdsConfirmDialog Dialog(IRenderedComponent<Host> cut) => cut.FindComponent<OdsConfirmDialog>().Instance;

    private static AngleSharp.Dom.IElement Danger(IRenderedComponent<Host> cut) =>
        cut.FindAll(".ods-modal button").Single(b => b.TextContent.Trim().EndsWith(Dialog(cut).ConfirmLabel, StringComparison.Ordinal)
                                                   && b.TextContent.Trim() != "Cancel");

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    /// <summary>OdsConfirm beside MudBlazor's providers, which portal the modal it renders into.</summary>
    public sealed class Host : ComponentBase
    {
        public OdsConfirm Confirm { get; private set; } = default!;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<OdsConfirm>(2);
            builder.AddComponentReferenceCapture(3, r => Confirm = (OdsConfirm)r);
            builder.CloseComponent();
        }
    }
}
