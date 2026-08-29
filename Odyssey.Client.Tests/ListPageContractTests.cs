using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lint guards for the three list-page contracts that issue #364 found broken on ten pages
/// each — every one of them a copy-paste divergence rather than a design decision, and every one of
/// them invisible to the compiler and to code review. Like <see cref="RazorStringBindingTests"/>,
/// these are pure source-text checks: the client test project has no bUnit, and none of these
/// defects need a rendered component to detect.
/// </summary>
public class ListPageContractTests
{
    /// <summary>
    /// Every page that owns a primary list, with the file(s) holding its <c>@code</c> / code-behind.
    /// Add new list pages here — the register is the point: a page that fetches a list the user is
    /// looking at must be able to say "this failed", and nothing else in the build enforces that.
    /// Thin wrappers over a shared list component (JournalTagsPage → OdsTagAdmin) are excluded; the
    /// component they delegate to is registered in their place, since it carries the state.
    /// </summary>
    private static readonly string[] ListPages =
    [
        "Pages/Files.razor",
        "Pages/Users.razor",
        "Pages/AnalysisLog.razor",
        "Pages/Finance/AccountsCard.razor",
        "Pages/Finance/BudgetsCard.razor",
        "Pages/Finance/ContactsCard.razor",
        "Pages/Finance/ContractsCard.razor",
        "Pages/Finance/CurrenciesCard.razor",
        "Pages/Finance/ExchangeRatesCard.razor",
        "Pages/Finance/InsuranceCard.razor",
        "Pages/Finance/SubscriptionCard.razor",
        "Pages/Finance/TaxStatementsCard.razor",
        "Pages/Finance/TransactionsCard.razor",
        "Pages/Journal/JournalCard.razor",
        "Pages/Journal/TasksPage.razor",
        "Components/OdsTagAdmin.razor",
        "Pages/Calendar/CalendarPage.razor",
        "Pages/Photos/PhotosCard.razor",
        "Pages/Photos/AlbumsPage.razor",
    ];

    /// <summary>
    /// A list unwrapped with <c>ItemsOrToast</c> / <c>PagedOrToast</c> falls back to an empty list on
    /// failure, so a 500 is indistinguishable from "you have none yet" unless the page records the
    /// failure separately. Without it the page renders its onboarding empty state — "No contracts yet
    /// — Add a contract…" — after a server error, with no indication anything went wrong and no way
    /// to retry. <c>ApiInteropExtensions.PagedOrToast</c> warns about exactly this in its remarks.
    /// </summary>
    [Fact]
    public void Every_list_page_can_distinguish_a_failed_load_from_an_empty_one()
    {
        var clientRoot = FindClientRoot();

        var missing = ListPages
            .Where(page => !SourceFor(clientRoot, page).Contains("_loadError"))
            .ToList();

        Assert.True(missing.Count == 0,
            "List pages with no _loadError field — a failed load renders as the empty state:\n" +
            string.Join('\n', missing));
    }

    /// <summary>
    /// On a server-paginated page the row count and the pager's total come from the same fetch, so
    /// removing a row locally without touching <c>_totalCount</c> leaves the pager reporting the
    /// pre-delete total and the page rendering one row short with nothing pulled up from the next
    /// page. Either refetch (<c>RefreshAsync</c>) or adjust the total — never just drop the row.
    /// </summary>
    [Fact]
    public void A_delete_on_a_paged_list_page_refetches_or_adjusts_the_total()
    {
        var clientRoot = FindClientRoot();
        var violations = new List<string>();

        foreach (var file in EnumeratePageSources())
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("_totalCount"))
                continue; // not server-paginated

            foreach (Match delete in Regex.Matches(text, @"\.DeleteAsync\("))
            {
                // The follow-up lives in the same success branch as the call — a short window covers
                // it without needing to parse the method body.
                var window = string.Join('\n', text[delete.Index..].Split('\n').Take(12));
                if (window.Contains("RefreshAsync") || window.Contains("ReloadAsync") || window.Contains("_totalCount"))
                    continue;

                var line = text[..delete.Index].Count(c => c == '\n') + 1;
                violations.Add($"{Path.GetRelativePath(clientRoot, file)}:{line} — delete on a paged list " +
                               "neither refetches (RefreshAsync/ReloadAsync) nor adjusts _totalCount");
            }
        }

        Assert.True(violations.Count == 0,
            "Deletes that leave the pager reporting a stale total:\n" + string.Join('\n', violations));
    }

    /// <summary>
    /// <c>OdsSearchField</c> defaults to <c>Immediate="true"</c>, so an <c>OnSearch</c> handler with no
    /// <c>DebounceInterval</c> fires on every <c>oninput</c> — one server round-trip per keystroke.
    /// The convention across the list pages is 300 ms.
    /// </summary>
    [Fact]
    public void No_search_field_fires_OnSearch_without_a_debounce()
    {
        var clientRoot = FindClientRoot();

        // Non-greedy up to the tag close; the attributes may be spread over several lines.
        var searchField = new Regex(@"<OdsSearchField\b(.*?)/?>", RegexOptions.Singleline);
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(clientRoot, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match tag in searchField.Matches(text))
            {
                var attrs = tag.Groups[1].Value;
                if (!Regex.IsMatch(attrs, @"\bOnSearch=") || attrs.Contains("DebounceInterval="))
                    continue;

                var line = text[..tag.Index].Count(c => c == '\n') + 1;
                violations.Add($"{Path.GetRelativePath(clientRoot, file)}:{line} — " +
                               "<OdsSearchField OnSearch=…> without DebounceInterval fires once per keystroke");
            }
        }

        Assert.True(violations.Count == 0,
            "Undebounced search fields:\n" + string.Join('\n', violations));
    }

    /// <summary>
    /// A pager moves the user to a different set of rows without moving focus and without a
    /// navigation, so the only thing that changes is content the screen-reader user is not looking
    /// at — WCAG 2.2 §4.1.3 Status Messages. <c>OdsLiveAnnouncer</c> is the sanctioned mechanism and
    /// was already on 20 surfaces when issue #365 found Files and Users paging in silence; this keeps
    /// the next paged page from being added without one.
    /// </summary>
    [Fact]
    public void Every_paged_list_page_hosts_a_live_announcer()
    {
        var clientRoot = FindClientRoot();

        var missing = ListPages
            .Select(page => (page, source: SourceFor(clientRoot, page)))
            .Where(p => p.source.Contains("<OdsPager") || p.source.Contains("<OdsInfiniteList"))
            .Where(p => !p.source.Contains("<OdsLiveAnnouncer"))
            .Select(p => p.page)
            .ToList();

        Assert.True(missing.Count == 0,
            "Paged list pages with no OdsLiveAnnouncer — paging announces nothing:\n" +
            string.Join('\n', missing));
    }

    /// <summary>
    /// Recording the failure is only half of it — the page also has to *render* it, and before issue
    /// #368 each one assembled the loading / refetching / error / empty states by hand, in one of two
    /// competing dialects. <c>OdsListStatus</c> (reached directly or through <c>OdsRecordTable</c> /
    /// <c>OdsInfiniteList</c>, which forward the same parameters) owns the whole state machine, and a
    /// page opts into the error state by handing it <c>Error</c>. A page that doesn't pass it renders
    /// its onboarding empty state after a 500 — the exact defect the field was added to prevent.
    /// </summary>
    [Fact]
    public void Every_list_page_hands_its_load_failure_to_the_shared_state_machine()
    {
        var clientRoot = FindClientRoot();

        var missing = ListPages
            .Where(page => !SourceFor(clientRoot, page).Contains("Error=\"_loadError\""))
            .ToList();

        Assert.True(missing.Count == 0,
            "List pages that record _loadError but never pass it to OdsListStatus (or to a list " +
            "primitive that forwards it), so nothing renders the error state:\n" +
            string.Join('\n', missing));
    }

    /// <summary>
    /// The error and filtered-empty states exist once, in <c>OdsListStatus</c>. A page that spells
    /// either out again has forked the dialect — which is how the two of them drifted apart across
    /// fourteen surfaces in the first place (issue #368). The shared copy is reachable through
    /// <c>Noun</c> / <c>EmptyTitle</c> / <c>EmptyDescription</c>; the whole-state <c>Empty</c> escape
    /// hatch stays for onboarding copy that genuinely needs markup.
    /// </summary>
    [Fact]
    public void No_page_hand_rolls_the_error_state()
    {
        var clientRoot = FindClientRoot();

        var violations = new List<string>();
        foreach (var file in EnumeratePageSources())
        {
            if (Path.GetFileName(file) == "OdsListStatus.razor")
                continue;

            var text = File.ReadAllText(file);
            foreach (Match hit in Regex.Matches(text, @"<OdsEmptyState[^>]*error_outline"))
            {
                var line = text[..hit.Index].Count(c => c == '\n') + 1;
                violations.Add($"{Path.GetRelativePath(clientRoot, file)}:{line} — hand-rolled error " +
                               "empty state; pass Error/OnRetry to OdsListStatus instead");
            }
        }

        Assert.True(violations.Count == 0,
            "Forked copies of the shared error state:\n" + string.Join('\n', violations));
    }

    /// <summary>
    /// A page that filters its list must offer a way back out of an over-filtered one. Nine of the
    /// nineteen didn't — <c>PhotosCard</c> went as far as telling the user to "try clearing a filter"
    /// while rendering no control that would. The filtered-empty state only shows its Clear filters
    /// button when <c>OnClearFilters</c> is bound, so the binding is the affordance.
    /// </summary>
    [Fact]
    public void Every_filtered_list_page_offers_a_clear_filters_control()
    {
        var clientRoot = FindClientRoot();

        var missing = ListPages
            .Select(page => (page, source: SourceFor(clientRoot, page)))
            .Where(p => p.source.Contains("HasFilters=\"") && !p.source.Contains("OnClearFilters=\""))
            .Select(p => p.page)
            .ToList();

        Assert.True(missing.Count == 0,
            "Filtered list pages whose over-filtered empty state offers no way out:\n" +
            string.Join('\n', missing));
    }

    /// <summary>
    /// The refetch bar is a status message (WCAG 2.2 §4.1.3) and its ARIA had forked 4-vs-4 between
    /// <c>role="status" aria-label</c> and <c>role="status" aria-live aria-busy</c>. It is now written
    /// once, in <c>OdsListStatus</c>; the remaining bars in the codebase are per-row detail spinners
    /// that carry <c>aria-hidden</c> and announce through their own wrapper.
    /// </summary>
    [Fact]
    public void The_refetch_bar_is_declared_in_exactly_one_place()
    {
        var clientRoot = FindClientRoot();

        var violations = new List<string>();
        foreach (var file in EnumeratePageSources())
        {
            if (Path.GetFileName(file) == "OdsListStatus.razor")
                continue;

            var text = File.ReadAllText(file);
            foreach (Match bar in Regex.Matches(text, @"<MudProgressLinear\b[^>]*>", RegexOptions.Singleline))
            {
                if (!bar.Value.Contains("role=\"status\""))
                    continue;

                var line = text[..bar.Index].Count(c => c == '\n') + 1;
                violations.Add($"{Path.GetRelativePath(clientRoot, file)}:{line} — second copy of the " +
                               "refetch bar; pass Refetching to OdsListStatus instead");
            }
        }

        Assert.True(violations.Count == 0,
            "Forked copies of the refetch bar:\n" + string.Join('\n', violations));
    }

    /// <summary>
    /// <c>OdsPager</c>'s <c>Page</c> is 1-based, and so is every page's own page counter — except
    /// <c>Users.razor</c>, which kept a 0-based <c>_page</c> and bridged the gap with
    /// <c>Page="_page + 1"</c> going out and <c>page - 1</c> coming back (issue #370). Two numbering
    /// bases in one page is the kind of thing that reads as correct in both directions and still
    /// produces an off-by-one the moment a third site touches the counter — the page's own
    /// announcement already had to say <c>_page + 1</c> to name the current page out loud.
    /// </summary>
    /// <remarks>
    /// The check is for arithmetic on the wire between the pager and the page, in either direction;
    /// a page whose counter is genuinely 1-based never needs any.
    /// </remarks>
    [Fact]
    public void No_list_page_adapts_between_OdsPager_and_a_0_based_page_counter()
    {
        var clientRoot = FindClientRoot();

        var violations = new List<string>();
        foreach (var page in ListPages)
        {
            var text = SourceFor(clientRoot, page);

            foreach (Match pager in Regex.Matches(text, @"<OdsPager\b[^>]*>", RegexOptions.Singleline))
            {
                var bound = Regex.Match(pager.Value, @"\bPage\s*=\s*""([^""]*)""");
                if (bound.Success && Regex.IsMatch(bound.Groups[1].Value, @"[+-]\s*1\b"))
                    violations.Add($"{page} — OdsPager Page=\"{bound.Groups[1].Value}\" offsets the " +
                                   "page number; make the page's own counter 1-based instead");
            }

            foreach (Match back in Regex.Matches(text, @"\bGoToPage\(\s*page\s*-\s*1\s*\)"))
            {
                var line = text[..back.Index].Count(c => c == '\n') + 1;
                violations.Add($"{page}:{line} — {back.Value} converts the pager's 1-based page to a " +
                               "0-based one; make the page's own counter 1-based instead");
            }
        }

        Assert.True(violations.Count == 0,
            "Pages translating between OdsPager's 1-based page and a 0-based counter:\n" +
            string.Join('\n', violations));
    }

    /// <summary>A page's markup plus its code-behind, if it has one.</summary>
    private static string SourceFor(string clientRoot, string page)
    {
        var razor = Path.Combine(clientRoot, page.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(razor), $"Registered list page not found: {page}");

        var codeBehind = razor + ".cs";
        return File.ReadAllText(razor) + (File.Exists(codeBehind) ? File.ReadAllText(codeBehind) : string.Empty);
    }

    /// <summary>
    /// Pages plus Components: a shared list surface such as <c>OdsTagAdmin</c> lives under
    /// <c>Components/</c> but owns a paged list and a delete, so scanning <c>Pages/</c> alone would let
    /// the list contracts lapse the moment a page is folded into a reusable component.
    /// </summary>
    private static IEnumerable<string> EnumeratePageSources() =>
        ClientSource.RazorFilesIn("Pages", "Components");

    private static string FindClientRoot() => ClientSource.Root;
}
