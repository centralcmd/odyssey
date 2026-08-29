using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for the client half of issue #439 — the Analyze affordance's dependence on the live
/// switch, and the re-prompt after a <c>409 disclosure_changed</c>.
///
/// <para>
/// Text checks rather than rendered assertions, in the style of
/// <see cref="FileAnalysisDisclosureSourceTests"/>: the client test project has no bUnit or render
/// harness, so the source-shaped acceptance criteria are pinned the way every other client AC in this
/// family is.
/// </para>
///
/// <para>
/// What these guard is a <em>consent</em> interaction, which is why they are worth having as lints at
/// all. With analysis switched off the old sequence let a user pick a document, read the disclosure,
/// affirm it, and only then receive a <c>503</c> — collecting a consent for a transfer that could
/// never happen. And a stale disclosure meant a user could affirm "…sending the complete file to
/// Anthropic…" moments before the server transferred to a gateway. Both are silent failures: nothing
/// throws, nothing logs, and the UI looks fine.
/// </para>
/// </summary>
public class FileAnalysisRuntimeSwitchSourceTests
{
    private static string AccountFilesSection =>
        File.ReadAllText(Path.Combine(
            ClientSource.Root, "Pages", "Finance", "AccountFilesSection.razor.cs"));

    private static string DialogCodeBehind =>
        File.ReadAllText(Path.Combine(
            ClientSource.Root, "Pages", "Finance", "FileAnalysisDialog.razor.cs"));

    private static string DialogMarkup =>
        File.ReadAllText(Path.Combine(
            ClientSource.Root, "Pages", "Finance", "FileAnalysisDialog.razor"));

    private static string DisclosureCache =>
        File.ReadAllText(Path.Combine(
            ClientSource.Root, "Services", "FileAnalysisDisclosureCache.cs"));

    // ── AC 53 — the affordance is disabled, with the reason in TEXT ──────────────────────────────

    /// <summary>
    /// The Analyze menu item is bound to the fetched <c>enabled</c> flag and carries a text
    /// explanation. The explanation is the load-bearing half: a greyed-out item conveys its meaning by
    /// colour alone, which is exactly what WCAG 1.4.1 forbids and what a keyboard or screen-reader user
    /// gets nothing from.
    /// </summary>
    [Fact]
    public void TheAnalyzeMenuItem_IsDisabledFromTheLiveSwitch_WithAVisibleTextExplanation()
    {
        var source = AccountFilesSection;

        Assert.Contains("Disabled = !_analysisEnabled", source, StringComparison.Ordinal);
        Assert.Contains("Description = _analysisEnabled ? null :", source, StringComparison.Ordinal);
        Assert.Contains("AI document analysis is turned off for this instance.", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The flag comes from the disclosure cache, not from a local default. <c>IsResolved</c> is part of
    /// the condition, so an unresolved fetch leaves the affordance disabled rather than optimistically
    /// enabled.
    /// </summary>
    [Fact]
    public void TheAvailabilityFlag_IsFetched_AndRequiresAResolvedDisclosure()
    {
        var source = AccountFilesSection;

        Assert.Contains("Disclosures.GetAsync()", source, StringComparison.Ordinal);
        Assert.Contains("Disclosures.IsResolved && disclosure.Enabled", source, StringComparison.Ordinal);
    }

    // ── AC 54 — an unresolved disclosure is never treated as "on" ────────────────────────────────

    /// <summary>
    /// <c>Fallback</c> is the last resort for a failed fetch and supplies display text only. Extending
    /// it to carry <c>Enabled = true</c> would turn a failed fetch into a green light — the one change
    /// that must not be made here, and one that would look like a harmless completeness fix.
    /// </summary>
    [Fact]
    public void TheDisclosureFallback_NeverSuppliesEnabledTrue()
    {
        var source = DisclosureCache;
        var fallback = source[source.IndexOf("Fallback = new()", StringComparison.Ordinal)..];
        var initialiser = fallback[..fallback.IndexOf("};", StringComparison.Ordinal)];

        Assert.DoesNotContain("Enabled = true", initialiser, StringComparison.Ordinal);
        Assert.DoesNotContain("Enabled", initialiser, StringComparison.Ordinal);
    }

    // ── AC 55 — the re-prompt ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>409</c> re-prompts rather than failing. Every clause here is a separate requirement, and
    /// the checkbox reset is the one that carries the legal weight: the previous affirmation was given
    /// for different facts and must not carry over into a transfer the user was not told about.
    /// </summary>
    [Fact]
    public void AConflictResponse_ReprompsTheGate_ResettingTheAffirmation()
    {
        var source = DialogCodeBehind;

        Assert.Contains("HttpStatusCode.Conflict", source, StringComparison.Ordinal);
        Assert.Contains("RepromptForChangedDisclosureAsync", source, StringComparison.Ordinal);

        var reprompt = source[source.IndexOf(
            "private async Task RepromptForChangedDisclosureAsync", StringComparison.Ordinal)..];

        Assert.Contains("Disclosures.Invalidate()", reprompt, StringComparison.Ordinal);
        Assert.Contains("Disclosures.GetAsync()", reprompt, StringComparison.Ordinal);
        Assert.Contains("_consentChecked = false", reprompt, StringComparison.Ordinal);
        Assert.Contains("FileAnalysisPhase.Consent", reprompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The explanation announces itself and receives focus, so it is not conveyed by visual position
    /// alone and a keyboard or screen-reader user lands on the reason rather than on a checkbox that
    /// silently reset beneath them.
    ///
    /// <para>
    /// Announcement comes from <c>OdsAlert Severity="Warning"</c>, which carries <c>role="alert"</c>.
    /// It must <strong>not</strong> also go through the dialog's polite region: the notice field is
    /// never cleared for the life of the dialog, so putting it at the front of <c>DesiredLiveMsg</c>'s
    /// null-coalescing chain masked every later announcement — grid actions and phase transitions —
    /// for the rest of the session, and spoke the notice itself two or three times over on the way in.
    /// The first version of this lint pinned exactly that pattern, which is why the negative assertion
    /// below is the load-bearing half.
    /// </para>
    /// </summary>
    [Fact]
    public void TheRepromptExplanation_AnnouncesItselfAndIsFocused_WithoutHijackingTheLiveRegion()
    {
        var code = DialogCodeBehind;

        // The notice must NOT sit in the live region's desired text at all.
        Assert.DoesNotMatch(
            new Regex(@"DesiredLiveMsg\s*=>[^;]*_disclosureChangedNotice", RegexOptions.Singleline),
            code);

        // Focused, once per re-prompt.
        Assert.Contains("_disclosureChangedNeedsFocus", code, StringComparison.Ordinal);
        Assert.Contains("_disclosureChangedRef.FocusAsync()", code, StringComparison.Ordinal);

        // Announced by the alert's own role, so the severity that yields role="alert" is the contract.
        var markup = DialogMarkup;
        Assert.Contains("OdsAlert Severity=\"Severity.Warning\"", markup, StringComparison.Ordinal);

        // And the element it focuses is programmatically focusable and carries the text.
        Assert.Contains("tabindex=\"-1\" @ref=\"_disclosureChangedRef\"", markup, StringComparison.Ordinal);
        Assert.Contains("@notice", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate echoes the version it rendered. Without this the server's check is opt-out by
    /// omission — a missing echo is treated as a mismatch, so a client that forgot it would simply stop
    /// working, but silently sending nothing would be the worse failure to reintroduce.
    /// </summary>
    [Fact]
    public void TheAnalyzeRequest_EchoesTheRenderedDisclosureVersion()
    {
        Assert.Contains(
            "DisclosureVersion = _disclosure.DisclosureVersion", DialogCodeBehind, StringComparison.Ordinal);
    }

    // ── AC 60 — no client-side copy of a server value ────────────────────────────────────────────

    /// <summary>
    /// None of the three new settings may be held as a client constant. A local copy of an
    /// admin-editable value goes stale the moment an administrator changes it, and for the base URL it
    /// would additionally be infrastructure detail the claim-free surfaces deliberately do not carry.
    /// </summary>
    [Fact]
    public void NoPageHoldsACopyOfTheBaseUrlOrTheSwitch()
    {
        var violations = new List<string>();

        foreach (var file in ClientSource.RazorFilesIn("Pages"))
        {
            var text = File.ReadAllText(file);
            foreach (var literal in new[] { "api.anthropic.com", "https://api." })
            {
                var index = text.IndexOf(literal, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    violations.Add($"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, index)} — {literal}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "The provider base URL is admin-editable and is served only on the admin-gated settings DTO; "
            + "a page holding a copy would be both stale and a disclosure: " + string.Join(", ", violations));
    }
}
