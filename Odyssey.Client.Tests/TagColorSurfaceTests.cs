using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Theme;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.TestShared;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client half of tag colours (issue #394): the <see cref="UserColor"/> sink, the colour picker, the
/// avatar and chip parameters, the chip components and the tag-admin write body.
/// </summary>
public class TagColorSurfaceTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    public TagColorSurfaceTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    // ── UserColor ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(HexColorNegativeCorpus.TheoryData), MemberType = typeof(HexColorNegativeCorpus))]
    public void Every_corpus_value_yields_no_style_from_every_sink(string value)
    {
        Assert.Null(UserColor.Canonical(value));
        Assert.Null(UserColor.Fill(value));
        Assert.Null(UserColor.Dot(value));
        Assert.Null(UserColor.Swatch(value));
        Assert.Null(UserColor.Glyph(value));
        Assert.Equal("background:#0369A1", UserColor.Background(value, ColorPalette.CalendarDefault));
        Assert.Null(UserColor.Background(value, value));
    }

    [Fact]
    public void A_valid_lower_case_value_comes_out_rebuilt_upper_case()
    {
        Assert.Equal("#77AA33", UserColor.Canonical("#77aa33"));
        Assert.Equal(("#F59E0B", HexColor.DarkForeground), UserColor.Fill("#f59e0b"));
        Assert.Equal("--user-color:#15803D", UserColor.Dot("#15803d"));
        Assert.Equal("--sw:#0369A1;--sw-fg:#FFFFFF", UserColor.Swatch("#0369a1"));
        Assert.Equal("oklch(from #0369A1 var(--user-glyph-l, l) c h)", UserColor.Glyph("#0369a1"));
    }

    [Fact]
    public void A_tag_colour_glyph_survives_the_breakdown_tiles_registry_wrap()
    {
        // The tile wraps every IconColor through OdsGlyphInk.Glyph; a user-colour glyph must pass its
        // shape check (else it paints `inherit` and the tag's colour is lost) and keep its own floor.
        var cut = ctx.Render<OdsBreakdownTile>(p => p.Add(c => c.Rows,
            [new OdsBreakdownRow { Icon = "flight", IconColor = UserColor.Glyph("#0369a1"), Label = "Travel", Count = 1 }]));

        Assert.Equal(
            "color:oklch(from oklch(from #0369A1 var(--user-glyph-l, l) c h) var(--glyph-l, l) c h);",
            cut.Find(".odc-breakdown-row .material-icons").GetAttribute("style"));
    }

    // ── OdsAvatar / OdsChip ───────────────────────────────────────────────────

    [Fact]
    public void The_avatar_fills_with_a_valid_user_colour_and_its_computed_glyph_colour()
    {
        var cut = ctx.Render<OdsAvatar>(p => p.Add(c => c.Icon, "home").Add(c => c.Bg, "var(--tag-soft)").Add(c => c.UserColor, "#f59e0b"));

        var style = cut.Find(".odc-avatar").GetAttribute("style")!;
        Assert.Contains("background:#F59E0B;", style, StringComparison.Ordinal);
        Assert.Contains("color:#0E1525;", style, StringComparison.Ordinal);
        Assert.Contains("user-color", cut.Find(".odc-avatar").ClassList);
    }

    [Theory]
    [MemberData(nameof(HexColorNegativeCorpus.TheoryData), MemberType = typeof(HexColorNegativeCorpus))]
    public void A_corrupt_user_colour_is_ignored_by_the_avatar_and_the_chip(string value)
    {
        var avatar = ctx.Render<OdsAvatar>(p => p.Add(c => c.Icon, "home").Add(c => c.Bg, "var(--tag-soft)").Add(c => c.UserColor, value));
        Assert.Contains("background:var(--tag-soft);", avatar.Find(".odc-avatar").GetAttribute("style")!, StringComparison.Ordinal);
        Assert.DoesNotContain("user-color", avatar.Find(".odc-avatar").ClassList);

        var chip = ctx.Render<OdsChip>(p => p.Add(c => c.UserColorDot, value).AddChildContent("Travel"));
        Assert.Empty(chip.FindAll(".odc-chip-colordot"));
    }

    [Fact]
    public void The_chip_dot_is_decorative_and_leaves_the_chip_unchanged()
    {
        var cut = ctx.Render<OdsChip>(p => p.Add(c => c.Tone, OdsChipTone.Tag).Add(c => c.UserColorDot, "#15803d").AddChildContent("Travel"));

        var dot = cut.Find(".odc-chip-colordot");
        Assert.Equal("true", dot.GetAttribute("aria-hidden"));
        Assert.Equal("--user-color:#15803D", dot.GetAttribute("style"));
        Assert.Null(cut.Find(".odc-chip").GetAttribute("style"));
    }

    // ── Chip components ───────────────────────────────────────────────────────

    [Fact]
    public void Transaction_tag_chips_show_a_dot_only_for_a_coloured_tag()
    {
        var cut = ctx.Render<OdsTagChips>(p => p.Add(c => c.Tags,
        [
            new ExistingTransactionTag { TransactionTagId = Guid.NewGuid(), Name = "Food", Archived = null, Color = "#15803D" },
            new ExistingTransactionTag { TransactionTagId = Guid.NewGuid(), Name = "Misc", Archived = null, Color = null },
        ]));

        var chips = cut.FindAll(".odc-chip.tag");
        Assert.Single(chips[0].QuerySelectorAll(".odc-chip-colordot"));
        Assert.Empty(chips[1].QuerySelectorAll(".odc-chip-colordot"));
        Assert.Equal("Food", chips[0].TextContent.Trim());
    }

    [Fact]
    public void Journal_tag_chips_show_a_dot_only_for_a_coloured_tag()
    {
        var cut = ctx.Render<JournalTagChips>(p => p.Add(c => c.Tags,
            [new JournalTagChips.Tag("Trip", "flight", "#0369a1"), new JournalTagChips.Tag("Misc", null, null)]));

        var chips = cut.FindAll(".odc-chip");
        Assert.Equal("--user-color:#0369A1", chips[0].QuerySelector(".odc-chip-colordot")!.GetAttribute("style"));
        Assert.Null(chips[1].QuerySelector(".odc-chip-colordot"));
    }

    // ── OdsTagColorPicker ─────────────────────────────────────────────────────

    private IRenderedComponent<OdsTagColorPicker> Picker(string? value, Action<string?>? changed = null, Action<bool>? valid = null,
        bool showErrors = false, bool disabled = false, bool invalid = false, string? error = null) =>
        ctx.Render<OdsTagColorPicker>(p => p
            .Add(c => c.Value, value)
            .Add(c => c.Error, error)
            .Add(c => c.Id, "tag-color")
            .Add(c => c.ShowErrors, showErrors)
            .Add(c => c.Disabled, disabled)
            .Add(c => c.Invalid, invalid)
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed?.Invoke(v)))
            .Add(c => c.ValidChanged, EventCallback.Factory.Create<bool>(this, v => valid?.Invoke(v))));

    [Fact]
    public void The_picker_is_a_radiogroup_of_word_named_cells_with_one_tab_stop()
    {
        var cut = Picker(null);

        Assert.Equal("radiogroup", cut.Find(".odc-colorpick-grid").GetAttribute("role"));
        var cells = cut.FindAll("[role=radio]");
        Assert.Equal(ColorPalette.All.Count + 2, cells.Count);
        Assert.Equal(["No color", .. ColorPalette.All.Select(s => s.Name), "Custom, no color entered"],
            cells.Select(c => c.GetAttribute("aria-label")));
        Assert.Single(cells, c => c.GetAttribute("tabindex") == "0");
        Assert.Equal("true", cells[0].GetAttribute("aria-checked"));
        Assert.Equal("tag-color-custom-hint", cells[^1].GetAttribute("aria-describedby"));
        Assert.Contains("Selecting this shows a hex color field", cut.Find("#tag-color-custom-hint").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stored_swatch_opens_as_that_swatch_and_any_other_hex_opens_as_Custom()
    {
        var swatch = Picker("#15803d");
        Assert.Equal("Green", swatch.Find("[aria-checked=true]").GetAttribute("aria-label"));
        Assert.Contains("Green", swatch.Find(".odc-colorpick-caption").TextContent, StringComparison.Ordinal);

        var custom = Picker("#c2185b");
        Assert.Equal("Custom, #C2185B", custom.Find("[aria-checked=true]").GetAttribute("aria-label"));
        Assert.Equal("#C2185B", custom.Find("#tag-color-hex").GetAttribute("value"));
    }

    [Fact]
    public void Choosing_Custom_from_a_swatch_seeds_the_field_with_that_swatchs_hex()
    {
        string? emitted = "unset";
        var cut = Picker("#15803D", v => emitted = v);

        cut.FindAll("[role=radio]")[^1].KeyDown(new KeyboardEventArgs { Key = "End" });
        cut.FindAll("[role=radio]")[^1].Click();

        Assert.Equal("#15803D", cut.Find("#tag-color-hex").GetAttribute("value"));
        Assert.Equal("#15803D", emitted);
        Assert.Equal("Custom, #15803D", cut.FindAll("[role=radio]")[^1].GetAttribute("aria-label"));
    }

    [Fact]
    public void Arrowing_from_Sky_onto_Custom_seeds_Skys_hex_so_no_cell_is_left_in_error()
    {
        bool? valid = null;
        var cut = Picker("#7DD3FC", valid: v => valid = v);

        cut.Find("[aria-checked=true]").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });

        Assert.Equal("#7DD3FC", cut.Find("#tag-color-hex").GetAttribute("value"));
        Assert.NotEqual(false, valid);
    }

    [Fact]
    public void Typing_a_pasted_value_trims_and_prefixes_it_and_emits_the_canonical_hex()
    {
        string? emitted = null;
        var cut = Picker("#c2185b", v => emitted = v);

        cut.Find("#tag-color-hex").Input("  77aa33 ");

        Assert.Equal("#77AA33", emitted);
        Assert.Equal("#77aa33", OdsTagColorPicker.FromInput("77aa33"));
        Assert.Equal("#FFF", OdsTagColorPicker.FromInput(" #FFF "));
    }

    [Fact]
    public void An_invalid_value_shows_its_error_as_an_alert_on_blur_and_reports_not_valid()
    {
        bool? valid = null;
        var cut = Picker("#c2185b", valid: v => valid = v);

        cut.Find("#tag-color-hex").Input("#7a3");
        cut.Find("#tag-color-hex").Blur();

        var error = cut.Find(".odc-field-help.error");
        Assert.Equal(HexColor.InvalidMessage, error.TextContent.Trim());
        Assert.Equal("alert", error.GetAttribute("role"));
        Assert.Equal("true", cut.Find("#tag-color-hex").GetAttribute("aria-invalid"));
        Assert.Equal(false, valid);
        Assert.Equal("Custom, not a valid color", cut.FindAll("[role=radio]")[^1].GetAttribute("aria-label"));
    }

    [Fact]
    public void After_submit_an_empty_Custom_shows_its_own_message_without_an_alert_role()
    {
        var cut = Picker("#c2185b", showErrors: true);

        cut.Find("#tag-color-hex").Input(string.Empty);

        var error = cut.Find(".odc-field-help.error");
        Assert.Equal(OdsTagColorPicker.EmptyMessage, error.TextContent.Trim());
        Assert.Null(error.GetAttribute("role"));
    }

    [Fact]
    public void While_saving_the_picker_is_aria_disabled_never_disabled()
    {
        var cut = Picker("#15803D", disabled: true);

        Assert.Equal("true", cut.Find(".odc-colorpick-grid").GetAttribute("aria-disabled"));
        Assert.All(cut.FindAll("[role=radio]"), cell => Assert.False(cell.HasAttribute("disabled")));
    }

    [Fact]
    public void While_saving_the_custom_controls_keep_focus_rather_than_becoming_disabled()
    {
        var cut = Picker("#C2185B", disabled: true);

        var hex = cut.Find("#tag-color-hex");
        Assert.False(hex.HasAttribute("disabled"));
        Assert.True(hex.HasAttribute("readonly"));
        Assert.Equal("true", hex.GetAttribute("aria-disabled"));
        var well = cut.Find("input[type=color]");
        Assert.False(well.HasAttribute("disabled"));
        Assert.Equal("true", well.GetAttribute("aria-disabled"));
    }

    [Fact]
    public void A_server_400_with_a_swatch_selected_marks_the_group_invalid()
    {
        var cut = Picker("#15803D", invalid: true);

        Assert.Equal("true", cut.Find(".odc-colorpick-grid").GetAttribute("aria-invalid"));
    }

    [Fact]
    public void A_server_400_with_a_swatch_selected_shows_its_message_as_text_and_describes_the_group()
    {
        var cut = Picker("#15803D", invalid: true, error: HexColor.InvalidMessage);

        var message = cut.Find("#tag-color-error");
        Assert.Equal(HexColor.InvalidMessage, message.TextContent.Trim());
        Assert.Contains("tag-color-error", cut.Find(".odc-colorpick-grid").GetAttribute("aria-describedby")!.Split(' '));
    }

    [Fact]
    public void Picker_cells_are_painted_only_through_UserColor() =>
        Assert.All(Picker(null).FindAll("[role=radio]").Skip(1).Take(ColorPalette.All.Count),
            (cell, i) => Assert.Equal(UserColor.Swatch(ColorPalette.All[i].Hex), cell.GetAttribute("style")));

    // ── OdsTagAdmin write body ────────────────────────────────────────────────

    [Fact]
    public void Every_tag_write_carries_the_colour_canonicalised()
    {
        Assert.Equal("#15803D", OdsTagAdmin<ExistingJournalTag>.WriteFor(true, "Trip", null, archived: true, "flight", "#15803d").Color);
        Assert.Null(OdsTagAdmin<ExistingJournalTag>.WriteFor(true, "Trip", null, archived: false, null, null).Color);
    }

    [Fact]
    public void The_tag_admin_requires_ColorOf_and_the_archive_action_passes_it()
    {
        var code = File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "OdsTagAdmin.razor.cs"));

        Assert.Matches(@"\[Parameter, EditorRequired\] public Func<TRow, string\?> ColorOf", code);
        Assert.Contains("color: ColorOf(tag)", code, StringComparison.Ordinal);
        foreach (var page in new[] { "Finance/TransactionTagsPage.razor", "Journal/JournalTagsPage.razor", "Journal/TaskTagsPage.razor", "Photos/PhotoTagsPage.razor" })
            Assert.Contains("ColorOf=\"@(t => t.Color)\"", File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", page)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pages/Photos/EditPhotoDialog.razor.cs")]
    [InlineData("Pages/Finance/FileAnalysisDialog.razor.cs")]
    [InlineData("Services/TagQuickCreate.cs")]
    public void A_create_only_tag_write_states_its_colour_explicitly(string file) =>
        Assert.Contains("Color: null", File.ReadAllText(Path.Combine(ClientSource.Root, file)), StringComparison.Ordinal);

    [Fact]
    public void The_calendar_swatches_declare_no_colour_of_their_own()
    {
        var code = File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "OdsCalendarSwatches.cs"));

        Assert.DoesNotMatch(@"""#[0-9A-Fa-f]{6}""", code);
        Assert.Equal(ColorPalette.All.Select(s => s.Hex), OdsCalendarSwatches.All.Select(s => s.Hex));
        Assert.Equal(ColorPalette.All.Count, OdsCalendarSwatches.TokenByKey.Count);
    }

    // ── Calendars: the tag colour, without No color ─────────────────────────────

    private IRenderedComponent<OdsTagColorPicker> CalendarPicker(string? value) =>
        ctx.Render<OdsTagColorPicker>(p => p
            .Add(c => c.Value, value)
            .Add(c => c.Id, "cal-color")
            .Add(c => c.AllowNone, false));

    [Fact]
    public void A_calendar_picker_offers_the_palette_and_Custom_but_no_No_color_cell()
    {
        var cut = CalendarPicker("#15803D");

        var cells = cut.FindAll("[role=radio]");
        Assert.Equal([.. ColorPalette.All.Select(s => s.Name), "Custom, no color entered"], cells.Select(c => c.GetAttribute("aria-label")));
        Assert.Equal("Green", cut.Find("[aria-checked=true]").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_calendar_picker_opens_a_custom_hex_as_Custom_and_a_missing_one_on_the_default()
    {
        Assert.Equal("Custom, #C2185B", CalendarPicker("#c2185b").Find("[aria-checked=true]").GetAttribute("aria-label"));
        Assert.Equal(ColorPalette.All[0].Name, CalendarPicker(null).Find("[aria-checked=true]").GetAttribute("aria-label"));
    }

    [Theory]
    [InlineData("Home")]
    [InlineData("ArrowLeft")]
    public void Keyboard_never_lands_a_calendar_picker_on_No_color(string key)
    {
        var cut = CalendarPicker(ColorPalette.All[0].Hex);

        cut.Find("[aria-checked=true]").KeyDown(new KeyboardEventArgs { Key = key });

        var expected = key == "Home" ? ColorPalette.All[0].Name : "Custom, " + ColorPalette.All[0].Hex;
        Assert.Equal(expected, cut.Find("[aria-checked=true]").GetAttribute("aria-label"));
    }

    [Fact]
    public void An_event_paints_a_palette_colour_with_its_vetted_foreground_and_a_custom_one_through_UserColor()
    {
        var palette = ColorPalette.All[0];
        Assert.Equal((palette.Hex, palette.Foreground), OdsCalendarSwatches.PaintFor(palette.Hex.ToLowerInvariant()));
        Assert.Equal(UserColor.Fill("#c2185b")!.Value, OdsCalendarSwatches.PaintFor("#c2185b"));
        Assert.Equal((ColorPalette.CalendarDefault, OdsCalendarSwatches.SwatchFor(null).Fg), OdsCalendarSwatches.PaintFor("red;x:y"));
    }
}
