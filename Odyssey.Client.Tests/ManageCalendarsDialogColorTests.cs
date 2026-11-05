using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The calendar colour works like a tag colour in the Manage calendars dialog: the tag colour picker
/// without No color, a Custom hex saved canonical, and an empty Custom hex that blocks the save with
/// the picker's own message rather than sending anything.
/// </summary>
public class ManageCalendarsDialogColorTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();
    private readonly Mock<ICalendarApiClient> calendars = new();

    public ManageCalendarsDialogColorTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        calendars.Setup(c => c.CreateCalendarAsync(It.IsAny<NewCalendar>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        ctx.Services.AddSingleton(calendars.Object);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    private IRenderedComponent<DialogHost> OpenNewCalendar()
    {
        var cut = ctx.Render<DialogHost>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("New calendar", StringComparison.Ordinal)).Click();
        cut.Find("input:not([type=color])").Input("Hobby");
        return cut;
    }

    private static AngleSharp.Dom.IElement CreateButton(IRenderedComponent<DialogHost> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains("Create calendar", StringComparison.Ordinal));

    [Fact]
    public void The_picker_has_no_No_color_cell_and_opens_on_the_default()
    {
        var cut = OpenNewCalendar();

        var cells = cut.FindAll("#cal-color-new [role=radio]");
        Assert.Equal(ColorPalette.All.Count + 1, cells.Count);
        Assert.DoesNotContain(cells, c => c.GetAttribute("aria-label") == "No color");
        Assert.Equal(ColorPalette.All[0].Name, cut.Find("#cal-color-new [aria-checked=true]").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_custom_hex_is_saved_canonical()
    {
        var cut = OpenNewCalendar();

        cut.FindAll("#cal-color-new [role=radio]")[^1].Click();
        cut.Find("#cal-color-new-hex").Input("#c2185b");
        CreateButton(cut).Click();

        cut.WaitForAssertion(() => calendars.Verify(c => c.CreateCalendarAsync(
            It.Is<NewCalendar>(n => n.Name == "Hobby" && n.Color == "#C2185B"), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void An_empty_custom_hex_blocks_the_save_with_the_pickers_message()
    {
        var cut = OpenNewCalendar();

        cut.FindAll("#cal-color-new [role=radio]")[^1].Click();
        cut.Find("#cal-color-new-hex").Input(string.Empty);
        CreateButton(cut).Click();

        cut.WaitForAssertion(() => Assert.Contains(OdsTagColorPicker.EmptyMessage, cut.Markup, StringComparison.Ordinal));
        calendars.Verify(c => c.CreateCalendarAsync(It.IsAny<NewCalendar>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public sealed class DialogHost : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<ManageCalendarsDialog>(2);
            builder.AddComponentParameter(3, nameof(ManageCalendarsDialog.Open), true);
            builder.AddComponentParameter(4, nameof(ManageCalendarsDialog.Calendars), (IReadOnlyList<ExistingCalendar>)[]);
            builder.CloseComponent();
        }
    }
}
