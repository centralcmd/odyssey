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
        "Pages/Finance/PropertiesCard.razor",
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

    /// <summary>
    /// Components that unwrap a list response but are deliberately NOT routed through
    /// <c>ListLoader</c>, each with the reason no two of its reads can ask different questions at once.
    /// Everything else that calls <c>PagedOrToast</c> / <c>ItemsOrToast</c> (or already holds a
    /// loader) is held to <see cref="Every_list_fetch_ignores_superseded_responses"/>. The set is derived
    /// rather than registered so a new list surface cannot hide by not being listed; this is the only
    /// list a reviewer has to read, and <see cref="Every_superseded_response_exemption_is_still_live"/>
    /// fails when an entry stops describing a real component.
    /// </summary>
    /// <remarks>
    /// Scope is Razor components (markup plus code-behind). A plain C# base class such as
    /// <c>FilesSectionBase</c> is reached through the components that derive from it.
    /// </remarks>
    private static readonly Dictionary<string, string> SupersededResponseExemptions = new()
    {
        ["Pages/Journal/TasksPage.razor"] = "loads every task once; search, tag and status filter client-side",
        ["Pages/Photos/AlbumsPage.razor"] = "loads every album once; search filters client-side",
        ["Pages/Finance/AccountContractsSection.razor"] = "one read per mounted account, on init",
        ["Pages/Finance/AccountEstimatesSection.razor"] = "one read per mounted account, on init and after its own writes",
        ["Pages/Finance/AccountTransactionsSection.razor"] = "one read per mounted account, on init and after its own row writes",
        ["Pages/Finance/BudgetTransactionsSection.razor"] = "parameter-driven reloads are gated on !_isLoading, so they never overlap; the other trigger is a write on its own rows",
        ["Pages/Finance/ContactAliasSection.razor"] = "seeds from the loaded contact; its list read follows its own writes",
        ["Pages/Finance/ContactDetailPanel.razor"] = "seeds from the loaded contact; its list reads follow its own writes",
        ["Pages/Finance/ContractTermsSection.razor"] = "one read per mounted contract, on init and after its own writes",
        ["Pages/Finance/PropertyContractsSection.razor"] = "one read per mounted property, on init",
        ["Pages/Finance/CreateTransactionDialog.razor"] = "reference data for the pickers, read once with no query input",
        ["Pages/Finance/Home.razor"] = "dashboard figures, read once with no query input",
        ["Pages/Photos/AddToAlbumDialog.razor"] = "every album, read on open and after its own create",
        ["Pages/Photos/AlbumFormDialog.razor"] = "one album's photos, read once per open",
        ["Pages/Photos/EditPhotoDialog.razor"] = "the tag list, read after its own create",
        ["Pages/Photos/PhotoLibraryOverview.razor"] = "whole-library reference data with no query input",
    };

    private static readonly Regex ListUnwrap = new(@"\b(?:PagedOrToast|ItemsOrToast|PagedItemsOrToast)\b|_listLoader\b");

    /// <summary>Every Razor component that unwraps a list response, keyed by its markup path.</summary>
    private static IReadOnlyDictionary<string, string> ListFetchingComponents() =>
        ClientSource.RazorFilesIn("Pages", "Components")
            .Where(file => file.EndsWith(".razor", StringComparison.Ordinal))
            .Select(file => (page: ClientSource.Relative(file).Replace('\\', '/'), source: SourceFor(ClientSource.Root, ClientSource.Relative(file).Replace('\\', '/'))))
            .Where(p => ListUnwrap.IsMatch(p.source))
            .ToDictionary(p => p.page, p => p.source);

    /// <summary>
    /// Search (debounced), filters, sort, page and page size all call one fetch, and before issue #249
    /// nothing cancelled or ignored a superseded request: a slower earlier response that landed last
    /// overwrote the newer one, leaving rows for a search the user had already replaced, a pager and
    /// announcement that disagreed with them, and the refetch bar cleared while the newest request was
    /// still running. <c>ListLoader</c> is the one guard.
    /// </summary>
    /// <remarks>
    /// Checked per <c>RunAsync</c> call, not per file, so a page with two fetches cannot pass on the
    /// strength of one: each call's result must be assigned to a local, that local's
    /// <c>IsSuperseded</c> must be tested in the SAME method and before any read of its <c>Value</c>
    /// (the transport reports a cancelled request as a failure, so unwrapping first would toast it),
    /// and the lambda must forward the loader's token. The loader must also be disposed with the
    /// component.
    /// </remarks>
    [Fact]
    public void Every_list_fetch_ignores_superseded_responses()
    {
        var violations = new List<string>();

        foreach (var (page, source) in ListFetchingComponents())
        {
            if (SupersededResponseExemptions.ContainsKey(page))
                continue;

            violations.AddRange(SupersededResponseViolations(source).Select(problem => $"{page} — {problem}"));
        }

        Assert.True(violations.Count == 0,
            "List fetches that can apply an out-of-order response:\n" + string.Join('\n', violations));
    }

    /// <summary>The registered list pages are all inside the derived set, and none is exempt by accident.</summary>
    [Fact]
    public void Every_registered_list_page_is_held_to_the_superseded_response_rule()
    {
        var derived = ListFetchingComponents();

        var missing = ListPages
            .Where(page => !derived.ContainsKey(page) && !SupersededResponseExemptions.ContainsKey(page))
            .ToList();

        Assert.True(missing.Count == 0,
            "Registered list pages the superseded-response lint cannot see:\n" + string.Join('\n', missing));
    }

    /// <summary>
    /// An exemption outlives its reason silently unless something checks it: the component must still
    /// exist, still unwrap a list, and still not hold a loader — once it adopts one it is held to the
    /// rule like everything else and the entry is dead weight.
    /// </summary>
    [Fact]
    public void Every_superseded_response_exemption_is_still_live()
    {
        var derived = ListFetchingComponents();

        var stale = SupersededResponseExemptions.Keys
            .Where(page => !derived.TryGetValue(page, out var source) || source.Contains("_listLoader"))
            .ToList();

        Assert.True(stale.Count == 0,
            "Exemptions naming a component that no longer unwraps a list, or that now uses ListLoader:\n" +
            string.Join('\n', stale));
    }

    /// <summary>The lint's own teeth: each shape it exists to refuse is refused.</summary>
    [Theory]
    [InlineData("no loader at all",
        "private async Task Load() { var r = await Api.ListAsync(); _rows = r.PagedOrToast(Snackbar, \"x\"); }")]
    [InlineData("check on the wrong fetch",
        "@implements IDisposable\nprivate readonly ListLoader _listLoader = new();\n" +
        "    private async Task Load()\n    {\n        var a = await _listLoader.RunAsync(ct => Api.ListAsync(ct));\n        if (a.IsSuperseded) return;\n" +
        "        var b = await _listLoader.RunAsync(ct => Api.OtherAsync(ct));\n        _rows = b.Value;\n    }\n" +
        "    public void Dispose() => _listLoader.Dispose();")]
    [InlineData("check in a different method",
        "@implements IDisposable\nprivate readonly ListLoader _listLoader = new();\n" +
        "    private async Task Load()\n    {\n        var a = await _listLoader.RunAsync(ct => Api.ListAsync(ct));\n        _rows = a.Value;\n    }\n" +
        "    private bool Other()\n    {\n        return a.IsSuperseded;\n    }\n" +
        "    public void Dispose() => _listLoader.Dispose();")]
    [InlineData("unwrap before the check",
        "@implements IDisposable\nprivate readonly ListLoader _listLoader = new();\n" +
        "    private async Task Load()\n    {\n        var a = await _listLoader.RunAsync(ct => Api.ListAsync(ct));\n        var load = a.Value.PagedOrToast(Snackbar, \"x\");\n        if (a.IsSuperseded) return;\n    }\n" +
        "    public void Dispose() => _listLoader.Dispose();")]
    [InlineData("token not forwarded",
        "@implements IDisposable\nprivate readonly ListLoader _listLoader = new();\n" +
        "    private async Task Load()\n    {\n        var a = await _listLoader.RunAsync(ct => Api.ListAsync(search: \"a;b\"));\n        if (a.IsSuperseded) return;\n    }\n" +
        "    public void Dispose() => _listLoader.Dispose();")]
    [InlineData("never disposed",
        "private readonly ListLoader _listLoader = new();\n" +
        "    private async Task Load()\n    {\n        var a = await _listLoader.RunAsync(ct => Api.ListAsync(ct));\n        if (a.IsSuperseded) return;\n    }\n")]
    public void The_superseded_response_lint_refuses(string shape, string source)
    {
        Assert.True(SupersededResponseViolations(source).Count > 0, $"The lint accepted: {shape}");
    }

    [Fact]
    public void The_superseded_response_lint_accepts_the_sanctioned_shape()
    {
        const string source =
            "@implements IDisposable\nprivate readonly ListLoader _listLoader = new();\n" +
            "    private async Task Load()\n    {\n        var a = await _listLoader.RunAsync(ct => Api.ListAsync(\n            search: \"a;b\",\n            ct: ct));\n" +
            "        if (a.IsSuperseded)\n            return;\n\n        var load = a.Value.PagedOrToast(Snackbar, \"x\");\n    }\n" +
            "    public void Dispose() => _listLoader.Dispose();";

        Assert.Empty(SupersededResponseViolations(source));
    }

    private static readonly Regex MethodHeader = new(
        @"^[ \t]*(?:(?:private|public|protected|internal|static|async|override|virtual)\s+)+[^\n;=]*\([^;{]*\)[ \t]*\r?\n[ \t]*\{",
        RegexOptions.Multiline);

    private static List<string> SupersededResponseViolations(string source)
    {
        var problems = new List<string>();

        if (!source.Contains("_listLoader"))
            return ["unwraps a list response without a ListLoader"];

        if (!source.Contains("private readonly ListLoader _listLoader = new();"))
            problems.Add("does not declare `private readonly ListLoader _listLoader = new();`");

        if (!source.Contains("_listLoader.Dispose()")
            || !Regex.IsMatch(source, @"@implements\s+I(?:Async)?Disposable\b|\bclass\s+\w+(?:<[^>]*>)?\s*:[^{]*\bI(?:Async)?Disposable\b"))
            problems.Add("does not dispose the loader with the component");

        var calls = Regex.Matches(source, @"_listLoader\.RunAsync\(");
        if (calls.Count == 0)
            problems.Add("declares a loader but never fetches through _listLoader.RunAsync");

        foreach (Match call in calls)
        {
            var line = ClientSource.LineAt(source, call.Index);
            var argsStart = call.Index + call.Length;
            var argsEnd = MatchingClose(source, argsStart - 1);
            var args = argsEnd < 0 ? string.Empty : source[argsStart..argsEnd];

            var lambda = Regex.Match(args, @"^\s*(?:async\s+)?(\w+)\s*=>");
            if (!lambda.Success
                || Regex.Matches(args[lambda.Length..], $@"\b{lambda.Groups[1].Value}\b").Count == 0)
                problems.Add($"line {line}: RunAsync does not forward the loader's token to the API call");

            var assigned = Regex.Match(source[..call.Index], @"var\s+(\w+)\s*=\s*await\s*$");
            if (!assigned.Success)
            {
                problems.Add($"line {line}: RunAsync's result is not assigned to a local, so its IsSuperseded cannot be checked");
                continue;
            }

            var method = EnclosingMethodEnd(source, call.Index);
            if (method < 0 || argsEnd < 0)
            {
                problems.Add($"line {line}: could not find the method enclosing RunAsync");
                continue;
            }

            var rest = source[argsEnd..method];
            var name = assigned.Groups[1].Value;
            var check = Regex.Match(rest, $@"\b{name}\.IsSuperseded\b");
            var firstValue = Regex.Match(rest, $@"\b{name}\.Value\b");
            if (!check.Success)
                problems.Add($"line {line}: `{name}.IsSuperseded` is never checked in the method that fetched it");
            else if (firstValue.Success && firstValue.Index < check.Index)
                problems.Add($"line {line}: `{name}.Value` is read before `{name}.IsSuperseded` is checked");
        }

        return problems;
    }

    /// <summary>The index of the closing brace of the method whose body contains <paramref name="index"/>.</summary>
    private static int EnclosingMethodEnd(string source, int index)
    {
        var header = MethodHeader.Matches(source[..index]).LastOrDefault();
        if (header is null)
            return -1;

        var close = MatchingClose(source, header.Index + header.Length - 1);
        return close > index ? close : -1;
    }

    /// <summary>
    /// The index of the bracket closing the one at <paramref name="open"/>, skipping comments and
    /// string and char literals so a <c>;</c>, <c>(</c> or <c>{</c> inside one is not mistaken for
    /// code — and an apostrophe in a comment is not mistaken for a char literal.
    /// </summary>
    private static int MatchingClose(string source, int open)
    {
        var (opener, closer) = source[open] == '(' ? ('(', ')') : ('{', '}');
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i = source.IndexOf('\n', i) is var eol and >= 0 ? eol : source.Length;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i = source.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 1 : source.Length;
                continue;
            }

            if (c == '\'' && Regex.Match(source[i..Math.Min(source.Length, i + 4)], @"^'(?:\\.|[^\\'])'") is { Success: true } ch)
            {
                i += ch.Length - 1;
                continue;
            }

            if (c == '"')
            {
                for (i++; i < source.Length && source[i] != '"'; i++)
                {
                    if (source[i] == '\\')
                        i++;
                }
                continue;
            }

            if (c == opener)
                depth++;
            else if (c == closer && --depth == 0)
                return i;
        }

        return -1;
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
