using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for the consent-gate half of issue #421 Wave 1 (AC 20), in the style of
/// <see cref="ImportExportSettingsSourceTests"/> — the client test project has no bUnit or render
/// harness, so the source-shaped acceptance criteria are pinned as text checks.
///
/// <para>
/// These exist because the defect they guard against was not a bug in logic, it was a <em>duplicate</em>:
/// the four processor facts lived on the server AND as compile-time constants in the client, and had
/// silently drifted until the consent gate named a model the server did not use. A lint is the only
/// thing that stops that reappearing, since both copies compile fine.
/// </para>
///
/// <para>
/// Scoped to <c>.razor</c> files under <c>Pages/</c> on purpose. The last-resort copy legitimately
/// still exists in <c>Services/FileAnalysisDisclosureCache.Fallback</c> — a failed fetch must render
/// something complete rather than a blank disclosure — and that is exactly where it belongs.
/// </para>
/// </summary>
public class FileAnalysisDisclosureSourceTests
{
    /// <summary>The four values that must never be re-hardcoded in a page.</summary>
    private static readonly string[] DisclosureLiterals =
    [
        "Anthropic",
        "United States",
        "Consent · GDPR Art. 6(1)(a)",
        "anthropic.com/legal/privacy",
    ];

    /// <summary>
    /// The specific defect this wave fixes: the panel hardcoded a model name in prose while
    /// <c>appsettings.json</c> configured a different one. Any <c>claude-*</c> literal in a page is that
    /// bug returning.
    /// </summary>
    [Fact]
    public void No_page_hardcodes_a_model_name()
    {
        var violations = new List<string>();

        foreach (var file in ClientSource.RazorFilesIn("Pages"))
        {
            var text = File.ReadAllText(file);
            var index = text.IndexOf("claude-", StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                // A mention inside a comment documents the fix; only rendered markup is the defect.
                // Razor block comments span lines, so this cannot be decided line-locally.
                if (!IsInComment(text, index))
                {
                    var line = LineContaining(text, index);
                    violations.Add($"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, index)} — {line.Trim()}");
                }

                index = text.IndexOf("claude-", index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.True(violations.Count == 0,
            "A model name must come from GET /api/file-analysis/disclosure, never a literal — that is "
            + "the drift issue #421 Wave 1 fixes: " + string.Join("; ", violations));
    }

    /// <summary>
    /// Neither in an interpolation nor <strong>in prose</strong>. The prose case is the one the first
    /// draft of this lint missed: the panel read "Anthropic retains the data…" as a literal sentence, so
    /// a changed processor would have produced a gate naming two different processors.
    /// </summary>
    [Fact]
    public void No_page_hardcodes_a_disclosure_value()
    {
        var violations = new List<string>();

        foreach (var file in ClientSource.RazorFilesIn("Pages"))
        {
            var text = File.ReadAllText(file);
            foreach (var literal in DisclosureLiterals)
            {
                var index = text.IndexOf(literal, StringComparison.Ordinal);
                while (index >= 0)
                {
                    if (!IsInComment(text, index))
                    {
                        violations.Add($"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, index)} — '{literal}'");
                    }

                    index = text.IndexOf(literal, index + 1, StringComparison.Ordinal);
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Disclosure values must come from the server, in prose as well as in interpolations: "
            + string.Join("; ", violations));
    }

    /// <summary>
    /// The four client constants are gone from the model. They moved to the cache's <c>Fallback</c> —
    /// §3/§11 require a complete compiled disclosure while the fetch is in flight or has failed, so
    /// "the constants are deleted" and "a fallback exists" would otherwise contradict each other.
    /// </summary>
    [Fact]
    public void The_consent_model_no_longer_declares_the_processor_facts()
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Models", "FileAnalysisConsent.cs"));

        foreach (var member in new[] { "const string Processor", "const string ProcessorRegion",
                                       "const string LawfulBasis", "const string PrivacyNoticeUrl" })
        {
            Assert.DoesNotContain(member, text, StringComparison.Ordinal);
        }

        // And the frozen sentence became a composer, so the affirmed text cannot disagree with the
        // panel it is rendered beside.
        Assert.DoesNotContain("const string Text", text, StringComparison.Ordinal);
        Assert.Contains("Compose(string processor)", text, StringComparison.Ordinal);
    }

    /// <summary>The fallback must still be complete — a blank disclosure is worse than a stale one.</summary>
    [Fact]
    public void The_cache_fallback_carries_every_disclosure_value()
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Services", "FileAnalysisDisclosureCache.cs"));

        Assert.Contains("Processor =", text, StringComparison.Ordinal);
        Assert.Contains("ProcessorRegion =", text, StringComparison.Ordinal);
        Assert.Contains("LawfulBasis =", text, StringComparison.Ordinal);
        Assert.Contains("PrivacyNoticeUrl =", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The panel stays presentational — parameters only, no injection. It was split out of the dialog
    /// in #373 for that reason, and "which component fetches" was left unspecified by the spec's first
    /// draft, which is how an implementer ends up injecting an API client into a display component.
    /// </summary>
    [Fact]
    public void The_consent_panel_does_not_inject_anything()
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "FileAnalysisConsentPanel.razor"));

        Assert.DoesNotContain("@inject", text, StringComparison.Ordinal);
        Assert.Contains("[Parameter, EditorRequired] public FileAnalysisDisclosureDto Disclosure",
            text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="index"/> sits inside a Razor block comment or a C# line comment.
    /// Block-aware on purpose: a multi-line <c>@* … *@</c> explaining this very fix would otherwise
    /// trip the lint on its own continuation lines, which is how a lint gets weakened to shut it up.
    /// </summary>
    private static bool IsInComment(string text, int index)
    {
        var open = text.LastIndexOf("@*", index, StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = text.IndexOf("*@", open, StringComparison.Ordinal);
            if (close < 0 || close > index)
            {
                return true;
            }
        }

        var line = LineContaining(text, index);
        return line.TrimStart().StartsWith("//", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("///", StringComparison.Ordinal);
    }

    private static string LineContaining(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Min(index, text.Length - 1)) + 1;
        var end = text.IndexOf('\n', index);
        return end < 0 ? text[start..] : text[start..end];
    }
}
