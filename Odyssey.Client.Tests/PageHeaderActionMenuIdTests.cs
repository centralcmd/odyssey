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
/// <see cref="PageHeader.ActionMenuId"/> and the null-<see cref="PageHeader.ActionMenu"/> rule (issue #509 §4.1, v5 §1,
/// AC 2 and AC 29). The id lands on the <c>MudMenu</c> ROOT, which carries no <c>tabindex</c>, so
/// <c>odsRestoreFocusOwner</c> resolves it to the root's first <c>button</c>: the unchanged built-in ⋯ activator.
/// </summary>
public class PageHeaderActionMenuIdTests
{
    static PageHeaderActionMenuIdTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static readonly RenderFragment Items = b =>
    {
        b.OpenComponent<MudMenuItem>(0);
        b.AddComponentParameter(1, nameof(MudMenuItem.ChildContent), (RenderFragment)(c => c.AddContent(0, "Export")));
        b.CloseComponent();
    };

    private static IRenderedComponent<PageHeader> Render(BunitContext ctx, string? id, RenderFragment? menu = null, string? label = null) =>
        ctx.Render<PageHeader>(p =>
        {
            p.Add(h => h.Title, "Feeds").Add(h => h.ActionMenu, menu ?? Items).Add(h => h.ActionMenuId, id);
            if (label is not null)
            {
                p.Add(h => h.ActionMenuLabel, label);
            }
        });

    [Fact] // AC 29
    public async Task The_id_is_on_the_menu_root_which_is_not_focusable_and_holds_the_activator_button()
    {
        await using var ctx = NewContext();

        var header = Render(ctx, "news-feeds-actions", label: "Feed actions");

        var root = header.Find("#news-feeds-actions");
        Assert.Contains("mud-menu", root.ClassList);
        Assert.Contains("ph-action-menu", root.ClassList);
        Assert.Null(root.GetAttribute("tabindex"));
        Assert.False(root.Matches("button, a[href], input, select, textarea, [tabindex]"));
        // The element odsRestoreFocusOwner picks: the root's first focusable descendant is the ⋯ button itself.
        var first = root.QuerySelector("button, a[href], input, select, textarea, [tabindex]:not([tabindex='-1'])");
        Assert.NotNull(first);
        Assert.Equal("BUTTON", first!.TagName);
        Assert.Equal("Feed actions", first.GetAttribute("aria-label"));
        Assert.Single(header.FindAll("#news-feeds-actions"));
    }

    [Fact] // AC 29
    public async Task The_activator_markup_is_the_same_with_and_without_an_id()
    {
        await using var ctx = NewContext();

        var plain = Render(ctx, null, label: "Feed actions").Find(".ph-action-menu");
        var withId = Render(ctx, "news-feeds-actions", label: "Feed actions").Find(".ph-action-menu");

        Assert.Null(plain.GetAttribute("id"));
        withId.RemoveAttribute("id");
        Assert.Equal(Normalised(plain.OuterHtml), Normalised(withId.OuterHtml));

        // One tab stop, its name, and the popup state on the button — not on a wrapper.
        var button = withId.QuerySelector("button")!;
        Assert.Single(withId.QuerySelectorAll("button, [tabindex]:not([tabindex='-1'])"));
        Assert.Equal("Feed actions", button.GetAttribute("aria-label"));
        Assert.NotNull(button.GetAttribute("aria-haspopup"));
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
    }

    // Render-scoped noise only: Blazor's event-handler ids and MudBlazor's per-instance element ids.
    private static string Normalised(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, @"\s(blazor:[a-z]+=""[^""]*""|(id|aria-controls|aria-owns)=""[^""]*"")", string.Empty);

    [Fact] // AC 29: an existing header renders unchanged
    public async Task An_existing_page_header_with_a_menu_and_no_id_renders_no_id()
    {
        await using var ctx = NewContext();

        var header = Render(ctx, null);

        var root = header.Find(".ph-action-menu");
        Assert.Null(root.GetAttribute("id"));
        Assert.Equal("More actions", root.QuerySelector("button")!.GetAttribute("aria-label"));
    }

    [Fact] // AC 2
    public async Task A_null_action_menu_renders_no_menu_button()
    {
        await using var ctx = NewContext();

        var header = ctx.Render<PageHeader>(p => p.Add(h => h.Title, "Feeds").Add(h => h.ActionMenuId, "news-feeds-actions"));

        Assert.Empty(header.FindAll(".ph-action-menu"));
        Assert.Empty(header.FindAll("#news-feeds-actions"));
    }
}
