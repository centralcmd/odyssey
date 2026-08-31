using Microsoft.AspNetCore.Components;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Layout;

// Module-based navigation model (design-system "Module rail + switcher + command palette" redesign).
// Every destination belongs to one of six modules; the rail shows the active module's pages, the
// switcher changes module, and the ⌘K palette jumps across all modules. Route keys/pages are
// unchanged — this is presentation only. `Href` is the real Blazor route (base-relative, no leading
// slash); `Claim` gates a page behind a permission (null = always visible).
//
// This registry is a second, hand-maintained source of truth: each page's `Href` must track its
// page's own `@page` route and each `Claim` its `[Authorize(Policy = …)]` attribute — there is no
// compile-time link, so a route rename or claim change must be mirrored here or the rail button goes
// dead / mis-gated.

/// <summary>One navigable page within a module.</summary>
public sealed record NavPage(string Key, string Label, string Icon, string Href, string? Claim = null, bool External = false);

/// <summary>An optionally-labelled group of pages inside a module (Finance is sub-grouped; others are flat).
/// <paramref name="NoDivider"/> suppresses the hairline the rail draws above a group — the design system
/// runs Finance's Reference pages straight on from Documents rather than splitting a nine-button rail four
/// ways.</summary>
public sealed record NavGroup(string? Label, IReadOnlyList<NavPage> Items, bool NoDivider = false);

/// <summary>A top-level module shown in the rail's switcher.</summary>
public sealed record NavModule(string Key, string Label, string Icon, IReadOnlyList<NavGroup> Groups);

/// <summary>A page flattened with its owning module (for the command palette).</summary>
public sealed record NavPageHit(NavPage Page, string ModuleKey, string ModuleLabel);

public static class NavModel
{
    /// <summary>The Material Icons ligature used for the About page (rendered as an inline GitHub SVG,
    /// since Material Icons has no GitHub glyph).</summary>
    public const string GithubIconKey = "github";

    /// <summary>Every destination, grouped by module. Route keys/hrefs are unchanged from the flat drawer.</summary>
    public static readonly IReadOnlyList<NavModule> All =
    [
        new("dashboard", "Dashboard", "space_dashboard",
        [
            new(null, [new("home", "Dashboard", "space_dashboard", "")]),
        ]),
        new("finance", "Finance", "account_balance_wallet",
        [
            new("Money",
            [
                new("accounts", "Accounts", "account_balance_wallet", "accounts"),
                new("transactions", "Transactions", "receipt_long", "transactions"),
                new("budgets", "Budgets", "pie_chart", "budgets"),
            ]),
            new("Commitments",
            [
                new("tax-statements", "Tax Statements", "request_quote", "tax-statements", PermissionClaims.TaxesRead),
                new("insurance", "Insurance", "shield", "insurance-policies", PermissionClaims.InsuranceRead),
                new("contracts", "Contracts", "handshake", "contracts", PermissionClaims.ContractsRead),
                new("subscriptions", "Subscriptions", "subscriptions", "subscriptions", PermissionClaims.SubscriptionsRead),
            ]),
            new("Documents",
            [
                new("files", "Files", "folder", "files", PermissionClaims.FilesRead),
            ]),
            new("Reference",
            [
                new("tags", "Transaction Tags", "local_offer", "transaction-tags"),
                new("currencies", "Currencies", "attach_money", "currencies"),
                new("exchange-rates", "Exchange rates", "currency_exchange", "exchange-rates"),
            ], NoDivider: true),
        ]),
        // The photo library and Contacts live under Journal (design-system app shell): the /photos page
        // shows as "Photos", with Albums, Contacts + the tag pages alongside the journal/task pages in one
        // flat group. Contacts was relocated here from its former standalone Contacts module (route key and
        // page build unchanged).
        new("journal", "Journal", "menu_book",
        [
            new(null,
            [
                new("journal", "Journal", "book", "journal", PermissionClaims.JournalRead),
                new("calendar", "Calendar", "calendar_month", "calendar", PermissionClaims.CalendarRead),
                new("photos", "Photos", "photo_library", "photos", PermissionClaims.PhotosRead),
                new("albums", "Albums", "photo_album", "albums", PermissionClaims.PhotoAlbumsRead),
                new("tasks", "Tasks", "checklist", "tasks", PermissionClaims.TasksRead),
                new("contacts", "Contacts", "groups", "contacts", PermissionClaims.ContactsRead),
                new("journal-tags", "Journal Tags", "local_offer", "journal-tags", PermissionClaims.JournalTagsRead),
                new("task-tags", "Task Tags", "local_offer", "task-tags", PermissionClaims.TaskTagsRead),
                new("photo-tags", "Photo Tags", "local_offer", "photo-tags", PermissionClaims.PhotoTagsRead),
            ]),
        ]),
        new("user", "User", "person",
        [
            new(null,
            [
                new("user-account", "Account", "account_circle", "account"),
                new("preferences", "Preferences", "tune", "preferences"),
            ]),
        ]),
        new("system", "System", "settings",
        [
            new(null,
            [
                new("users", "Users", "manage_accounts", "users", PermissionClaims.UsersRead),
                new("roles", "Roles", "badge", "roles", PermissionClaims.UsersRead),
                new("analysis-log", "Analysis log", "policy", "analysis-log", PermissionClaims.FileAnalysisAudit),
                new("legal-documents", "Terms of Service", "gavel", "legal-documents", PermissionClaims.UsersManage),
                new("settings", "Settings", "settings", "settings", PermissionClaims.SystemSettingsRead),
                new("about", "About", GithubIconKey, "https://github.com/centralcmd/odyssey", External: true),
            ]),
        ]),
    ];

    /// <summary>Groups within <paramref name="module"/> whose items the user can view (empty groups dropped).</summary>
    public static IReadOnlyList<NavGroup> VisibleGroups(NavModule module, Func<NavPage, bool> canView) =>
        [.. module.Groups
            .Select(g => new NavGroup(g.Label, [.. g.Items.Where(canView)], g.NoDivider))
            .Where(g => g.Items.Count > 0)];

    /// <summary>The viewable pages of a module, flattened.</summary>
    public static IReadOnlyList<NavPage> VisibleItems(NavModule module, Func<NavPage, bool> canView) =>
        [.. VisibleGroups(module, canView).SelectMany(g => g.Items)];

    /// <summary>The modules with at least one page the current user can view. A module whose every page
    /// is claim-gated away (zero visible pages) is dropped entirely — no dead "0 pages" row in the rail
    /// switcher or palette (issue #311 FE #1). This future-proofs any later fully-gated module too.</summary>
    public static IReadOnlyList<NavModule> VisibleModules(Func<NavPage, bool> canView) =>
        [.. All.Where(m => VisibleItems(m, canView).Count > 0)];

    /// <summary>Every viewable page across all modules, tagged with its owning module (palette source).</summary>
    public static IReadOnlyList<NavPageHit> AllVisiblePages(Func<NavPage, bool> canView) =>
        [.. All.SelectMany(m => VisibleItems(m, canView).Select(p => new NavPageHit(p, m.Key, m.Label)))];

    /// <summary>The base-relative path with any query string / fragment stripped — Blazor routing is
    /// case-insensitive and ignores <c>?query</c>/<c>#fragment</c>, so active-route matching must too.</summary>
    public static string NormalizePath(string relative)
    {
        var cut = relative.AsSpan().IndexOfAny('?', '#');
        return cut < 0 ? relative : relative[..cut];
    }

    /// <summary>Whether a page is the one the current route resolves to. Matching is case-insensitive
    /// on both arms (Blazor routes are case-insensitive), so <c>/Accounts</c> still highlights Accounts.</summary>
    public static bool IsActive(NavPage page, string currentRelative)
    {
        if (page.External)
        {
            return false;
        }

        if (page.Href.Length == 0)
        {
            return currentRelative.Length == 0; // Dashboard owns the root only
        }

        return string.Equals(currentRelative, page.Href, StringComparison.OrdinalIgnoreCase)
            || currentRelative.StartsWith(page.Href + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The module owning the current route (falls back to the first module).</summary>
    public static NavModule ModuleOf(string currentRelative) =>
        All.FirstOrDefault(m => m.Groups.Any(g => g.Items.Any(p => IsActive(p, currentRelative)))) ?? All[0];
}

/// <summary>Inline brand glyphs the Material Icons font can't provide.</summary>
public static class NavIcons
{
    /// <summary>GitHub mark (Material Icons has no GitHub glyph). <c>currentColor</c> so CSS tints it.</summary>
    public const string GithubSvg =
        "<svg class=\"nav-icon-svg\" viewBox=\"0 0 24 24\" width=\"20\" height=\"20\" fill=\"currentColor\" aria-hidden=\"true\">" +
        "<path d=\"M12 .5C5.65.5.5 5.65.5 12c0 5.09 3.29 9.4 7.86 10.93.57.1.78-.25.78-.55v-2.05c-3.19.7-3.86-1.36-3.86-1.36-.52-1.32-1.27-1.67-1.27-1.67-1.04-.71.08-.7.08-.7 1.15.08 1.76 1.18 1.76 1.18 1.02 1.76 2.69 1.25 3.35.96.1-.74.4-1.25.72-1.54-2.55-.29-5.23-1.27-5.23-5.66 0-1.25.45-2.27 1.18-3.07-.12-.29-.51-1.46.11-3.04 0 0 .96-.31 3.15 1.17a10.94 10.94 0 0 1 5.74 0c2.19-1.48 3.15-1.17 3.15-1.17.62 1.58.23 2.75.11 3.04.74.8 1.18 1.82 1.18 3.07 0 4.4-2.69 5.37-5.25 5.65.41.35.78 1.05.78 2.13v3.16c0 .31.21.66.79.55C20.21 21.4 23.5 17.09 23.5 12 23.5 5.65 18.35.5 12 .5z\"/></svg>";

    /// <summary>Render a nav icon: the GitHub SVG for the About page, else a Material Icons ligature span.</summary>
    public static RenderFragment Icon(string icon) => builder =>
    {
        if (icon == NavModel.GithubIconKey)
        {
            builder.AddMarkupContent(0, GithubSvg);
        }
        else
        {
            builder.OpenElement(1, "span");
            builder.AddAttribute(2, "class", "material-icons");
            builder.AddAttribute(3, "aria-hidden", "true");
            builder.AddContent(4, icon);
            builder.CloseElement();
        }
    };
}
