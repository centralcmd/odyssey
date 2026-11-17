using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>The tag icon picker's search box and its forgiving matcher, <see cref="TagIconSearch"/>.</summary>
public class TagIconSearchTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    public TagIconSearchTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    private static List<string> Keys(string query) => TagIconSearch.Search(query).Select(o => o.Key).ToList();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_query_matches_nothing(string? query) => Assert.Empty(TagIconSearch.Search(query));

    [Theory]
    [InlineData("fuel", "local_gas_station")]      // the label
    [InlineData("petrol", "local_gas_station")]    // a keyword
    [InlineData("vacation", "beach_access")]       // a synonym of Holiday
    [InlineData("palm", "beach_access")]
    [InlineData("vacaton", "beach_access")]        // a typo
    [InlineData("cafe", "local_cafe")]             // diacritics fold
    [InlineData("CABIN", "cabin")]                 // case folds
    [InlineData("hous", "house")]                  // a prefix while typing
    [InlineData("contact", "contacts")]
    [InlineData("gas station", "local_gas_station")] // every word must match
    public void A_query_finds_the_expected_icon(string query, string key) => Assert.Contains(key, Keys(query));

    [Fact]
    public void A_label_hit_ranks_before_a_keyword_hit()
    {
        var keys = Keys("cabin");

        Assert.Equal("cabin", keys[0]);
        Assert.Contains("cottage", keys);
    }

    [Theory]
    [InlineData("cafe", "child_care")]   // "care" is one typo from "cafe", but Café matches outright
    [InlineData("palm", "palette")]
    [InlineData("vacation", "location_city")]
    public void A_typo_match_is_only_a_fallback(string query, string notExpected) =>
        Assert.DoesNotContain(notExpected, Keys(query));

    [Fact]
    public void Every_word_must_match()
    {
        Assert.Empty(TagIconSearch.Search("cabin zzzz"));
    }

    [Fact]
    public void Short_words_are_not_fuzzy()
    {
        // "tx" is no word's prefix or substring, and two letters get no typo budget.
        Assert.Empty(TagIconSearch.Search("tx"));
    }

    [Fact]
    public void Every_catalogue_icon_is_found_by_its_own_label()
    {
        Assert.All(TagIcons.All, option => Assert.Contains(option.Key, Keys(option.Label)));
    }

    [Fact]
    public void Every_catalogue_icon_carries_keywords()
    {
        Assert.All(TagIcons.All, option => Assert.False(string.IsNullOrWhiteSpace(option.Keywords), option.Key));
    }

    [Theory]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "acb", 1)]
    [InlineData("kitten", "sitting", 3)]
    public void Distance_counts_edits_with_a_swap_as_one(string a, string b, int expected) =>
        Assert.Equal(expected, TagIconSearch.Distance(a, b, 5));

    [Fact]
    public void Distance_stops_early_past_the_cap_on_length_alone() =>
        Assert.Equal(2, TagIconSearch.Distance("ab", "abcdef", 1));

    [Fact]
    public void Equal_scores_keep_catalogue_order()
    {
        // Cottage and Chalet both carry "cabin" as a keyword; Cabin itself is the label hit.
        var keys = Keys("cabin");
        var order = TagIcons.All.Select(o => o.Key).ToList();

        Assert.Equal(["cabin", "cottage", "chalet"], keys);
        Assert.True(order.IndexOf("cottage") < order.IndexOf("chalet"));
    }

    // ── The picker ────────────────────────────────────────────────────────────

    private IRenderedComponent<OdsTagIconPicker> Picker(string? value, Action<string?>? changed = null) =>
        ctx.Render<OdsTagIconPicker>(p => p
            .Add(c => c.Value, value)
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed?.Invoke(v))));

    [Fact]
    public void Typing_filters_the_grid_and_hides_Default()
    {
        var cut = Picker(null);

        cut.Find("input[type=search]").Input("cabin");

        var labels = cut.FindAll("[role=radio]").Select(c => c.GetAttribute("aria-label")).ToList();
        Assert.Equal("Cabin", labels[0]);
        Assert.DoesNotContain("Default (tag icon)", labels);
        Assert.DoesNotContain("Groceries", labels);
        Assert.Contains("match", cut.Find("[role=status]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void No_match_says_so_and_announces_it()
    {
        var cut = Picker(null);

        cut.Find("input[type=search]").Input("qqqqqq");

        Assert.Empty(cut.FindAll("[role=radio]"));
        Assert.Contains("qqqqqq", cut.Find(".odc-iconpick-empty").TextContent, StringComparison.Ordinal);
        Assert.Equal("No icons match.", cut.Find("[role=status]").TextContent);
    }

    [Fact]
    public void A_hidden_selection_stays_named_and_the_first_match_takes_the_tab_stop()
    {
        var cut = Picker("cabin");

        cut.Find("input[type=search]").Input("fuel");

        var cells = cut.FindAll("[role=radio]");
        Assert.Equal("0", cells[0].GetAttribute("tabindex"));
        Assert.DoesNotContain(cells, c => c.GetAttribute("aria-checked") == "true");
        Assert.Contains("Cabin", cut.Find(".odc-iconpick-sel").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Clicking_a_match_emits_its_key()
    {
        var emitted = new List<string?>();
        var cut = Picker(null, emitted.Add);

        cut.Find("input[type=search]").Input("palm");
        cut.Find("[aria-label=Holiday]").Click();

        Assert.Equal(["beach_access"], emitted);
    }

    [Fact]
    public void Enter_picks_the_first_match()
    {
        var emitted = new List<string?>();
        var cut = Picker(null, emitted.Add);

        var search = cut.Find("input[type=search]");
        search.Input("petrol");
        search.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(["local_gas_station"], emitted);
    }

    [Fact]
    public void Escape_clears_the_query_and_restores_the_whole_grid()
    {
        var cut = Picker(null);

        var search = cut.Find("input[type=search]");
        search.Input("cabin");
        search.KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Equal(TagIcons.All.Count + 1, cut.FindAll("[role=radio]").Count);
        Assert.Equal("", cut.Find("[role=status]").TextContent);
    }

    // The reference the picker holds for the visible cell at that position (bUnit's markup does not carry it).
    private static string ButtonRef(IRenderedComponent<OdsTagIconPicker> cut, int index) =>
        ((ElementReference[])typeof(OdsTagIconPicker)
            .GetField("_buttons", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cut.Instance)!)[index].Id;

    [Fact]
    public void Arrow_Down_moves_focus_from_the_box_to_the_tab_stop()
    {
        var cut = Picker(null);

        var search = cut.Find("input[type=search]");
        search.Input("cabin");
        search.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        var focused = ctx.JSInterop.VerifyFocusAsyncInvoke();
        Assert.Equal(ButtonRef(cut, 0), ((ElementReference)focused.Arguments[0]!).Id);
    }

    [Fact]
    public void Arrow_Down_with_no_match_focuses_nothing()
    {
        var cut = Picker(null);

        var search = cut.Find("input[type=search]");
        search.Input("qqqqqq");
        search.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("qqqqqq")]
    public void Enter_on_a_blank_or_matchless_query_picks_nothing(string query)
    {
        var emitted = new List<string?>();
        var cut = Picker(null, emitted.Add);

        var search = cut.Find("input[type=search]");
        search.Input(query);
        search.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Empty(emitted);
    }

    [Fact]
    public void Enter_announces_the_icon_it_picked()
    {
        var cut = Picker(null);

        var search = cut.Find("input[type=search]");
        search.Input("petrol");
        search.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal("Fuel selected.", cut.Find("[role=status]").TextContent);
    }

    [Fact]
    public void A_disabled_picker_disables_the_box_and_Enter_picks_nothing()
    {
        var emitted = new List<string?>();
        var cut = ctx.Render<OdsTagIconPicker>(p => p
            .Add(c => c.Disabled, true)
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<string?>(this, emitted.Add)));

        var search = cut.Find("input[type=search]");
        Assert.True(search.HasAttribute("disabled"));

        search.Input("petrol");
        search.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.Empty(emitted);
    }

    [Fact]
    public void Typing_clears_the_hover_preview()
    {
        var cut = Picker(null);

        cut.Find("[aria-label=Fuel]").MouseEnter();
        Assert.NotEmpty(cut.FindAll(".odc-iconpick-peek"));

        cut.Find("input[type=search]").Input("fu");
        Assert.Empty(cut.FindAll(".odc-iconpick-peek"));
    }

    [Fact]
    public void The_empty_state_sits_outside_the_radiogroup()
    {
        var cut = Picker(null);

        cut.Find("input[type=search]").Input("qqqqqq");

        var empty = cut.Find(".odc-iconpick-empty");
        Assert.Null(empty.Closest("[role=radiogroup]"));
        Assert.Equal("true", empty.GetAttribute("aria-hidden"));
    }

    [Fact]
    public async Task FocusSelectedAsync_clears_a_query_hiding_the_selection_then_focuses_it()
    {
        var cut = Picker("cabin");
        cut.Find("input[type=search]").Input("fuel");

        await cut.InvokeAsync(() => cut.Instance.FocusSelectedAsync());

        Assert.Equal("", cut.Find("input[type=search]").GetAttribute("value") ?? "");
        Assert.Equal(TagIcons.All.Count + 1, cut.FindAll("[role=radio]").Count);
        var focused = ctx.JSInterop.VerifyFocusAsyncInvoke();
        Assert.Equal(ButtonRef(cut, cut.FindAll("[role=radio]").ToList().FindIndex(b => b.GetAttribute("aria-label") == "Cabin")), ((ElementReference)focused.Arguments[0]!).Id);
    }

    [Fact]
    public void The_search_box_is_named_and_controls_the_grid()
    {
        var cut = Picker(null);

        var search = cut.Find("input[type=search]");
        Assert.Equal("Search icons", search.GetAttribute("aria-label"));
        Assert.Equal(cut.Find("[role=radiogroup]").Id, search.GetAttribute("aria-controls"));
    }
}
