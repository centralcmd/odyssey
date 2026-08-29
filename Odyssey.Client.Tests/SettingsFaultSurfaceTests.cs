using System.Text.RegularExpressions;
using Odyssey.Dtos.Application;
using Odyssey.Client.Pages;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// AC 30 — the two page-level fault surfaces (issue #437 Goal 12), and the three counts that must
/// reconcile between them.
///
/// <para>
/// The <strong>announcement</strong> is filter-blind and capped; the <strong>summary</strong> is
/// filter-aware and persistent. They cannot share a signature, and that difference is the whole design:
/// a fault is something the administrator did not cause, so the announcement must name it whether or
/// not a filter happens to be hiding its row — while the persistent record has to say how many are
/// hidden, or a returning administrator reads a count they cannot reconcile against the screen.
/// </para>
///
/// <para>
/// Both are <c>internal static</c> pure functions rather than private instance members reading page
/// state. When they were written this project had no renderer at all; it has bUnit now (issue #444),
/// but they stay pure functions — the negative cases below are combinatorial, and driving each one
/// through a full page render would cost far more than it proves.
/// </para>
/// </summary>
public class SettingsFaultSurfaceTests
{
    private const string Window = nameof(SystemSettingsUpdate.SubscriptionRenewalWindowDays);
    private const string Renewals = nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals);
    private const string Fetch = nameof(SystemSettingsUpdate.SubscriptionMaxSummarySubscriptions);
    private const string InsuranceWindow = nameof(SystemSettingsUpdate.InsuranceExpiringSoonWindowDays);
    private const string InsuranceMax = nameof(SystemSettingsUpdate.InsuranceMaxSummaryPolicies);

    /// <summary>
    /// Built with the DEFAULT comparer, matching what System.Text.Json actually hands the client: it
    /// constructs a fresh dictionary for an <c>IReadOnlyDictionary</c> property and discards the
    /// initializer's comparer. A suite building its inputs with <c>OrdinalIgnoreCase</c> would prove a
    /// lookup the runtime never performs.
    /// </summary>
    private static Dictionary<string, SettingFaultKind> Faults(
        params (string Field, SettingFaultKind Kind)[] entries) =>
        entries.ToDictionary(entry => entry.Field, entry => entry.Kind);

    // ── The announcement ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_faults_produces_no_announcement()
    {
        Assert.Null(Settings.FaultAnnouncement(Faults(), Settings.AllItems));
    }

    /// <summary>
    /// The singular form is pinned separately from the plural. A one-fault page is the likeliest real
    /// case, and an unpinned singular renders "1 settings aren't using their stored value".
    /// </summary>
    [Fact]
    public void One_fault_is_announced_in_the_singular()
    {
        var announcement = Settings.FaultAnnouncement(
            Faults((Window, SettingFaultKind.Unreadable)), Settings.AllItems);

        Assert.Equal(
            "1 setting isn't using its stored value: Upcoming renewals window (couldn't be read). "
            + "Use Go to first fault to reach it.",
            announcement);
    }

    /// <summary>
    /// The two- and three-item forms are written out rather than expressed as a deletion from the
    /// capped one: describing one form by deletion from another is what leaves the separator before the
    /// closing sentence, the comma when the remainder clause is dropped, and the conjunction all
    /// undetermined.
    /// </summary>
    [Fact]
    public void Two_faults_are_announced_without_a_conjunction()
    {
        var announcement = Settings.FaultAnnouncement(
            Faults((Window, SettingFaultKind.Unreadable), (Fetch, SettingFaultKind.Clamped)),
            Settings.AllItems);

        Assert.Equal(
            "2 settings aren't using their stored value: Upcoming renewals window (couldn't be read), "
            + "Max subscriptions read for summary (is outside its allowed range). "
            + "Use Go to first fault to reach them.",
            announcement);
    }

    [Fact]
    public void Three_faults_are_announced_with_no_remainder_clause()
    {
        var announcement = Settings.FaultAnnouncement(
            Faults(
                (Window, SettingFaultKind.Unreadable),
                (Renewals, SettingFaultKind.Clamped),
                (Fetch, SettingFaultKind.Clamped)),
            Settings.AllItems);

        Assert.Equal(
            "3 settings aren't using their stored value: Upcoming renewals window (couldn't be read), "
            + "Max renewals shown in summary (is outside its allowed range), "
            + "Max subscriptions read for summary (is outside its allowed range). "
            + "Use Go to first fault to reach them.",
            announcement);
    }

    /// <summary>
    /// <strong>The cap is load-bearing, not belt-and-braces.</strong> <c>OdsLiveAnnouncer</c> is
    /// <c>aria-live="polite"</c> with <c>aria-atomic="true"</c>, so the whole message is re-spoken as
    /// one utterance on every change — and this page deliberately defeats the usual "unchanged text is
    /// not re-spoken" mitigation with a zero-width-space parity suffix. The message re-fires on load
    /// AND after every successful save, so without the cap an administrator repairing rows one at a
    /// time would hear the entire remaining list re-read after each save, with no way to skip it.
    /// </summary>
    [Fact]
    public void Four_or_more_faults_are_capped_at_three_named_settings()
    {
        var announcement = Settings.FaultAnnouncement(
            Faults(
                (Window, SettingFaultKind.Unreadable),
                (Renewals, SettingFaultKind.Unreadable),
                (Fetch, SettingFaultKind.Unreadable),
                (InsuranceWindow, SettingFaultKind.Unreadable),
                (InsuranceMax, SettingFaultKind.Unreadable)),
            Settings.AllItems);

        Assert.NotNull(announcement);
        Assert.StartsWith("5 settings aren't using their stored value: ", announcement, StringComparison.Ordinal);
        Assert.EndsWith(", and 2 more. Use Go to first fault to reach them.", announcement, StringComparison.Ordinal);

        // Exactly three are named, and the remainder is count - 3.
        Assert.Equal(3, Regex.Matches(announcement!, @"\(couldn't be read\)").Count);
    }

    /// <summary>
    /// <strong>Which</strong> three, not just how many: unreadable before clamped, catalogue order
    /// within each. Without a stated order a test can assert "at most three" but not which, and the
    /// user-facing choice matters. The order matches the log levels — unreadable is an error, clamped a
    /// warning.
    /// </summary>
    [Fact]
    public void Unreadable_settings_are_named_before_clamped_ones()
    {
        // The insurance rows come FIRST in the catalogue, so catalogue order alone would name them; the
        // kind has to outrank it.
        var announcement = Settings.FaultAnnouncement(
            Faults(
                (InsuranceWindow, SettingFaultKind.Clamped),
                (InsuranceMax, SettingFaultKind.Clamped),
                (Window, SettingFaultKind.Unreadable),
                (Fetch, SettingFaultKind.Unreadable)),
            Settings.AllItems);

        Assert.NotNull(announcement);
        Assert.StartsWith(
            "4 settings aren't using their stored value: Upcoming renewals window (couldn't be read), "
            + "Max subscriptions read for summary (couldn't be read), ",
            announcement,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The announcement is computed over the DTO's faults, never over the rendered sections — it takes
    /// no search term at all, so it cannot become filter-aware by accident. The page's own precedent
    /// pulls the other way, which is why this is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void The_announcement_takes_no_search_term()
    {
        var parameters = typeof(Settings)
            .GetMethod(
                nameof(Settings.FaultAnnouncement),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(string));
    }

    // ── The summary ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_faults_produces_no_summary()
    {
        Assert.Null(Settings.FaultSummaryText(Faults(), Settings.Sections, string.Empty));
    }

    [Fact]
    public void The_summary_reports_the_total_with_no_filter()
    {
        Assert.Equal(
            "2 settings aren't using their stored value.",
            Settings.FaultSummaryText(
                Faults((Window, SettingFaultKind.Unreadable), (Fetch, SettingFaultKind.Clamped)),
                Settings.Sections,
                string.Empty));
    }

    /// <summary>
    /// <strong>The discriminating case.</strong> A filter hiding SOME but not all faulted rows must
    /// still report the hidden count — a boolean "is any faulted row rendered" reports the total with no
    /// hidden clause here, so with six faults and a filter matching one it says "6 settings" while five
    /// are unreachable. A sighted user might reconcile "6" against the screen; a screen-reader user
    /// would have to traverse every row counting advisory nodes.
    /// </summary>
    [Fact]
    public void The_summary_counts_hidden_rows_even_when_one_is_visible()
    {
        var summary = Settings.FaultSummaryText(
            Faults(
                (Window, SettingFaultKind.Unreadable),
                (Renewals, SettingFaultKind.Clamped),
                (InsuranceMax, SettingFaultKind.Clamped)),
            Settings.Sections,
            // Matches the Subscriptions group name, so the two subscription rows stay visible and the
            // insurance one is hidden.
            "Subscriptions");

        Assert.Equal(
            "3 settings aren't using their stored value. 1 of them is hidden by the current search.",
            summary);
    }

    [Fact]
    public void The_summary_pluralises_the_hidden_count()
    {
        var summary = Settings.FaultSummaryText(
            Faults(
                (Window, SettingFaultKind.Unreadable),
                (InsuranceWindow, SettingFaultKind.Clamped),
                (InsuranceMax, SettingFaultKind.Clamped)),
            Settings.Sections,
            "Subscriptions");

        Assert.Equal(
            "3 settings aren't using their stored value. 2 of them are hidden by the current search.",
            summary);
    }

    /// <summary>
    /// A filter matching nothing at all: the sections container is replaced by the empty state exactly
    /// then, which is why this block is rendered outside that branch.
    /// </summary>
    [Fact]
    public void The_summary_survives_a_filter_that_hides_every_faulted_row()
    {
        var summary = Settings.FaultSummaryText(
            Faults((Window, SettingFaultKind.Unreadable), (Fetch, SettingFaultKind.Clamped)),
            Settings.Sections,
            "zzzz-matches-nothing");

        Assert.Equal(
            "2 settings aren't using their stored value. 2 of them are hidden by the current search.",
            summary);
    }

    /// <summary>
    /// The two singular forms, added because the earlier three cases all sat in the plural branch — so
    /// the forms that had rendered "1 settings aren't using their stored value" were covered by nothing.
    /// </summary>
    [Fact]
    public void The_summary_pins_both_singular_forms()
    {
        Assert.Equal(
            "1 setting isn't using its stored value.",
            Settings.FaultSummaryText(
                Faults((Window, SettingFaultKind.Unreadable)), Settings.Sections, string.Empty));

        Assert.Equal(
            "1 setting isn't using its stored value. It is hidden by the current search.",
            Settings.FaultSummaryText(
                Faults((Window, SettingFaultKind.Unreadable)), Settings.Sections, "zzzz-matches-nothing"));
    }

    /// <summary>
    /// <strong>Two sentences, no dash.</strong> NVDA and JAWS speak neither an em dash nor a hyphen at
    /// default punctuation, so a joined clause renders as "…their stored value 2 hidden by the current
    /// search", running the two numbers together — on the persistent, browse-mode record that exists
    /// precisely so a screen-reader administrator can find the faults without the announcement.
    /// </summary>
    [Fact]
    public void The_summary_joins_its_clauses_with_a_sentence_break()
    {
        var summary = Settings.FaultSummaryText(
            Faults((Window, SettingFaultKind.Unreadable), (Fetch, SettingFaultKind.Clamped)),
            Settings.Sections,
            "zzzz-matches-nothing")!;

        Assert.DoesNotContain('—', summary);
        Assert.DoesNotContain('–', summary);
        Assert.DoesNotContain(" - ", summary, StringComparison.Ordinal);
        Assert.Contains(". ", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The verb covers BOTH kinds. "Could not be read as stored" is false for a clamped row, whose
    /// value was read perfectly well — and the fault set is the union of the two kinds, so a verb true
    /// of only one of them is the same defect one surface over.
    /// </summary>
    [Fact]
    public void The_summarys_verb_is_true_of_a_clamped_row_too()
    {
        var summary = Settings.FaultSummaryText(
            Faults((Fetch, SettingFaultKind.Clamped)), Settings.Sections, string.Empty)!;

        Assert.Equal("1 setting isn't using its stored value.", summary);
        Assert.DoesNotContain("read", summary, StringComparison.OrdinalIgnoreCase);
    }

    // ── The three page-level counts reconcile ───────────────────────────────────────────────────

    /// <summary>
    /// Precedence puts each projection advisory INTO <c>Warnings</c>, so counting that map
    /// unconditionally counts every fault twice and then tells the administrator it "does not block
    /// saving" — the clause that is wrong for a fault. On a page with three cost advisories and two
    /// faults they would hear "5 advisories …", then a fault sentence naming two of those five, while
    /// the summary rendered "2".
    /// </summary>
    [Fact]
    public void The_advisory_count_excludes_the_faults()
    {
        var warnings = new Dictionary<string, string>
        {
            ["A"] = "cost", ["B"] = "cost", ["C"] = "cost",
            [Window] = "fault", [Fetch] = "fault",
        };

        var faults = Faults((Window, SettingFaultKind.Unreadable), (Fetch, SettingFaultKind.Clamped));

        Assert.Equal(3, Settings.CostAdvisoryCount(warnings, faults));
        Assert.Equal(2, Settings.FaultSummaryText(faults, Settings.Sections, string.Empty) is not null ? 2 : 0);
    }

    [Fact]
    public void The_advisory_count_is_the_whole_map_when_nothing_is_faulted()
    {
        var warnings = new Dictionary<string, string> { ["A"] = "cost", ["B"] = "cost" };

        Assert.Equal(2, Settings.CostAdvisoryCount(warnings, Faults()));
    }

    // ── Source lints: render placement and focus, which a pure function cannot prove ─────────────

    private static string Page() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor"));

    private static string CodeBehind() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));

    /// <summary>
    /// The fault summary must be visible to a caller holding only <c>system-settings.read</c>, and must
    /// survive a search that matches nothing.
    ///
    /// <para>
    /// This is a render-placement property, so it cannot be a pure-function assertion: the functions
    /// take no claim and return a string, and <c>CanSave</c> is a private instance property. Asserted
    /// as a source lint instead, the shape this repository already prescribes for the client-side cap
    /// rules.
    /// </para>
    ///
    /// <para>
    /// <strong>The gate is stated literally, because the neighbouring banner's gate is the opposite
    /// one.</strong> <c>BlockingSummary</c> sits inside <c>@if (CanSave)</c> — placing this beside it
    /// leaves a read-only caller with no record at all — while <c>ss-note</c> is <c>!CanSave</c>, so
    /// reusing THAT condition produces the same hole with the sign flipped, visible only to viewers who
    /// cannot repair anything. The claim-blind neighbour is <c>ss-lastchanged</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void The_fault_summary_is_gated_on_the_phase_alone()
    {
        var page = Page();

        Assert.Contains(
            "@if (_phase == Phase.Ready && FaultSummary is { } faultSummary)",
            page,
            StringComparison.Ordinal);

        // It is a page-level block, not nested inside the primary action (where BlockingSummary lives)
        // or inside the sections branch (which the empty state replaces).
        var blockIndex = page.IndexOf("FaultSummary is { } faultSummary", StringComparison.Ordinal);
        var primaryActionEnd = page.IndexOf("</PageHeader>", StringComparison.Ordinal);
        // The branch condition gained a second disjunct when the Credentials group landed (issue #444),
        // so this matches the branch's OPENING rather than its exact full condition. The property being
        // pinned is unchanged: the fault block sits BEFORE the sections branch, outside it.
        var sectionsBranch = page.IndexOf("else if (VisibleSections.Count > 0", StringComparison.Ordinal);

        Assert.True(blockIndex > primaryActionEnd, "FaultSummary is nested inside the page header.");
        Assert.True(blockIndex < sectionsBranch, "FaultSummary is nested inside the sections branch.");
    }

    /// <summary>
    /// A page-owned element, not an <c>Ods*</c> wrapper carrying a forwarded class. A scoped rule keyed
    /// on a class forwarded into a child component never matches — that is exactly why
    /// <c>.ss-row.ss-disabled</c> was dead — and adding a third instance while citing the first two
    /// would be careless.
    /// </summary>
    [Fact]
    public void The_fault_summary_is_a_page_declared_element()
    {
        Assert.Contains("<div class=\"ss-faults\"", Page(), StringComparison.Ordinal);
        Assert.Contains(
            ".ss-faults {",
            File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.css")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The jump target must be FOCUSABLE, or the control moves no focus at all:
    /// <c>window.odsFocusById</c> is a bare <c>.focus()</c>, a silent no-op on a non-focusable node.
    /// This also repairs <c>JumpToFirstError</c>'s row branch, broken today for the same reason — its
    /// section branch works only because <c>ss-rtalert</c> carries an explicit <c>tabindex</c>.
    ///
    /// <para>
    /// The attribute is CONDITIONAL on <c>TitleId</c>, so this cannot copy the model lint's literal
    /// <c>tabindex="-1"</c> assertion.
    /// </para>
    /// </summary>
    [Fact]
    public void The_row_title_is_focusable_when_it_is_a_jump_target()
    {
        var component = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Components", "OdsSettingRow.razor"));

        var title = Regex.Match(component, @"<div class=""odc-setting-ttl""[^>]*>");
        Assert.True(title.Success, "The row title element is gone — this lint is stale, not the code.");
        Assert.Contains("tabindex=", title.Value, StringComparison.Ordinal);
        Assert.Contains("TitleId", title.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A programmatic focus target with no indicator moves focus invisibly. This project declares focus
    /// rings per selector — there is no catch-all and MudBlazor ships no global reset — so the new
    /// landing target must not be the one place relying on the UA default.
    ///
    /// <para>
    /// It lives in the COMPONENT's own sheet, because the element is declared there: putting it on the
    /// page would be a fourth instance of the dead-scoped-rule defect.
    /// </para>
    /// </summary>
    [Fact]
    public void The_row_title_carries_a_focus_ring()
    {
        var css = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Components", "OdsSettingRow.razor.css"));

        Assert.Contains(".odc-setting-ttl:focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("outline:", css[css.IndexOf(".odc-setting-ttl:focus-visible", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The control mutates state BEFORE the interop — it clears the search term and reopens the region —
    /// so its target row is not in the DOM when the handler starts, and <c>odsFocusById</c> no-ops
    /// silently on a missing id too.
    ///
    /// <para>
    /// The handler is NAMED, because this file has two other <c>odsFocusById</c> call sites and one
    /// deliberately omits <c>StateHasChanged()</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void Go_to_first_fault_renders_before_it_moves_focus()
    {
        var codeBehind = CodeBehind();

        var start = codeBehind.IndexOf("private async Task GoToFirstFault()", StringComparison.Ordinal);
        Assert.True(start >= 0, "GoToFirstFault is gone — this lint is stale, not the code.");

        var end = codeBehind.IndexOf("\n    private", start + 40, StringComparison.Ordinal);
        var handler = end < 0 ? codeBehind[start..] : codeBehind[start..end];

        var render = handler.IndexOf("StateHasChanged();", StringComparison.Ordinal);
        var focus = handler.IndexOf("odsFocusById", StringComparison.Ordinal);

        Assert.True(render >= 0, "GoToFirstFault does not call StateHasChanged().");
        Assert.True(focus >= 0, "GoToFirstFault does not move focus.");
        Assert.True(render < focus, "GoToFirstFault moves focus before rendering the row it targets.");

        // It clears the search term itself rather than pointing at the search field's clear button:
        // the header renders its search content only while the region is open, and collapsing it does
        // not clear the term — so an administrator can arrive with a filter applied and no input on
        // screen at all, and both are persisted page state.
        Assert.Contains("OnSearchChanged(string.Empty)", handler, StringComparison.Ordinal);
        Assert.Contains("_searchOpen = true", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// The load/save announcement routes its advisory count through the extracted function, so the
    /// three-page-level-numbers defect cannot be reintroduced by one edit with every test still green —
    /// <c>AdvisorySuffixed</c> is a private instance member no extraction returns.
    /// </summary>
    [Fact]
    public void The_announcement_counts_cost_advisories_through_the_extracted_function()
    {
        var codeBehind = CodeBehind();

        var start = codeBehind.IndexOf("private string AdvisorySuffixed(", StringComparison.Ordinal);
        Assert.True(start >= 0, "AdvisorySuffixed is gone — this lint is stale, not the code.");

        var end = codeBehind.IndexOf("\n    private static readonly", start, StringComparison.Ordinal);
        var method = end < 0 ? codeBehind[start..] : codeBehind[start..end];

        Assert.Contains("CostAdvisoryCount(", method, StringComparison.Ordinal);
        Assert.Contains("FaultAnnouncement(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_dto?.Warnings.Count", method, StringComparison.Ordinal);
    }

    /// <summary>
    /// The announcer is hosted OUTSIDE every phase branch, so the region pre-exists its first message
    /// and the load announcement is a mutation into a live region rather than a region inserted with its
    /// content. Move it inside <c>Phase.Ready</c> and every load-time announcement — including the
    /// fault announcement, the only one that fires on load — stops firing silently, with no test
    /// failing.
    /// </summary>
    [Fact]
    public void The_live_announcer_is_hosted_outside_every_phase_branch()
    {
        var page = Page();

        var announcer = page.IndexOf("<OdsLiveAnnouncer", StringComparison.Ordinal);
        var emptyState = page.IndexOf("<OdsEmptyState", StringComparison.Ordinal);

        Assert.True(announcer > emptyState,
            "OdsLiveAnnouncer has moved inside a phase branch; load-time announcements would stop firing.");
        Assert.Contains("</MudStack>", page[announcer..], StringComparison.Ordinal);
    }
}
