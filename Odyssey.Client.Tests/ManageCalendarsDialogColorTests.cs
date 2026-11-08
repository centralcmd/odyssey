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
    public void The_chosen_icon_is_saved_and_the_default_cell_sends_null()
    {
        var cut = OpenNewCalendar();

        Assert.Equal("Default (calendar icon)", cut.Find("#cal-icon-new [aria-checked=true]").GetAttribute("aria-label"));
        cut.FindAll("#cal-icon-new [role=radio]").Single(c => c.GetAttribute("aria-label") == TagIcons.LabelFor("work")).Click();
        CreateButton(cut).Click();

        cut.WaitForAssertion(() => calendars.Verify(c => c.CreateCalendarAsync(
            It.Is<NewCalendar>(n => n.Name == "Hobby" && n.Icon == "work"), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void A_row_leads_with_its_calendars_own_icon_in_its_colour()
    {
        var cut = ctx.Render<DialogHost>(p => p.Add(d => d.Calendars,
        [
            new ExistingCalendar { CalendarId = Guid.NewGuid(), Name = "Work", Color = "#0369A1", Icon = "work" },
            new ExistingCalendar { CalendarId = Guid.NewGuid(), Name = "Home", Color = "#15803D" },
        ]));

        var glyphs = cut.FindAll(".cal-mgr-glyph");
        Assert.Equal(["work", CalendarIcons.Default], glyphs.Select(g => g.TextContent.Trim()));
        Assert.Equal("color:oklch(from #0369A1 var(--user-glyph-l, l) c h)", glyphs[0].GetAttribute("style"));
    }

    [Fact]
    public void Every_calendar_option_carries_its_icon_and_colour()
    {
        var option = CalendarVisuals.Option(new ExistingCalendar { CalendarId = Guid.NewGuid(), Name = "Bills", Color = "not a colour", Icon = "work" });

        Assert.Equal("work", option.Icon);
        Assert.Equal(Odyssey.Client.Theme.UserColor.Glyph(ColorPalette.CalendarDefault), option.IconColor);
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

    [Theory]
    [InlineData("#0369A1", "Blue")]
    [InlineData("#0369a1", "Blue")]
    [InlineData("#c2185b", "Custom #C2185B")]
    [InlineData("not a colour", "Blue")]
    [InlineData(null, "Blue")]
    public void A_row_names_its_colour_by_swatch_or_as_a_custom_hex(string? stored, string expected) =>
        Assert.Equal(expected, ManageCalendarsDialog.ColorName(stored));

    [Fact]
    public void The_new_calendar_affordance_is_the_add_row_and_its_name_field_shows_the_example()
    {
        var cut = ctx.Render<DialogHost>();

        var add = cut.Find("button.odc-add-row");
        Assert.Contains("New calendar", add.TextContent);
        Assert.Contains("Name, an optional description, an icon and a colour.", add.TextContent);

        add.Click();
        Assert.Equal("e.g. Personal", cut.Find(".cal-mgr-editor-grid input").GetAttribute("placeholder"));
        Assert.NotNull(cut.Find(".cal-mgr-add-open > .cal-mgr-editor-actions"));
    }

    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public IReadOnlyList<ExistingCalendar> Calendars { get; set; } = [];

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<ManageCalendarsDialog>(2);
            builder.AddComponentParameter(3, nameof(ManageCalendarsDialog.Open), true);
            builder.AddComponentParameter(4, nameof(ManageCalendarsDialog.Calendars), Calendars);
            builder.CloseComponent();
        }
    }
}
