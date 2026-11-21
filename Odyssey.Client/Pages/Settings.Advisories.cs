using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages;

public partial class Settings
{
    // ── Non-blocking advisories (issue #434 §3) ───────────────────────────────────────────────────
    //
    // Server-authored, keyed by the SystemSettingsUpdate property name — the SAME join key
    // ApiProblem.Errors uses, so the field→row lookup below is the one _serverErrors already needs and
    // not a second mapping table.
    //
    // An advisory is not an error. It does not set aria-invalid, does not appear in ErrorFor, does not
    // count toward BlockingSummary and does not disable Save. It renders in OdsSettingRow's own
    // Advisory slot — never Footer, which is already occupied on fileAnalysisProcessor, the one row the
    // correspondence heuristic targets.

    private static string AdvisoryId(string key) => $"ss-advisory-{key}";

    /// <summary>
    /// Appends an advisory count to a load/save announcement.
    ///
    /// <para>
    /// Routed through the page's existing <c>OdsLiveAnnouncer</c> rather than giving the advisory
    /// element <c>role="status"</c>: a live region inserted into the DOM at the same time as its content
    /// is frequently not announced at all, and <c>role="alert"</c> stays reserved for the field-error
    /// path — an advisory must not interrupt. The message names the count and points at the rows, and
    /// each advisory's full text is reachable from its own field's <c>aria-describedby</c>.
    /// </para>
    /// </summary>
    private string AdvisorySuffixed(string message)
    {
        var faults = _dto?.ProjectionFaults ?? EmptyFaults;
        var count = CostAdvisoryCount(_dto?.Warnings ?? EmptyWarnings, faults);

        // The client-authored origin hint is an advisory like any other on the page, so it counts —
        // otherwise the spoken total disagrees with what the page renders, which is the exact defect
        // CostAdvisoryCount exists to avoid in the other direction. It cannot double-count: it is only
        // shown when the server left this field's advisory slot empty.
        if (OriginMismatchHint is not null)
        {
            count++;
        }

        var suffixed = count == 0
            ? message
            : $"{message} {count} {(count == 1 ? "advisory" : "advisories")} on this page — "
              + "each is shown under its own setting and does not block saving.";

        // A separate clause, never folded into the count above: "does not block saving" describes a
        // value the administrator chose, and is the wrong thing to say about a fault they did not cause.
        return FaultAnnouncement(faults, AllItems) is { } faultClause
            ? $"{suffixed} {faultClause}"
            : suffixed;
    }

    private static readonly IReadOnlyDictionary<string, SettingFaultKind> EmptyFaults =
        new Dictionary<string, SettingFaultKind>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> EmptyWarnings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The advisories that describe a value the administrator <em>chose</em> — the ones the "does not
    /// block saving" wording is true of.
    ///
    /// <para>
    /// Precedence puts each projection advisory INTO <c>Warnings</c> (the channel holds one string per
    /// field), so counting <c>Warnings.Count</c> unconditionally would count every fault twice and then
    /// tell the administrator it does not block saving. On a page with three cost advisories and two
    /// faults they would hear "5 advisories …", then a fault sentence naming two of those five, while
    /// the page's own summary rendered "2".
    /// </para>
    /// </summary>
    internal static int CostAdvisoryCount(
        IReadOnlyDictionary<string, string> warnings,
        IReadOnlyDictionary<string, SettingFaultKind> faults) =>
        warnings.Count - warnings.Keys.Count(faults.ContainsKey);

    /// <summary>
    /// The load/save announcement's fault clause, or null when nothing is faulted.
    ///
    /// <para>
    /// <strong>Filter-blind, deliberately</strong> — it is computed over the DTO's faults, never over
    /// the rendered sections. The page's own precedent pulls the other way (<c>BlockingSummary</c> and
    /// <c>JumpToFirstError</c> both scope to what is visible), and that is right for a validation error
    /// the administrator just created and wrong for a fault they did not cause.
    /// </para>
    ///
    /// <para>
    /// <strong>Capped at three named settings.</strong> <c>OdsLiveAnnouncer</c> is
    /// <c>aria-live="polite"</c> with <c>aria-atomic="true"</c>, so the whole message is re-spoken as
    /// one utterance on every change — and this page deliberately defeats the usual "unchanged text is
    /// not re-spoken" mitigation with a zero-width-space parity suffix, so the cap is load-bearing
    /// rather than belt-and-braces. It re-fires on load AND after every successful save, so an
    /// administrator repairing rows one at a time would otherwise hear the entire remaining list
    /// re-read after each save with no way to skip it.
    /// </para>
    ///
    /// <para>
    /// It carries titles and kind-tags, never the server's predicates: with the range interpolated,
    /// three clamped rows run about eighty words. The terms of the violation stay on the row, which is
    /// where they are acted on. The closing sentence points at the CONTROL rather than at the rows,
    /// because the control is the thing that works under a filter — it clears the search term itself.
    /// </para>
    /// </summary>
    internal static string? FaultAnnouncement(
        IReadOnlyDictionary<string, SettingFaultKind> faults,
        IEnumerable<SettingItem> items)
    {
        var named = FaultedItems(faults, items);
        if (named.Count == 0)
        {
            return null;
        }

        // "is outside its allowed range" rather than "outside its allowed range": both tags must
        // concatenate onto the title as a SENTENCE, because parentheses are silent at default screen
        // reader punctuation levels. A verb-less prepositional phrase degrades to a fragment.
        static string Tag(SettingFaultKind kind) =>
            kind == SettingFaultKind.Unreadable ? "couldn't be read" : "is outside its allowed range";

        var parts = named.Take(3).Select(entry => $"{entry.Item.Title} ({Tag(entry.Kind)})").ToList();
        var count = named.Count;

        if (count == 1)
        {
            return $"1 setting isn't using its stored value: {parts[0]}. Use Go to first fault to reach it.";
        }

        var listed = count > 3
            ? $"{string.Join(", ", parts)}, and {count - 3} more"
            : string.Join(", ", parts);

        return $"{count} settings aren't using their stored value: {listed}. Use Go to first fault to reach them.";
    }

    /// <summary>
    /// The faulted rows, unreadable before clamped and in catalogue order within each. The order is
    /// stated rather than incidental: the announcement names at most three, so <em>which</em> three is
    /// a user-facing choice, and it matches the log levels — unreadable is an error, clamped a warning.
    /// </summary>
    private static List<(SettingItem Item, SettingFaultKind Kind)> FaultedItems(
        IReadOnlyDictionary<string, SettingFaultKind> faults, IEnumerable<SettingItem> items)
    {
        if (faults.Count == 0)
        {
            return [];
        }

        var matched = items
            .Where(item => item.Field is not null)
            .Select(item => (Item: item, Found: faults.TryGetValue(item.Field!, out var kind), Kind: kind))
            .Where(entry => entry.Found)
            .Select(entry => (entry.Item, entry.Kind))
            .ToList();

        return
        [
            .. matched.Where(entry => entry.Kind == SettingFaultKind.Unreadable),
            .. matched.Where(entry => entry.Kind != SettingFaultKind.Unreadable),
        ];
    }

    /// <summary>
    /// The persistent, non-live record of the faulted rows, or null when there are none.
    ///
    /// <para>
    /// <strong>Filter-AWARE, and it counts the hidden rows rather than testing whether any is
    /// visible.</strong> A boolean "is any faulted row rendered" reports the total with no hidden
    /// clause whenever a filter matches even one of them — with six faults and a filter matching one it
    /// says "6 settings" while five are unreachable, which is the exact harm this exists to close. A
    /// sighted user might reconcile "6" against the screen; a screen-reader user would have to traverse
    /// every row counting advisory nodes.
    /// </para>
    ///
    /// <para>
    /// <strong>Two sentences, no dash, every form written out.</strong> NVDA and JAWS speak neither an
    /// em dash nor a hyphen at default punctuation, so a joined clause runs the two numbers together —
    /// and this is the browse-mode record that exists precisely so a screen-reader administrator can
    /// find the faults without the announcement. The singular forms are pinned separately because a
    /// one-fault page is the likeliest real case.
    /// </para>
    ///
    /// <para>
    /// The verb covers both kinds. "Could not be read as stored" is false for a clamped row, whose
    /// value was read perfectly well; "aren't using their stored value" is true of both, and matches the
    /// announcement's opening.
    /// </para>
    /// </summary>
    internal static string? FaultSummaryText(
        IReadOnlyDictionary<string, SettingFaultKind> faults,
        IReadOnlyList<SettingSection> sections,
        string search)
    {
        var faulted = sections
            .SelectMany(section => section.Items.Select(item => (Item: item, section.Group)))
            .Where(entry => entry.Item.Field is not null && faults.ContainsKey(entry.Item.Field!))
            .ToList();

        var count = faulted.Count;
        if (count == 0)
        {
            return null;
        }

        var hidden = faulted.Count(entry => !Matches(entry.Item, entry.Group, search));

        if (count == 1)
        {
            return hidden == 0
                ? "1 setting isn't using its stored value."
                : "1 setting isn't using its stored value. It is hidden by the current search.";
        }

        return hidden switch
        {
            0 => $"{count} settings aren't using their stored value.",
            1 => $"{count} settings aren't using their stored value. 1 of them is hidden by the current search.",
            _ => $"{count} settings aren't using their stored value. {hidden} of them are hidden by the current search.",
        };
    }

    /// <summary>The page-level fault record, rendered whenever the page is Ready and a fault exists.</summary>
    private string? FaultSummary =>
        FaultSummaryText(_dto?.ProjectionFaults ?? EmptyFaults, Sections, _search);

    /// <summary>
    /// Moves focus to the first faulted row, clearing the search term on the way.
    ///
    /// <para>
    /// It <strong>clears the term itself</strong> rather than pointing at the search field's clear
    /// button: <c>PageHeader</c> renders its search content only while the region is open, and
    /// collapsing the region does not clear the term — so an administrator can arrive with a filter
    /// applied and no input, or clear button, on screen at all. Both the term and the region's open
    /// state are persisted page state, so that is the normal landing state rather than an edge case.
    /// </para>
    ///
    /// <para>
    /// <c>StateHasChanged()</c> before the interop is required, not defensive: unlike
    /// <see cref="JumpToFirstError"/> this control mutates state first, so its target row is not in the
    /// DOM when the handler starts, and <c>odsFocusById</c> is a bare <c>.focus()</c> that no-ops
    /// silently on a missing id.
    /// </para>
    /// </summary>
    private async Task GoToFirstFault()
    {
        var faults = _dto?.ProjectionFaults ?? EmptyFaults;
        var first = FaultedItems(faults, AllItems).FirstOrDefault().Item;
        if (first is null)
        {
            return;
        }

        _searchOpen = true;
        OnSearchChanged(string.Empty);
        StateHasChanged();

        try
        {
            await JS.InvokeVoidAsync("odsFocusById", TitleId(first.Key));
        }
        catch
        {
            // The element isn't rendered (a concurrent re-render) — non-fatal; the summary still says
            // how many rows are affected.
        }
    }

    private string? AdvisoryText(SettingItem item)
    {
        // A guidance row whose stored value cannot be used says so in its own words, in the advisory
        // slot (the fault replaces the advisory; it has no separate id). "Not being sent" is the fact
        // that matters: the analysis runs without it.
        if (item.Control == SettingControl.LongText && item.Field is { } faultField
            && _dto?.ProjectionFaults is { Count: > 0 } rowFaults && rowFaults.ContainsKey(faultField))
        {
            return "This stored guidance can't be used and is not being sent. "
                   + (Editable(item.Claim) ? "Save a new value or clear it." : "An administrator can replace it.");
        }

        if (item.Field is { } field && _dto?.Warnings is { Count: > 0 } warnings
            && warnings.TryGetValue(field, out var message))
        {
            return message;
        }

        // The CLIENT-AUTHORED advisories (issue #8 §3.3). Everything else in this channel is
        // server-authored, and these are not exceptions made for convenience: the server cannot
        // compute either one. AdvisoryContext is deliberately empty — no DbContext, no HttpContext,
        // no services — and Advise delegates run on every GET rather than after a save, so a
        // server-side version could see neither the caller's origin nor the page's unsaved draft.
        //
        // They fall BELOW the server channel, never above it: a projection fault on a row reports a
        // stored value that cannot be used, which outranks either of these.
        if (item.Key == ClientBaseUrlKey && OriginMismatchHint is { } hint)
        {
            return hint;
        }

        if (item.StandingAdvisory is { } standing)
        {
            return standing;
        }

        // The design system's on-row warning: while one of the three clearing rows is dirty in its
        // clearing direction, EVERY one of them says what the save will cost. It is deliberately not
        // limited to the row that tripped it — the credential is cleared once for the whole save, so
        // a reader looking at any of the three should see the same answer.
        //
        // The dialog alone was not enough: it appears at Save, after the reader has finished editing
        // and formed an expectation. This is the cost stated where the decision is being made.
        return ClearingRowKeys.Contains(item.Key) && WillClearRelayCredential
            ? "Saving this clears the stored SMTP username and password, in the same transaction as "
              + $"the change. Re-enter them in {RelayCredentialGroup} afterwards; until you do, mail is "
              + "sent unauthenticated and any relay that requires a login will reject it."
            : null;
    }

    /// <summary>
    /// The card the relay credentials render in, read off <see cref="SecretCatalogue"/> rather than
    /// written as a literal: there is no "Credentials" group any more — secrets sit in their subject
    /// cards — and a hardcoded name is how the copy came to point at one that does not exist.
    /// </summary>
    internal static string RelayCredentialGroup =>
        SecretCatalogue.First(item => item.Key == SecretSettingKeys.EmailUsername).Group;

    /// <summary>The three rows whose change takes the stored relay credential with it.</summary>
    private static readonly string[] ClearingRowKeys = [SmtpHostKey, SmtpPortKey, StartTlsKey];

    private RenderFragment? AdvisoryFor(SettingItem item) =>
        AdvisoryText(item) is { } message ? builder => builder.AddContent(0, message) : null;

    /// <summary>
    /// The control's <c>aria-describedby</c>: the field's helper line, its error message when one is
    /// showing, and its advisory element when one is present — so a screen-reader user reaching the
    /// control hears all three as part of the field rather than only on navigating past them.
    ///
    /// <para>
    /// The error id is load-bearing here in a way it was not on the old row layout: inside an
    /// <c>OdsSettingField</c> frame the control's own error node is hidden by the sheet, so this
    /// reference is the only route from the control to the message.
    /// </para>
    /// </summary>
    private string DescribedBy(SettingItem item)
    {
        var parts = new List<string>(3) { DescId(item.Key) };

        if (ErrorFor(item) is not null)
        {
            parts.Add(ErrorId(item.Key));
        }

        if (AdvisoryText(item) is not null)
        {
            parts.Add(AdvisoryId(item.Key));
        }

        return string.Join(' ', parts);
    }
}
