using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client half of issue #279 (design system · <c>docs/components-transaction-tag-icons.md</c>): the
/// icon picker, the avatar glyph, the transaction row's icon, the tag-admin write body and the shared
/// transaction-tag option.
/// </summary>
public class TransactionTagIconSurfaceTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    public TransactionTagIconSurfaceTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    // ── OdsTagIconPicker ──────────────────────────────────────────────────────

    private IRenderedComponent<OdsTagIconPicker> Picker(string? value, Action<string?>? changed = null) =>
        ctx.Render<OdsTagIconPicker>(p => p
            .Add(c => c.Value, value)
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed?.Invoke(v))));

    [Fact]
    public void Picker_offers_Default_then_the_whole_catalogue_in_order()
    {
        var cut = Picker(null);

        var cells = cut.FindAll("[role=radio]");
        Assert.Equal(TransactionTagIcons.All.Count + 1, cells.Count);
        Assert.Equal("Default (tag icon)", cells[0].GetAttribute("aria-label"));
        Assert.Equal(TransactionTagIcons.Default, cells[0].QuerySelector(".material-icons")!.TextContent);
        Assert.Equal(
            TransactionTagIcons.All.Select(o => o.Label),
            cells.Skip(1).Select(c => c.GetAttribute("aria-label")));
        Assert.Equal(
            TransactionTagIcons.All.Select(o => o.Key),
            cells.Skip(1).Select(c => c.QuerySelector(".material-icons")!.TextContent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("retired_key")]
    [InlineData("local_offer")]
    public void Null_unknown_and_the_default_key_all_select_Default(string? value)
    {
        var cut = Picker(value);

        var cells = cut.FindAll("[role=radio]");
        Assert.Equal("true", cells[0].GetAttribute("aria-checked"));
        Assert.Equal("0", cells[0].GetAttribute("tabindex"));
        Assert.Single(cells, c => c.GetAttribute("aria-checked") == "true");
        Assert.Single(cells, c => c.GetAttribute("tabindex") == "0");
        Assert.Contains("the tag icon", cut.Find(".odc-iconpick-sel").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_known_value_is_the_one_checked_cell_and_the_one_tab_stop_and_is_named_in_words()
    {
        var cut = Picker("restaurant");

        var selected = cut.Find("[aria-checked=true]");
        Assert.Equal("Dining", selected.GetAttribute("aria-label"));
        Assert.Equal("0", selected.GetAttribute("tabindex"));
        Assert.Contains("selected", selected.ClassName, StringComparison.Ordinal);
        Assert.NotNull(selected.QuerySelector(".odc-iconpick-check"));
        Assert.Contains("Dining", cut.Find(".odc-iconpick-sel").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Clicking_a_cell_emits_its_key_and_Default_emits_null()
    {
        var emitted = new List<string?>();
        var cut = Picker("restaurant", emitted.Add);

        cut.Find("[aria-label=Bills]").Click();
        cut.Find("[aria-label='Default (tag icon)']").Click();

        Assert.Equal(["receipt_long", null], emitted);
    }

    [Fact]
    public void Arrow_keys_Home_and_End_move_and_select_without_wrapping()
    {
        var emitted = new List<string?>();
        var cut = Picker(null, emitted.Add);

        // Each key goes to the cell holding the roving tab stop, as a real keyboard user's would.
        void Press(string key) => cut.Find("[role=radio][tabindex='0']").KeyDown(new KeyboardEventArgs { Key = key });

        Press("ArrowLeft");   // clamps at the start
        Press("ArrowRight");
        Press("ArrowDown");   // one row of the fallback column count
        Press("End");
        Press("Home");
        Press("Tab");         // not the grid's key

        Assert.Equal(
            [null, TransactionTagIcons.All[0].Key, TransactionTagIcons.All[OdsTagIconPicker.FallbackColumns].Key,
             TransactionTagIcons.All[^1].Key, null],
            emitted);
    }

    [Fact]
    public void Hovering_a_cell_previews_its_label_in_the_caption()
    {
        var cut = Picker(null);

        cut.Find("[aria-label=Fuel]").MouseEnter();

        Assert.Contains("Fuel", cut.Find(".odc-iconpick-peek").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_picker_emits_nothing()
    {
        var emitted = new List<string?>();
        var cut = ctx.Render<OdsTagIconPicker>(p => p
            .Add(c => c.Disabled, true)
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<string?>(this, emitted.Add)));

        cut.Find("[aria-label=Bills]").Click();

        Assert.Empty(emitted);
    }

    [Fact]
    public void A_visible_label_replaces_the_aria_label()
    {
        var cut = ctx.Render<OdsTagIconPicker>(p => p
            .Add(c => c.AriaLabelledBy, "tag-icon-label")
            .Add(c => c.AriaDescribedBy, "tag-icon-help"));

        var group = cut.Find("[role=radiogroup]");
        Assert.Equal("tag-icon-label", group.GetAttribute("aria-labelledby"));
        Assert.Null(group.GetAttribute("aria-label"));
        Assert.Equal("tag-icon-help", group.GetAttribute("aria-describedby"));
    }

    [Fact]
    public void An_invalid_picker_marks_the_group_aria_invalid()
    {
        var valid = ctx.Render<OdsTagIconPicker>();
        var invalid = ctx.Render<OdsTagIconPicker>(p => p.Add(c => c.Invalid, true));

        Assert.Null(valid.Find("[role=radiogroup]").GetAttribute("aria-invalid"));
        Assert.Equal("true", invalid.Find("[role=radiogroup]").GetAttribute("aria-invalid"));
    }

    // ── OdsAvatar ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A ligature is text in a material-icons span. Through <c>MudIcon</c> it landed on the class
    /// attribute and every ligature avatar — the tag table's, the transaction rows' — drew nothing.
    /// </summary>
    [Fact]
    public void Avatar_draws_a_ligature_as_text()
    {
        var cut = ctx.Render<OdsAvatar>(p => p.Add(c => c.Icon, "restaurant"));

        Assert.Equal("restaurant", cut.Find(".material-icons").TextContent);
        Assert.Empty(cut.FindAll(".mud-icon-root"));
    }

    [Fact]
    public void Avatar_still_draws_an_svg_constant_through_MudIcon()
    {
        var cut = ctx.Render<OdsAvatar>(p => p.Add(c => c.Icon, Icons.Material.Filled.LocalOffer));

        Assert.NotEmpty(cut.FindAll("svg"));
        Assert.Empty(cut.FindAll(".material-icons"));
    }

    // ── OdsTxnTable leading avatar ────────────────────────────────────────────

    private static ExistingTransaction Txn(decimal amount, string displayIcon) => new()
    {
        TransactionId = Guid.NewGuid(),
        Description = "Row",
        Amount = amount,
        TimeStamp = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        AccountId = Guid.NewGuid(),
        DisplayIcon = displayIcon,
    };

    [Theory]
    [InlineData("restaurant", "restaurant")]
    [InlineData("local_offer", "local_offer")]
    [InlineData("future_key_from_a_newer_server", "local_offer")]
    public void Transaction_row_draws_the_display_icon(string displayIcon, string expected)
    {
        var cut = ctx.Render<OdsTxnTable>(p => p.Add(c => c.Rows, [Txn(-10, displayIcon)]));

        var avatar = cut.Find(".odc-avatar");
        Assert.Equal("true", avatar.GetAttribute("aria-hidden"));
        Assert.Equal(expected, avatar.QuerySelector(".material-icons")!.TextContent);
    }

    [Fact]
    public void Transaction_row_tone_still_encodes_direction()
    {
        var cut = ctx.Render<OdsTxnTable>(p => p.Add(c => c.Rows, [Txn(10, "payments"), Txn(-10, "payments")]));

        var styles = cut.FindAll(".odc-avatar").Select(a => a.GetAttribute("style") ?? "").ToList();
        Assert.Contains("--finance-income", styles[0], StringComparison.Ordinal);
        Assert.Contains("--finance-expense", styles[1], StringComparison.Ordinal);
    }

    // ── OdsTagAdmin write body ────────────────────────────────────────────────

    [Fact]
    public void A_family_with_icons_always_carries_the_normalised_icon()
    {
        static TagWrite Write(string? icon) =>
            OdsTagAdmin<ExistingTransactionTag>.WriteFor(hasIcons: true, "Rent", null, archived: true, icon);

        Assert.Equal(new TagWrite("Rent", null, true, "home"), Write("home"));
        Assert.Null(Write("retired_key").Icon);
        Assert.Null(Write(TransactionTagIcons.Default).Icon);
        Assert.Null(Write(null).Icon);
    }

    [Fact]
    public void A_family_without_icons_never_carries_one()
    {
        Assert.Null(OdsTagAdmin<ExistingJournalTag>.WriteFor(hasIcons: false, "Trip", null, archived: false, "home").Icon);
    }

    /// <summary>
    /// The archive / restore row action and the dialog both build their PUT through
    /// <see cref="OdsTagAdmin{TRow}.WriteFor"/>, so neither can drop the icon. A <c>new TagWrite(</c>
    /// elsewhere in the component would be a write path that can.
    /// </summary>
    [Fact]
    public void Every_tag_admin_write_goes_through_WriteFor()
    {
        var path = Path.Combine(ClientSource.Root, "Components", "OdsTagAdmin.razor.cs");
        var code = File.ReadAllText(path);

        Assert.DoesNotMatch(@"new\s+TagWrite\s*\(", code);
        // The two declarations, the instance overload's delegation, and the two writers: the dialog's
        // save and the archive / restore action.
        Assert.Equal(5, Regex.Matches(code, @"\bWriteFor\s*\(").Count);
    }

    [Fact]
    public void The_transaction_tag_page_switches_icons_on()
    {
        var page = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "TransactionTagsPage.razor"));

        Assert.Contains("IconOf=\"@(t => t.Icon)\"", page, StringComparison.Ordinal);
    }

    // ── OdsTransactionTagOptions ──────────────────────────────────────────────

    [Theory]
    [InlineData("restaurant", "restaurant")]
    [InlineData(null, "local_offer")]
    [InlineData("retired_key", "local_offer")]
    public void A_tag_option_carries_the_tag_glyph(string? icon, string expected)
    {
        var id = Guid.NewGuid();
        var option = OdsTransactionTagOptions.From(new ExistingTransactionTag
        {
            TransactionTagId = id, Name = "Food", Archived = null, Icon = icon,
        });

        Assert.Equal(id.ToString(), option.Value);
        Assert.Equal("Food", option.Label);
        Assert.Equal(expected, option.Icon);
    }

    /// <summary>
    /// Every transaction-tag picker and filter builds its options through
    /// <see cref="OdsTransactionTagOptions"/> (DS <c>OdysseyData.tagOption</c>). A surface that projects
    /// a tag by hand would silently drop the icon.
    /// </summary>
    [Fact]
    public void No_transaction_tag_surface_projects_an_option_by_hand()
    {
        var files = ClientSource.SourceFiles().ToList();
        Assert.True(files.Count > 100, "The scan set is unexpectedly small; the lint would prove nothing.");

        // Tax-statement derivation tags are keyed by their own catalogue and are unchanged (DS §4).
        string[] exempt = ["TaxStatementsCard.razor.cs", "CreateTaxStatementDialog.razor.cs"];
        var offenders = files
            .Where(f => !exempt.Contains(Path.GetFileName(f)))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"new OdsOption\([^)]*TransactionTagId\.ToString\(\)"))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.Empty(offenders);
    }
}
