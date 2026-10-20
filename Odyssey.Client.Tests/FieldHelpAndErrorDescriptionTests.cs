using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A field keeps its help visible beside its error, and its control is described by both
/// (Odyssey Design System · FieldShell). <see cref="OdsFieldShell"/> renders the two lines, the error
/// on <c>{id}-help-error</c> when help is present; a wrapper that owns its input must list both ids,
/// or the error stays on screen but out of what a screen reader announces for the control.
/// </summary>
public class FieldHelpAndErrorDescriptionTests
{
    private const string HelpText = "Help text";
    private const string ErrorText = "Error text";

    public static TheoryData<string> Fields() => ["amount", "note", "type", "reference"];

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task Help_and_error_both_render_and_both_describe_the_control(string field)
    {
        await using var ctx = NewContext();
        var cut = Render(ctx, field, help: HelpText, error: ErrorText);

        Assert.Equal([HelpText, ErrorText], Lines(cut));
        Assert.Equal([HelpText, ErrorText], Described(cut));
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task An_error_alone_describes_the_control_on_the_help_id(string field)
    {
        await using var ctx = NewContext();
        var cut = Render(ctx, field, help: field == "reference" ? "" : null, error: ErrorText);

        Assert.Equal([ErrorText], Lines(cut));
        Assert.Equal([ErrorText], Described(cut));
    }

    [Fact]
    public async Task A_coordinate_error_is_styled_and_announced_as_an_error()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsCoordinateField>(p => p.Add(c => c.Help, HelpText).Add(c => c.Error, ErrorText));

        var line = cut.Find(".odc-field-help.error");
        Assert.Equal("alert", line.GetAttribute("role"));
        Assert.Equal(ErrorText, line.TextContent.Trim());
    }

    [Fact]
    public async Task A_coordinate_help_line_is_not_an_alert()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsCoordinateField>(p => p.Add(c => c.Help, HelpText));

        var line = cut.Find(".odc-field-help");
        Assert.False(line.ClassList.Contains("error"));
        Assert.Null(line.GetAttribute("role"));
    }

    private static IRenderedComponent<IComponent> Render(BunitContext ctx, string field, string? help, string error) =>
        field switch
        {
            "amount" => As(ctx.Render<OdsAmountField>(p => p.Add(c => c.Label, "Amount").Add(c => c.Help, help).Add(c => c.Error, error))),
            "note" => As(ctx.Render<OdsNoteField>(p => p.Add(c => c.Label, "Note").Add(c => c.Help, help).Add(c => c.Error, error))),
            "type" => As(ctx.Render<OdsTypeSelect>(p => p
                .Add(c => c.Label, "Type")
                .Add(c => c.Types, [new OdsTypeOption { Key = "A", Label = "A", Icon = "label", Color = "#888888", Soft = "#88888833" }])
                .Add(c => c.Help, help).Add(c => c.Error, error))),
            "reference" => As(ctx.Render<OdsReferenceNumberField>(p => p.Add(c => c.Label, "Reference").Add(c => c.Help, help ?? HelpText).Add(c => c.Error, error))),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

    private static IRenderedComponent<IComponent> As<T>(IRenderedComponent<T> cut) where T : IComponent =>
        (IRenderedComponent<IComponent>)(object)cut;

    private static List<string> Lines(IRenderedComponent<IComponent> cut) =>
        cut.FindAll(".odc-field-help").Select(line => line.TextContent.Trim()).ToList();

    /// <summary>The texts of the elements the control's <c>aria-describedby</c> names, in order; a dangling id fails.</summary>
    private static List<string> Described(IRenderedComponent<IComponent> cut)
    {
        var ids = cut.Find("[aria-describedby]").GetAttribute("aria-describedby")!
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(id => !id.EndsWith("-req", StringComparison.Ordinal));
        return ids.Select(id => cut.Find($"#{id}").TextContent.Trim()).ToList();
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Render<MudPopoverProvider>();
        return ctx;
    }
}
