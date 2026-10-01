using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The <c>/users</c> row is not a button (issue #273, WCAG 2.2 SC 4.1.2, axe <c>nested-interactive</c>).
///
/// <para>
/// Every body row used to be <c>&lt;tr role="button" tabindex="0" aria-expanded&gt;</c> holding the
/// "More actions" menu and the expand chevron. A <c>button</c> role has presentational children, so a
/// screen reader announced one button named after every cell, and the keyboard met two tab stops per
/// row. The design-system RecordTable never modelled the row as the control: the chevron is the only
/// disclosure, and the row click is a pointer convenience. These render the real page and assert that
/// shape, then that the header row reads as one style and the empty Created column is gone (issue #274).
/// </para>
/// </summary>
public class UsersRowDisclosureTests
{
    static UsersRowDisclosureTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly ExistingUser[] People =
    [
        new() { Id = "u-1", UserName = "ada", Email = "ada@example.test", Role = "Admin", Enabled = true, EmailConfirmed = true },
        new() { Id = "u-2", UserName = "bob", Email = "bob@example.test", Role = "User", Enabled = true },
    ];

    /// <summary>
    /// Renders <c>/users</c> with two rows. The page returns early from <c>OnInitializedAsync</c> outside a
    /// browser, so the load is driven through the search field's <c>OnSearch</c> (as
    /// <see cref="ListLoaderCardTests"/> does) and the first-load flag that only the browser path clears
    /// is cleared by hand — the one private field touched, because that flag has no seam outside a
    /// browser and the alternative is not rendering the page at all.
    /// </summary>
    private static (BunitContext Ctx, IRenderedComponent<Users> Cut) RenderUsers()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<IPageStateService>());
        ctx.Services.AddSingleton(Mock.Of<IProfileApiClient>());
        ctx.AddAuthorization().SetAuthorized("admin")
            .SetPolicies(PermissionClaims.UsersUpdate, PermissionClaims.UsersDelete);

        var users = new Mock<IUsersApiClient>();
        users.Setup(u => u.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingUser>>.Success(
                new PagedResult<ExistingUser> { Items = People, TotalCount = People.Length, Offset = 0, Limit = 25 }, HttpStatusCode.OK));
        ctx.Services.AddSingleton(users.Object);

        var cut = ctx.Render<Users>();
        var search = cut.FindComponent<OdsSearchField>().Instance;
        cut.InvokeAsync(() => search.OnSearch.InvokeAsync()).GetAwaiter().GetResult();
        typeof(Users).GetField("_isLoading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cut.Instance, false);
        cut.Render();
        cut.WaitForAssertion(() => Assert.Equal(People.Length, BodyRows(cut).Count));
        return (ctx, cut);
    }

    private static IReadOnlyList<IElement> BodyRows(IRenderedComponent<Users> cut) =>
        [.. cut.FindAll("table.usr-tbl > tbody > tr").Where(r => !r.ClassList.Contains("usr-detail-row"))];

    private static IElement Chevron(IRenderedComponent<Users> cut, int row) =>
        BodyRows(cut)[row].QuerySelector("button.usr-expand-btn")!;

    [Fact]
    public async Task A_body_row_is_not_a_control_and_holds_no_control_inside_one()
    {
        var (ctx, cut) = RenderUsers();
        await using var _ = ctx;

        foreach (var row in BodyRows(cut))
        {
            Assert.Null(row.GetAttribute("role"));
            Assert.Null(row.GetAttribute("tabindex"));
            Assert.Null(row.GetAttribute("aria-expanded"));

            // axe nested-interactive: nothing interactive sits inside another interactive element.
            foreach (var control in row.QuerySelectorAll(NestedInteractiveControlTests.InteractiveSelector))
            {
                for (var up = control.ParentElement; up is not null && up != row; up = up.ParentElement)
                    Assert.False(up.Matches(NestedInteractiveControlTests.InteractiveSelector),
                        $"<{control.LocalName}> is nested inside <{up.LocalName}>");
            }
        }
    }

    /// <summary>One tab stop per control: the menu trigger, then the chevron, in that order.</summary>
    [Fact]
    public async Task The_row_tab_order_is_more_actions_then_expand()
    {
        var (ctx, cut) = RenderUsers();
        await using var _ = ctx;

        var stops = BodyRows(cut)[0].QuerySelectorAll(NestedInteractiveControlTests.InteractiveSelector)
            .Where(e => e.GetAttribute("tabindex") != "-1")
            .Select(e => e.GetAttribute("aria-label"))
            .ToList();

        Assert.Equal(["More actions", "Expand row"], stops);
    }

    [Fact]
    public async Task The_chevron_is_the_disclosure_and_controls_the_detail_row_while_open()
    {
        var (ctx, cut) = RenderUsers();
        await using var _ = ctx;

        var chevron = Chevron(cut, 0);
        Assert.Equal("button", chevron.GetAttribute("type"));
        Assert.Equal("Expand row", chevron.GetAttribute("aria-label"));
        Assert.Equal("false", chevron.GetAttribute("aria-expanded"));
        Assert.Null(chevron.GetAttribute("aria-controls"));

        chevron.Click();

        chevron = Chevron(cut, 0);
        Assert.Equal("Collapse row", chevron.GetAttribute("aria-label"));
        Assert.Equal("true", chevron.GetAttribute("aria-expanded"));
        var controls = chevron.GetAttribute("aria-controls");
        Assert.Equal("usr-detail-u-1", controls);
        var detail = cut.Find($"#{controls}");
        Assert.Contains("usr-detail-row", detail.ClassList);
        Assert.Equal(BodyRows(cut)[0], detail.PreviousElementSibling);

        chevron.Click();

        chevron = Chevron(cut, 0);
        Assert.Equal("false", chevron.GetAttribute("aria-expanded"));
        Assert.Null(chevron.GetAttribute("aria-controls"));
        Assert.Empty(cut.FindAll("#usr-detail-u-1"));
    }

    /// <summary>The row click stays as a pointer convenience and drives the same state as the chevron.</summary>
    [Fact]
    public async Task A_row_click_still_toggles_the_row_and_the_chevron_reflects_it()
    {
        var (ctx, cut) = RenderUsers();
        await using var _ = ctx;

        BodyRows(cut)[1].Click();

        Assert.Equal("true", Chevron(cut, 1).GetAttribute("aria-expanded"));
        Assert.Equal("usr-detail-u-2", Chevron(cut, 1).GetAttribute("aria-controls"));
        Assert.Contains("expanded", BodyRows(cut)[1].ClassList);
    }

    /// <summary>
    /// Issue #274 §2. The table is the shared record table, so the <c>OdsSortHeader</c> cells and the plain
    /// Actions header take one header style from <c>.odc-rec thead th</c>; the page-local <c>.tbl thead th</c>
    /// rule that styled only the plain cells is gone. The Created column, empty on every row because
    /// <c>ApplicationUser</c> has no creation timestamp, is hidden — and the colspans follow the count.
    /// </summary>
    [Fact]
    public async Task The_header_is_one_style_and_the_empty_created_column_is_hidden()
    {
        var (ctx, cut) = RenderUsers();
        await using var _ = ctx;

        var table = cut.Find("table.usr-tbl");
        Assert.Contains("odc-rec", table.ClassList);
        Assert.DoesNotContain("tbl", table.ClassList);

        var headers = cut.FindAll("table.usr-tbl > thead th").Select(h => h.TextContent.Trim()).ToList();
        Assert.DoesNotContain("Created", headers);
        Assert.Equal("Actions", headers[^1]);

        Assert.All(BodyRows(cut), row => Assert.Equal(headers.Count, row.Children.Count(c => c.LocalName == "td")));

        Chevron(cut, 0).Click();
        Assert.Equal(headers.Count.ToString(), cut.Find("td.usr-detail-cell").GetAttribute("colspan"));

        // The page-local header rule the plain cells picked up — any `.tbl` selector, not `.tbl-*`.
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Users.razor.css"));
        Assert.DoesNotMatch(new Regex(@"\.tbl(?![\w-])"), css);
    }

    /// <summary>The detail panel's Created tile names the gap rather than printing a bare dash.</summary>
    [Fact]
    public void A_missing_creation_date_reads_not_recorded()
    {
        Assert.Equal("Not recorded", UserDisplay.CreatedText(People[0]));
    }
}
