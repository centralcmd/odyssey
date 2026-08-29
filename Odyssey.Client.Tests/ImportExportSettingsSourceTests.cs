using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for the frontend half of issue #343 (admin-configurable import/export limits), in
/// the style of <see cref="SourceConventionTests"/>/<see cref="ListPageContractTests"/>: pure
/// text checks over the checked-in <c>.razor</c>/<c>.razor.cs</c> sources — the client test project
/// has no bUnit or render harness, so these pin the acceptance criteria that are genuinely
/// source-shaped (§16 AC 31-35g) rather than needing a live DOM.
/// </summary>
public class ImportExportSettingsSourceTests
{
    private static readonly string[] ImportDialogFiles =
    [
        "Pages/Finance/ContactImportDialog.razor",
        "Pages/Calendar/ImportCalendarDialog.razor",
        "Pages/Calendar/ImportCalendarDialog.razor.cs",
        "Pages/Journal/ImportTasksDialog.razor",
        "Pages/Journal/ImportTasksDialog.razor.cs",
        "Pages/Journal/ImportJournalEntriesDialog.razor",
        "Pages/Journal/ImportJournalEntriesDialog.razor.cs",
    ];

    // ─────────────────────────────────────────────────────────────────────────
    //  AC 31 — no Pages/ file references a MaxImportBytes constant
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The four import dialogs must read the effective limit from <c>IImportLimitsCache</c>, never a
    /// compile-time <c>XxxApiClient.MaxImportBytes</c> constant directly — that fallback now lives in
    /// exactly one place, <c>ImportLimitsCache.Fallback</c> (issue #343 fe C3/C4).
    /// </summary>
    [Fact]
    public void No_page_under_Pages_references_a_MaxImportBytes_constant()
    {
        var violations = new List<string>();

        foreach (var file in ClientSource.RazorFilesIn("Pages"))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("MaxImportBytes", StringComparison.Ordinal))
            {
                violations.Add(ClientSource.Relative(file));
            }
        }

        Assert.True(violations.Count == 0,
            "These Pages/ files still reference a MaxImportBytes constant directly instead of " +
            "reading IImportLimitsCache (issue #343 fe C3):\n" + string.Join('\n', violations));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  AC 32 — no hard-coded megabyte literal in an import dialog's Hint/error text
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly Regex HardCodedMegabyteHint =
        new(@"(Hint\s*=\s*""[^""]*\b\d+\s*MB|larger than the \d+\s*MB limit)", RegexOptions.Compiled);

    [Fact]
    public void No_import_dialog_hardcodes_a_megabyte_literal_in_hint_or_error_text()
    {
        var violations = new List<string>();

        foreach (var relative in ImportDialogFiles)
        {
            var file = Path.Combine(ClientSource.Root, relative);
            if (!File.Exists(file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in HardCodedMegabyteHint.Matches(text))
            {
                violations.Add($"{relative}:{ClientSource.LineAt(text, match.Index)} — {match.Value}");
            }
        }

        Assert.True(violations.Count == 0,
            "An import dialog still hard-codes a megabyte number in its hint or error text instead of " +
            "the effective limit (issue #343 fe C3):\n" + string.Join('\n', violations));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  AC 33/35e — every OdsCapacityField call site is fully labelled
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly Regex CapacityFieldTag =
        new(@"<OdsCapacityField\b[^/]*?/>", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void Every_OdsCapacityField_call_site_passes_Label_AriaLabelledBy_AriaDescribedBy_and_Class()
    {
        var violations = new List<string>();

        foreach (var file in ClientSource.RazorFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match tag in CapacityFieldTag.Matches(text))
            {
                var line = ClientSource.LineAt(text, tag.Index);
                foreach (var required in new[] { "Label=", "AriaLabelledBy=", "AriaDescribedBy=" })
                {
                    if (!tag.Value.Contains(required, StringComparison.Ordinal))
                    {
                        violations.Add($"{ClientSource.Relative(file)}:{line} — missing {required.TrimEnd('=')}");
                    }
                }

                // No call site should pin an inline width on the number column — the 120px sizing is
                // the DS atom's own .odc-capacity-num rule (issue #343 fe R1).
                if (tag.Value.Contains("width:", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{ClientSource.Relative(file)}:{line} — sets an inline width; the atom already sizes itself");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Every OdsCapacityField call site must pass Label, AriaLabelledBy and AriaDescribedBy, and " +
            "never an inline width (issue #343 AC 33/35e):\n" + string.Join('\n', violations));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  AC 33 — every setting field on the page identifies its label and its helper line
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The settings catalogue moved from one <c>OdsSettingRow</c> card per setting to a grid of
    /// <c>OdsSettingField</c> blocks, so the ids this checks are now <c>LabelId</c> and <c>HelpId</c>.
    /// The requirement is unchanged and load-bearing in exactly the same way: a control with no visible
    /// label of its own (an <c>OdsCapacityField</c>, an <c>OdsSwitch</c>) names itself with
    /// <c>aria-labelledby</c> pointing at the label, and describes itself with <c>aria-describedby</c>
    /// pointing at the helper line — and both are also the jump targets the problems rollup and the
    /// fault summary focus.
    /// </summary>
    [Fact]
    public void Settings_page_OdsSettingField_call_sites_identify_their_label_and_help()
    {
        var file = Path.Combine(ClientSource.Root, "Pages/Settings.razor");
        var text = File.ReadAllText(file);

        var tags = Regex.Matches(text, @"<OdsSettingField\b.*?>", RegexOptions.Singleline);
        Assert.True(tags.Count > 0, "Expected at least one OdsSettingField call site in Settings.razor.");

        var violations = new List<string>();
        foreach (Match tag in tags)
        {
            var line = ClientSource.LineAt(text, tag.Index);
            if (!tag.Value.Contains("LabelId=", StringComparison.Ordinal))
                violations.Add($"Settings.razor:{line} — missing LabelId");
            if (!tag.Value.Contains("HelpId=", StringComparison.Ordinal))
                violations.Add($"Settings.razor:{line} — missing HelpId");
        }

        Assert.True(violations.Count == 0,
            "Every setting field on the settings page must supply both LabelId and HelpId " +
            "(issue #343 AC 33):\n" + string.Join('\n', violations));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  AC 34/35 — OdsFieldShell's two-node Help/Error rendering; OdsSettingRow.DescId /
    //  OdsNumberField.AriaDescribedBy exist as parameters
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OdsFieldShell_renders_Help_and_Error_as_two_separately_identified_nodes()
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Components/OdsFieldShell.razor"));

        // Two independent conditional blocks — Help/HelpContent no longer suppressed by Error.
        Assert.Contains("if (HelpContent is not null)", text, StringComparison.Ordinal);
        Assert.Contains("if (!string.IsNullOrEmpty(Help))", text, StringComparison.Ordinal);
        Assert.Contains("if (!string.IsNullOrEmpty(Error))", text, StringComparison.Ordinal);
        Assert.Contains("id=\"@HelpId\"", text, StringComparison.Ordinal);
        Assert.Contains("id=\"@ErrorId\"", text, StringComparison.Ordinal);

        // The single-node backward-compatibility guarantee for the ~25 existing consumers: when no
        // help renders, the error reuses HelpId itself rather than a second, orphaned id.
        Assert.Contains("HasHelp ?", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OdsSettingRow_exposes_DescId_and_OdsNumberField_exposes_AriaDescribedBy()
    {
        var settingRow = File.ReadAllText(Path.Combine(ClientSource.Root, "Components/OdsSettingRow.razor"));
        Assert.Contains("public string? DescId", settingRow, StringComparison.Ordinal);

        var numberField = File.ReadAllText(Path.Combine(ClientSource.Root, "Components/OdsNumberField.razor"));
        Assert.Contains("public string? AriaDescribedBy", numberField, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  AC 35c — OdsCapacityField renders literal "No limit" text, not an icon/colour/empty box
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OdsCapacityField_renders_the_literal_text_No_limit_when_unlimited()
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Components/OdsCapacityField.razor"));
        Assert.Contains(">No limit<", text, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  AC 35d — the group-level round-trip alert is a focusable role="alert" region
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_alert_carries_role_alert_and_a_focusable_tabindex()
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages/Settings.razor"));
        // The alert is now the error variant of the section card's own full-width band (the group
        // header/band/grid layout replaced the free-standing .ss-rtalert block), but it is the same
        // element doing the same job: group-level, because the offending export row may itself be
        // disabled and so unfocusable.
        var match = Regex.Match(text, @"<div class=""ss-sect-band error""[^>]*>", RegexOptions.Singleline);

        Assert.True(match.Success, "Expected an .ss-sect-band.error element in Settings.razor.");
        Assert.Contains("role=\"alert\"", match.Value, StringComparison.Ordinal);
        Assert.Contains("tabindex=\"-1\"", match.Value, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Follow-up (post-#343): every "number of records" capacity field on the page supports "No
    //  limit" — including calendarIcsMaxExportEvents (an earlier fix incorrectly special-cased it
    //  off, coupling it to a hard ceiling on a DIFFERENT, unrelated export path; the real fix was
    //  removing that coupling server-side, not carving out a UI exception) and taskIcsMaxExportTasks
    //  (a new field — Tasks previously had no export cap at all). OdsCapacityField itself must never
    //  regain a way to hide the switch.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OdsCapacityField_always_renders_the_No_limit_switch()
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Components/OdsCapacityField.razor"));

        Assert.DoesNotContain("AllowUnlimited", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("calendarIcsMaxExportEvents")]
    [InlineData("taskIcsMaxExportTasks")]
    public void Every_export_count_SettingItem_omits_a_max_below_the_shared_capacity_ceiling(string key)
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages/Settings.razor.cs"));
        var match = Regex.Match(text, $@"new\(""{key}"".*?\);", RegexOptions.Singleline);

        Assert.True(match.Success, $"Expected a {key} SettingItem in Settings.razor.cs.");
        Assert.DoesNotContain("AllowUnlimited", match.Value, StringComparison.Ordinal);
        Assert.Contains("Max: 1_000_000", match.Value, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Follow-up (post-#343): a "maximum export file size" exists for all four surfaces, and Tasks
    //  gained an export row cap — pin that the catalog carries all five new keys.
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("contactVCardMaxExportMegabytes")]
    [InlineData("calendarIcsMaxExportMegabytes")]
    [InlineData("taskIcsMaxExportTasks")]
    [InlineData("taskIcsMaxExportMegabytes")]
    [InlineData("journalIcsMaxExportMegabytes")]
    public void Settings_catalog_contains_the_new_export_limit_key(string key)
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages/Settings.razor.cs"));
        Assert.Contains($"\"{key}\"", text, StringComparison.Ordinal);
    }
}
