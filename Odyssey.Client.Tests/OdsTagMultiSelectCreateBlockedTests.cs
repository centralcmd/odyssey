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
        var row = cut.Find(".odc-tagms-blocked");
        Assert.Equal("status", row.GetAttribute("role"));
        Assert.Equal("“Old album” is an archived tag. Restore it on the Photo tags page to use it.", row.TextContent.Trim());
        // The status row is the only line: the "no match" text does not also render.
        Assert.DoesNotContain(cut.FindAll(".odc-tagms-empty"), e => !e.ClassList.Contains("odc-tagms-blocked"));
    }

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
    }

    [Theory]
    [InlineData("old album", "“Old album” is an archived tag. Restore it on the Transaction tags page to use it.")]
    [InlineData("  GROCERIES ", "A tag called “Groceries” already exists.")]
    [InlineData("Groceries extra", null)]
    [InlineData("   ", null)]
    public void TagNameRule_words_the_refusal_once(string typed, string? expected) =>
        Assert.Equal(expected, TagNameRule.Blocked(Tags, typed, TagNameRule.Pages.Transaction));

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
