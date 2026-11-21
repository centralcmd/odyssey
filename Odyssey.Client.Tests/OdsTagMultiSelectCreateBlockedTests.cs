using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>OdsTagMultiSelect.CreateBlocked</c> (Odyssey Design System · <c>TagMultiSelect</c>
/// <c>createBlocked</c>): a refused name shows why, as a status row, in place of the create rows; and
/// <see cref="TagNameRule"/>, the one place the refusal sentence is worded.
/// </summary>
public sealed class OdsTagMultiSelectCreateBlockedTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    public OdsTagMultiSelectCreateBlockedTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await ctx.DisposeAsync();

    private static readonly TagNameEntry[] Tags =
    [
        new("Groceries", Archived: false),
        new("Old album", Archived: true),
    ];

    private IRenderedComponent<PickerHost> Open(string typed, Func<string, string?>? blocked)
    {
        var cut = ctx.Render<PickerHost>(p => p.Add(h => h.Blocked, blocked));
        cut.Find("button.odc-tagms-trigger").Click();
        cut.WaitForElement(".odc-tagms-search input").Input(typed);
        return cut;
    }

    [Fact]
    public void A_refused_name_replaces_the_create_rows_with_a_status_row()
    {
        var cut = Open("old album", TagNameRule.For(Tags, TagNameRule.Pages.Photo));

        Assert.Empty(cut.FindAll("button.odc-tagms-create"));
        const string reason = "“Old album” is an archived tag. Restore it on the Photo tags page to use it.";
        var row = cut.Find(".odc-tagms-blocked");
        Assert.Equal(reason, row.TextContent.Trim());
        // The row is inserted already holding its text, which is not announced, so the reason goes
        // through the component's persistent polite live region instead (WCAG 4.1.3).
        Assert.Null(row.GetAttribute("role"));
        Assert.StartsWith(reason, LiveRegion(cut));
        // The status row is the only line: the "no match" text does not also render.
        Assert.DoesNotContain(cut.FindAll(".odc-tagms-empty"), e => !e.ClassList.Contains("odc-tagms-blocked"));
    }

    [Fact]
    public void The_reason_is_announced_once_per_change()
    {
        // "Holiday" is an active tag the picker does not list (another field's options), so it is refused too.
        var cut = Open("old album", TagNameRule.For([.. Tags, new("Holiday", Archived: false)], TagNameRule.Pages.Photo));
        var first = LiveRegion(cut);

        // The same refused name typed again (different case) is the same reason: not said twice.
        cut.Find(".odc-tagms-search input").Input("OLD ALBUM");
        Assert.Equal(first, LiveRegion(cut));

        // A different refused name is a new reason.
        cut.Find(".odc-tagms-search input").Input("holiday");
        Assert.StartsWith("A tag called “Holiday” already exists.", LiveRegion(cut));
    }

    private static string LiveRegion(IRenderedComponent<PickerHost> cut) =>
        cut.Find("div.sr-only[role=status][aria-live=polite]").TextContent.Trim('\u200B', ' ');

    [Fact]
    public void A_free_name_still_offers_the_create_row()
    {
        var cut = Open("Brand new", TagNameRule.For(Tags, TagNameRule.Pages.Photo));

        Assert.Single(cut.FindAll("button.odc-tagms-create"));
        Assert.Empty(cut.FindAll(".odc-tagms-blocked"));
    }

    [Fact]
    public void Without_a_rule_the_create_row_shows_as_before()
    {
        var cut = Open("old album", blocked: null);

        Assert.Single(cut.FindAll("button.odc-tagms-create"));
        Assert.Empty(cut.FindAll(".odc-tagms-blocked"));
    }

    [Fact]
    public void Enter_does_not_create_a_refused_name()
    {
        var created = 0;
        var cut = ctx.Render<PickerHost>(p => p
            .Add(h => h.Blocked, TagNameRule.For(Tags, TagNameRule.Pages.Photo))
            .Add(h => h.OnCreated, () => created++));
        cut.Find("button.odc-tagms-trigger").Click();
        var search = cut.WaitForElement(".odc-tagms-search input");
        search.Input("Old album");
        search.KeyDown("Enter");

        Assert.Equal(0, created);

        // Positive control: the same gesture on a free name does create, so the zero above is the rule.
        search = cut.Find(".odc-tagms-search input");
        search.Input("Brand new");
        search.KeyDown("Enter");

        Assert.Equal(1, created);
    }

    [Theory]
    [InlineData("old album", "“Old album” is an archived tag. Restore it on the Transaction tags page to use it.")]
    [InlineData("  GROCERIES ", "A tag called “Groceries” already exists.")]
    [InlineData("Groceries extra", null)]
    [InlineData("   ", null)]
    public void TagNameRule_words_the_refusal_once(string typed, string? expected) =>
        Assert.Equal(expected, TagNameRule.Blocked(Tags, typed, TagNameRule.Pages.Transaction));

    [Theory]
    [InlineData(TagNameRule.Pages.Journal)]
    [InlineData(TagNameRule.Pages.Task)]
    public void Journal_and_task_vocabularies_refuse_an_archived_name_too(string page)
    {
        // Journal and task tag names are unique archived tags included, like every other vocabulary.
        Assert.Equal($"“Old album” is an archived tag. Restore it on the {page} page to use it.", TagNameRule.Blocked(Tags, "old album", page));
        Assert.Equal("A tag called “Groceries” already exists.", TagNameRule.Blocked(Tags, "groceries", page));
    }

    [Fact]
    public void TagNameRule_keeps_inner_whitespace_as_the_server_does() =>
        Assert.Null(TagNameRule.Blocked(Tags, "Old  album", TagNameRule.Pages.Photo));

    private sealed class PickerHost : ComponentBase
    {
        [Parameter] public Func<string, string?>? Blocked { get; set; }
        [Parameter] public Action? OnCreated { get; set; }

        private IReadOnlyCollection<string> value = [];

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsTagMultiSelect>(1);
            builder.AddAttribute(2, nameof(OdsTagMultiSelect.Label), "Tags");
            builder.AddAttribute(3, nameof(OdsTagMultiSelect.Options), (IReadOnlyList<OdsOption>)[new OdsOption("g", "Groceries")]);
            builder.AddAttribute(4, nameof(OdsTagMultiSelect.Value), value);
            builder.AddAttribute(5, nameof(OdsTagMultiSelect.ValueChanged),
                EventCallback.Factory.Create<IReadOnlyCollection<string>>(this, v => value = v));
            builder.AddAttribute(6, nameof(OdsTagMultiSelect.OnCreate), (Func<string, string?, OdsOption?>)((text, _) =>
            {
                OnCreated?.Invoke();
                return new OdsOption("new", text);
            }));
            builder.AddAttribute(7, nameof(OdsTagMultiSelect.CreateBlocked), Blocked);
            builder.CloseComponent();
        }
    }
}
