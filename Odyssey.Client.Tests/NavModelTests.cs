using Odyssey.Dtos.Authorization;
using Odyssey.Client.Layout;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Unit coverage for <see cref="NavModel"/> — the pure (non-Blazor) navigation registry behind the
/// module rail / switcher / command palette (PR #299): active-route matching (case-insensitive, root
/// and prefix boundaries, external pages), module resolution + fallback, permission gating, and
/// query/fragment normalisation.
/// </summary>
public class NavModelTests
{
    private static NavPage Page(string key) =>
        NavModel.All.SelectMany(m => m.Groups).SelectMany(g => g.Items).First(p => p.Key == key);

    private static NavModule Module(string key) => NavModel.All.First(m => m.Key == key);

    private static readonly Func<NavPage, bool> Admin = _ => true;
    // A non-admin sees only ungated pages (claim == null).
    private static readonly Func<NavPage, bool> NonAdmin = p => p.Claim is null;

    // ── IsActive ────────────────────────────────────────────────────────────────
    [Fact]
    public void IsActive_exact_match() => Assert.True(NavModel.IsActive(Page("accounts"), "accounts"));

    [Fact]
    public void IsActive_is_case_insensitive() =>
        // Blazor routing is case-insensitive; /Accounts must still highlight Accounts (regression guard).
        Assert.True(NavModel.IsActive(Page("accounts"), "Accounts"));

    [Fact]
    public void IsActive_matches_sub_path_prefix() =>
        Assert.True(NavModel.IsActive(Page("accounts"), "accounts/42"));

    [Fact]
    public void IsActive_does_not_match_sibling_with_shared_prefix() =>
        // The "/" boundary: "accounts" must not light up for "accounts-archive".
        Assert.False(NavModel.IsActive(Page("accounts"), "accounts-archive"));

    [Fact]
    public void IsActive_root_page_owns_root_only()
    {
        Assert.True(NavModel.IsActive(Page("home"), ""));
        Assert.False(NavModel.IsActive(Page("home"), "accounts"));
    }

    [Fact]
    public void IsActive_external_page_is_never_active() =>
        Assert.False(NavModel.IsActive(Page("about"), Page("about").Href));

    // ── ModuleOf ────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("subscriptions", "finance")]
    [InlineData("insurance-policies", "finance")]
    // Contacts was relocated from its standalone module into the Journal module (design-system app shell).
    [InlineData("contacts", "journal")]
    [InlineData("account", "user")]
    [InlineData("settings", "system")]
    // The photo library lives under the Journal module (design-system app shell).
    [InlineData("photos", "journal")]
    [InlineData("albums", "journal")]
    [InlineData("photo-tags", "journal")]
    [InlineData("", "dashboard")]
    public void ModuleOf_resolves_owning_module(string relative, string expectedModuleKey) =>
        Assert.Equal(expectedModuleKey, NavModel.ModuleOf(relative).Key);

    [Fact]
    public void ModuleOf_unknown_route_falls_back_to_first_module() =>
        Assert.Equal(NavModel.All[0].Key, NavModel.ModuleOf("nope-not-a-route").Key);

    // ── NormalizePath ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("accounts", "accounts")]
    [InlineData("accounts?tab=1", "accounts")]
    [InlineData("accounts#section", "accounts")]
    [InlineData("accounts/42?x=1#y", "accounts/42")]
    [InlineData("", "")]
    public void NormalizePath_strips_query_and_fragment(string input, string expected) =>
        Assert.Equal(expected, NavModel.NormalizePath(input));

    [Fact]
    public void NormalizePath_then_IsActive_ignores_query() =>
        Assert.True(NavModel.IsActive(Page("accounts"), NavModel.NormalizePath("accounts?filter=open")));

    // ── Permission gating ────────────────────────────────────────────────────────
    [Fact]
    public void VisibleItems_system_shows_only_about_to_non_admin()
    {
        var visible = NavModel.VisibleItems(Module("system"), NonAdmin);
        Assert.Equal(new[] { "about" }, visible.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void VisibleItems_system_shows_all_to_admin() =>
        Assert.Equal(6, NavModel.VisibleItems(Module("system"), Admin).Count);

    /// <summary>
    /// The Terms of Service authoring surface (issue #354) is gated by <c>users.manage</c> — the same
    /// claim as the version-management endpoints it calls — not by the <c>system-settings.read</c> that
    /// gates the Settings entry beside it.
    /// </summary>
    [Fact]
    public void LegalDocuments_isGatedByUsersManage() =>
        Assert.Equal(
            PermissionClaims.UsersManage,
            NavModel.VisibleItems(Module("system"), Admin).Single(p => p.Key == "legal-documents").Claim);

    [Fact]
    public void VisibleGroups_drops_a_fully_gated_group()
    {
        // Finance "Commitments" (tax/insurance/contracts/subscriptions) is entirely claim-gated → a
        // non-admin sees only the Money + Reference groups.
        var groups = NavModel.VisibleGroups(Module("finance"), NonAdmin);
        Assert.DoesNotContain("Commitments", groups.Select(g => g.Label));
        Assert.Contains("Money", groups.Select(g => g.Label));
        Assert.Contains("Reference", groups.Select(g => g.Label));
    }

    /// <summary>
    /// The rail draws a hairline above every group but the first, so <c>NoDivider</c> is what runs
    /// Finance's Reference pages straight on from Documents instead of splitting a nine-button rail
    /// four ways (the design system's AppShell). <c>VisibleGroups</c> rebuilds each <c>NavGroup</c> to
    /// drop the pages a user can't see — a rebuild that forgot to carry the flag would silently put
    /// the divider back, which nothing else in the suite would notice.
    /// </summary>
    [Fact]
    public void VisibleGroups_carries_NoDivider_through_the_rebuild()
    {
        foreach (var user in new[] { Admin, NonAdmin })
        {
            var reference = NavModel.VisibleGroups(Module("finance"), user).Single(g => g.Label == "Reference");
            Assert.True(reference.NoDivider);
        }

        // And it is not simply true everywhere: the groups that DO earn a divider still declare one.
        var groups = NavModel.VisibleGroups(Module("finance"), Admin);
        Assert.All(groups.Where(g => g.Label != "Reference"), g => Assert.False(g.NoDivider));
    }

    [Fact]
    public void Contacts_page_is_gated_by_ContactsRead()
        // Contacts was relocated into the Journal module and gated to mirror ContactsCard's
        // [Authorize(Policy = ContactsRead)]. Pin the claim directly (not just via the "Journal is
        // fully gated" invariant) so a future ungating is caught as an explicit intent regression.
        => Assert.Equal(PermissionClaims.ContactsRead, Page("contacts").Claim);

    [Fact]
    public void AllVisiblePages_respects_gate_and_tags_owning_module()
    {
        var adminKeys = NavModel.AllVisiblePages(Admin).Select(h => h.Page.Key).ToList();
        var nonAdminKeys = NavModel.AllVisiblePages(NonAdmin).Select(h => h.Page.Key).ToList();

        Assert.Contains("subscriptions", adminKeys);
        Assert.DoesNotContain("subscriptions", nonAdminKeys); // subscriptions.read gated
        Assert.DoesNotContain("users", nonAdminKeys);         // users.read gated
        Assert.Contains("accounts", nonAdminKeys);            // ungated

        var accounts = NavModel.AllVisiblePages(Admin).First(h => h.Page.Key == "accounts");
        Assert.Equal("finance", accounts.ModuleKey);
    }

    // ── VisibleModules (issue #311 FE #1: drop zero-visible-page modules) ─────────
    [Fact]
    public void VisibleModules_admin_sees_every_module() =>
        Assert.Equal(NavModel.All.Count, NavModel.VisibleModules(Admin).Count);

    [Fact]
    public void VisibleModules_drops_the_fully_gated_journal_module_for_a_guest()
    {
        var keys = NavModel.VisibleModules(NonAdmin).Select(m => m.Key).ToList();

        // Every Journal page is claim-gated, so a user holding none of those claims (a Guest) must not
        // see the Journal module at all — no dead "0 pages" row in the rail switcher / palette.
        Assert.DoesNotContain("journal", keys);
        // Modules that still have at least one ungated page remain.
        Assert.Contains("finance", keys);
        Assert.Contains("dashboard", keys);
    }
}
