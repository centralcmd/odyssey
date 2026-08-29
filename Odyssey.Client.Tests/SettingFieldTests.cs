using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The contracts of the two atoms the system-settings grid is built from — <c>OdsSettingField</c> (the
/// notched-outline setting block) and <c>OdsErrorSummary</c> (the "n problems · Review" control beside a
/// disabled Save) — plus the page-level rules that only hold if the two are wired together correctly.
///
/// <para>
/// Source lints rather than render tests, matching the rest of this project's design-system guards: each
/// one is a claim the design system makes in prose that would otherwise be re-decided by whoever next
/// edits the markup.
/// </para>
/// </summary>
public class SettingFieldTests
{
    private static string Component(string name) =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Components", name));

    /// <summary>
    /// The rendered markup only, with the <c>@code</c> block and Razor comments stripped. The parameter
    /// documentation legitimately DISCUSSES <c>aria-invalid</c> and <c>role="alert"</c> — a test a doc
    /// comment can fail is a test that gets its docs deleted.
    /// </summary>
    private static string RenderedMarkup(string name)
    {
        var markup = Component(name);
        var code = markup.IndexOf("@code {", StringComparison.Ordinal);
        var body = code < 0 ? markup : markup[..code];
        return Regex.Replace(body, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);
    }

    /// <summary>Each rendered <c>.odc-sfield-advisory</c> element, opening tag through closing div.</summary>
    private static List<string> AdvisoryBlocks() =>
        Regex.Matches(
                RenderedMarkup("OdsSettingField.razor"),
                "<div class=\"odc-sfield-advisory\".*?</div>\\s*</div>",
                RegexOptions.Singleline)
            .Select(match => match.Value)
            .ToList();

    private static string Page() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor"));

    private static string PageCodeBehind() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));

    // ── OdsSettingField ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The outline is a real <c>fieldset</c>/<c>legend</c>, not a floating span over a border.
    ///
    /// <para>
    /// This is the whole reason the atom exists in this shape: the BROWSER cuts the notch, so the gap
    /// tracks the label's own text metrics at any font size or zoom, and nothing has to be painted to
    /// match the card behind it. A span-over-border reimplementation looks identical at the default
    /// size and breaks at 200% zoom or with a user font stack — the two cases nobody screenshots.
    /// </para>
    /// </summary>
    [Fact]
    public void The_outline_is_a_real_fieldset_and_legend()
    {
        var markup = RenderedMarkup("OdsSettingField.razor");

        Assert.Contains("<fieldset class=\"@FrameClass\">", markup, StringComparison.Ordinal);
        Assert.Contains("<legend class=\"odc-sfield-legend\">", markup, StringComparison.Ordinal);

        // The label inside the legend is a real <label for> whenever the control has a focusable id.
        Assert.Contains("for=\"@HtmlFor\"", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The error renders ABOVE the helper line and does not displace it: the reader keeps the definition
    /// of the setting while fixing its value. Both nodes are independent conditionals, so neither can
    /// suppress the other.
    /// </summary>
    [Fact]
    public void The_error_does_not_displace_the_description()
    {
        var markup = RenderedMarkup("OdsSettingField.razor");

        // The frame branch only — the tile branch has no error slot, so searching the whole file would
        // measure the tile's helper line against the frame's error.
        var frame = markup[markup.IndexOf("<fieldset", StringComparison.Ordinal)..];
        var errorAt = frame.IndexOf("odc-sfield-err", StringComparison.Ordinal);
        var helpAt = frame.IndexOf("odc-sfield-help", StringComparison.Ordinal);

        Assert.True(errorAt >= 0 && helpAt > errorAt,
            "The error block must render before (above) the helper line, not instead of it.");

        // Independent conditions — the helper line's gate must not mention Error.
        Assert.Contains("private bool HasHelpLine =>", Component("OdsSettingField.razor"), StringComparison.Ordinal);
        var gate = Component("OdsSettingField.razor");
        var gateStart = gate.IndexOf("private bool HasHelpLine =>", StringComparison.Ordinal);
        var gateLine = gate[gateStart..gate.IndexOf(';', gateStart)];
        Assert.DoesNotContain("Error", gateLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// An advisory is not a validation error and not a live region of its own. It must never set
    /// <c>aria-invalid</c>, and <c>role="alert"</c> stays reserved for the field-error path because an
    /// advisory must not interrupt — a live region inserted into the DOM at the same time as its content
    /// is frequently not announced at all, so announcement is routed through the page's existing
    /// <c>OdsLiveAnnouncer</c> instead.
    /// </summary>
    [Fact]
    public void The_advisory_is_neither_invalid_nor_a_live_region()
    {
        // Both branches render one — the tile and the frame — so both are checked. Slicing from the
        // first occurrence to end of file would sweep in the frame's error node, which legitimately
        // carries role="alert".
        var blocks = AdvisoryBlocks();
        Assert.Equal(2, blocks.Count);

        foreach (var block in blocks)
        {
            Assert.DoesNotContain("aria-invalid", block, StringComparison.Ordinal);
            Assert.DoesNotContain("role=\"status\"", block, StringComparison.Ordinal);
            Assert.DoesNotContain("role=\"alert\"", block, StringComparison.Ordinal);
            Assert.DoesNotContain("aria-live", block, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Meaning as TEXT, never colour or icon alone (WCAG 1.4.1): the literal word "Advisory" precedes the
    /// message and the glyph is <c>aria-hidden</c>. This is also what makes an advisory distinguishable
    /// from a field error by more than hue.
    /// </summary>
    [Fact]
    public void The_advisory_states_its_meaning_in_text_with_a_hidden_icon()
    {
        foreach (var block in AdvisoryBlocks())
        {
            Assert.Contains("odc-sfield-advisory-t\">Advisory</b>", block, StringComparison.Ordinal);
            Assert.Contains("aria-hidden=\"true\"", block, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The amber outline is suppressed while an error is showing. Coral outranks amber and two border
    /// colours cannot both win, so a row that is both advised and invalid must read as invalid.
    /// </summary>
    [Fact]
    public void An_error_outranks_an_advisory_on_the_outline()
    {
        var component = Component("OdsSettingField.razor");
        var frameClass = component[component.IndexOf("private string FrameClass =>", StringComparison.Ordinal)..];
        var body = frameClass[..frameClass.IndexOf("private string? BoundMarker", StringComparison.Ordinal)];

        Assert.Contains("\"advised\"", body, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrEmpty(Error)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tile always spans the grid. A switch or an action has no text value to notch a label into, so it
    /// renders as one full-width tile — a half-width tile would leave the control floating beside a gap.
    /// </summary>
    [Fact]
    public void A_tile_always_spans_the_grid()
    {
        var component = Component("OdsSettingField.razor");
        Assert.Contains("(Wide || Tile) ? \"wide\" : null", component, StringComparison.Ordinal);
    }

    // ── OdsErrorSummary ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// It is a BUTTON, not a banner. Pressing it moves focus to the offending field, and that is what
    /// makes a disabled primary action recoverable by keyboard — a banner saying the same words is not
    /// operable and leaves the user hunting.
    /// </summary>
    [Fact]
    public void The_error_summary_is_a_button()
    {
        var markup = RenderedMarkup("OdsErrorSummary.razor");

        Assert.Contains("<button type=\"button\" class=\"odc-errsum\"", markup, StringComparison.Ordinal);
        Assert.Contains("class=\"odc-errsum-item\"", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count is folded into the accessible NAME, so it reads "2 problems, review" rather than
    /// "2 · Review" — the visible count, the separator and the action word are all <c>aria-hidden</c>,
    /// which would otherwise announce as three disconnected fragments.
    /// </summary>
    [Fact]
    public void The_count_is_folded_into_the_accessible_name()
    {
        var markup = RenderedMarkup("OdsErrorSummary.razor");

        Assert.Contains("aria-label=\"@($\"{CountLabel}, {Action.ToLowerInvariant()}\")\"", markup, StringComparison.Ordinal);

        var visible = markup[markup.IndexOf("odc-errsum-count", StringComparison.Ordinal)..];
        var head = visible[..visible.IndexOf("odc-errsum-chev", StringComparison.Ordinal)];
        Assert.Equal(3, Regex.Matches(head, "aria-hidden=\"true\"").Count);
    }

    /// <summary>
    /// It announces NOTHING itself. The validation behind it recomputes on every keystroke, so a live
    /// region here would interrupt a screen-reader user once per character of "1000"; the page announces
    /// politely on a save ATTEMPT instead.
    /// </summary>
    [Fact]
    public void The_error_summary_is_not_a_live_region()
    {
        var markup = RenderedMarkup("OdsErrorSummary.razor");

        Assert.DoesNotContain("aria-live", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"status\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"alert\"", markup, StringComparison.Ordinal);

        // …and the page does announce on a save attempt.
        Assert.Contains("<OdsLiveAnnouncer", Page(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>aria-expanded</c> is present only when there is something to expand. Announcing "collapsed" on
    /// a control that just moves focus is a promise of a disclosure that never opens.
    /// </summary>
    [Fact]
    public void Aria_expanded_is_only_present_when_the_control_expands()
    {
        var markup = RenderedMarkup("OdsErrorSummary.razor");
        Assert.Contains("aria-expanded=\"@(Expandable ? (_open ? \"true\" : \"false\") : null)\"", markup, StringComparison.Ordinal);
    }

    // ── The page-level wiring ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every listed problem points at a RENDERED row. A search-filtered row is removed from the DOM
    /// entirely and <c>window.odsFocusById</c> is a bare <c>.focus()</c> that no-ops silently on a
    /// missing id, so an entry built from the whole catalogue would be a dead end presented as a fix.
    /// </summary>
    [Fact]
    public void The_problem_list_is_built_from_rendered_sections_only()
    {
        var code = PageCodeBehind();
        var start = code.IndexOf("private IReadOnlyList<OdsErrorSummaryProblem> BlockingProblems =>", StringComparison.Ordinal);
        Assert.True(start >= 0, "No BlockingProblems rollup found in Settings.razor.cs.");

        var body = code[start..code.IndexOf("];", start, StringComparison.Ordinal)];

        Assert.Contains("VisibleSections", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AllItems", body, StringComparison.Ordinal);

        // Both halves of "blocking": the per-row errors AND the group-level round-trip conflicts, whose
        // offending row may itself be disabled and therefore unfocusable.
        Assert.Contains("ErrorFor(item)", body, StringComparison.Ordinal);
        Assert.Contains("RoundTripAlertId(section.Group)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each <c>RenderFragment</c> in the page's <c>@code</c> block, keyed by method name. The fragments
    /// are what actually place a control in the DOM, so enumerating them is the only way to make a claim
    /// about "every call site" that stays true when one is added.
    /// </summary>
    private static Dictionary<string, string> RenderFragments()
    {
        var page = Page();
        var code = page.IndexOf("@code {", StringComparison.Ordinal);
        Assert.True(code >= 0, "Settings.razor has no @code block.");

        var fragments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match declaration in Regex.Matches(
                     page[code..], @"private RenderFragment (?<name>\w+)\(", RegexOptions.None))
        {
            // The body runs to the next member declaration, or the end of the block.
            var from = declaration.Index;
            var next = Regex.Match(page[code..][(from + declaration.Length)..], @"\n    private \w");
            var length = next.Success ? declaration.Length + next.Index : page[code..].Length - from;
            fragments[declaration.Groups["name"].Value] = page[code..].Substring(from, length);
        }

        return fragments;
    }

    /// <summary>
    /// Inside the notched frame the sheet hides the control's own help/error node, so the field owns the
    /// message and the control is handed only a state flag. Passing the real text to both would render
    /// it twice on a tile and not at all inside a frame.
    ///
    /// <para>
    /// Enumerated rather than spot-checked (PR #449, test review). The previous form asserted the
    /// absence of one hand-written string that embedded a newline and eight spaces of indentation — so
    /// reformatting the file, or adding a control that got it wrong in a different shape, would have
    /// gone green while claiming to have checked "every control call site".
    /// </para>
    /// </summary>
    [Fact]
    public void The_field_owns_the_message_and_the_control_owns_only_the_state()
    {
        var fragments = RenderFragments();
        var controls = fragments.Where(f => f.Key.EndsWith("Control", StringComparison.Ordinal)).ToList();

        Assert.True(controls.Count >= 5,
            $"Only {controls.Count} control fragments found — the enumeration is not seeing the @code block.");

        foreach (var (name, body) in controls)
        {
            if (!body.Contains("Error=", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(body.Contains("Error=\"@FieldStateFor(item)\"", StringComparison.Ordinal),
                $"{name} must hand the control the state placeholder, not the message.");
            Assert.False(body.Contains("Error=\"@ErrorFor(", StringComparison.Ordinal),
                $"{name} passes the message text to the control; the field renders it, so it would double up.");
        }

        // …and the field wrapper itself carries the text.
        Assert.True(fragments.TryGetValue("FrameField", out var frame), "No FrameField fragment found.");
        Assert.Contains("Error=\"@ErrorFor(item)\"", frame, StringComparison.Ordinal);

        Assert.Contains("private string? FieldStateFor(SettingItem item) => ErrorFor(item) is null ? null : \" \";",
            PageCodeBehind(), StringComparison.Ordinal);

        // The suppression this depends on is real.
        var sheet = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));
        Assert.Contains(".odc-sfield-ctrl .odc-field-help { display: none; }", sheet, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stored 0.0–1.0 fraction is entered as a whole percent, scaled at the control boundary — never
    /// exposed raw with a two-decimal stepper, which gives the reader no clue the number is a proportion
    /// and is a fiddly target. The bounds and the error message follow the displayed unit, so the field
    /// can never report a range it would refuse.
    /// </summary>
    [Fact]
    public void A_percent_row_scales_at_the_control_boundary()
    {
        var page = Page();
        var code = PageCodeBehind();

        Assert.Contains("Min=\"@PercentOf(item.DecimalMin)\" Max=\"@PercentOf(item.DecimalMax)\"", page, StringComparison.Ordinal);
        Assert.Contains("Unit=\"%\"", page, StringComparison.Ordinal);
        Assert.Contains("Value=\"@GetPercent(item.Key)\"", page, StringComparison.Ordinal);

        // The stored contract is untouched: the draft store still holds the fraction.
        Assert.Contains("SetDecimal(key, percent is null ? null : percent.Value / 100m)", code, StringComparison.Ordinal);

        // The message is phrased in the unit the field shows.
        Assert.Contains("Must be between {PercentOf(item.DecimalMin)} and {PercentOf(item.DecimalMax)}%",
            code, StringComparison.Ordinal);
    }

    /// <summary>
    /// No page holds a size cap as a literal. The cap is an admin-editable runtime setting, so a typed
    /// number is a claim that goes stale silently — the user-visible half of the same defect as a
    /// hard-coded pre-check. Where the effective cap is not known, the hint omits the size clause rather
    /// than asserting a default that may not be this deployment's.
    /// </summary>
    [Fact]
    public void No_upload_hint_carries_a_hard_coded_size_cap()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ClientSource.Root, "*.razor", SearchOption.AllDirectories))
        {
            foreach (Match hint in Regex.Matches(File.ReadAllText(file), @"Hint=""(?<value>[^""]*)"""))
            {
                if (Regex.IsMatch(hint.Groups["value"].Value, @"\d+(\s| )*MB", RegexOptions.IgnoreCase))
                {
                    offenders.Add($"{ClientSource.Relative(file)} — {hint.Groups["value"].Value}");
                }
            }
        }

        // The component's own fallback is the other half: it must not name a number either.
        var upload = Component("OdsFileUpload.razor");
        var fallback = upload[upload.IndexOf("private string EffectiveHint =>", StringComparison.Ordinal)..];
        Assert.DoesNotContain("25 MB", fallback[..fallback.IndexOf(';')], StringComparison.Ordinal);

        Assert.True(offenders.Count == 0,
            "Upload hints must interpolate the effective cap, never state it as a literal:\n"
            + string.Join('\n', offenders));
    }

    // ── PR #449 review findings ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every dropzone states this deployment's cap (PR #449, frontend review).
    ///
    /// <para>
    /// The sibling test above bans a LITERAL in the hint. This one closes the other end: a call site
    /// that passes neither <c>Hint</c> nor <c>MaxMegabytes</c> renders the fallback, which omits the
    /// size clause entirely rather than asserting a default that may not be this deployment's. Three
    /// Journal call sites regressed exactly that way when the hardcoded "25 MB" default was removed —
    /// silently, because a missing clause reads as a design choice rather than a defect.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_file_upload_call_site_states_its_cap()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ClientSource.Root, "*.razor", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (Match use in Regex.Matches(source, @"<OdsFileUpload\b"))
            {
                var close = source.IndexOf("/>", use.Index, StringComparison.Ordinal);
                Assert.True(close > 0, $"Unterminated <OdsFileUpload> in {ClientSource.Relative(file)}.");

                var element = source[use.Index..close];
                if (!element.Contains("Hint=", StringComparison.Ordinal)
                    && !element.Contains("MaxMegabytes=", StringComparison.Ordinal))
                {
                    offenders.Add($"{ClientSource.Relative(file)}:{source[..use.Index].Count(c => c == '\n') + 1}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Every OdsFileUpload must pass Hint or MaxMegabytes, or its hint drops the size clause:\n"
            + string.Join('\n', offenders));
    }

    /// <summary>
    /// A Toggle row's helper line reaches assistive tech (WCAG 1.3.1 Level A; PR #449, accessibility
    /// review).
    ///
    /// <para>
    /// Every other control type on the page passes <c>AriaDescribedBy</c>; Toggle rows did not, so the
    /// description and any server advisory were visible but unannounced. <c>MudSwitch</c> has no
    /// <c>AriaDescribedBy</c> of its own, which is why <c>OdsSwitch</c> has to expose one — and why the
    /// attribute must sit AFTER the <c>UserAttributes</c> splat, or a call site's stray value wins.
    /// </para>
    /// </summary>
    [Fact]
    public void Toggle_rows_describe_their_control()
    {
        var toggle = Page();
        var fragment = toggle[toggle.IndexOf("private RenderFragment ToggleControl", StringComparison.Ordinal)..];
        fragment = fragment[..fragment.IndexOf(";", StringComparison.Ordinal)];

        Assert.Contains("AriaDescribedBy=", fragment, StringComparison.Ordinal);

        var component = RenderedMarkup("OdsSwitch.razor");
        Assert.Contains("aria-describedby=\"@AriaDescribedBy\"", component, StringComparison.Ordinal);
        Assert.True(
            component.IndexOf("@attributes=\"UserAttributes\"", StringComparison.Ordinal)
                < component.IndexOf("aria-describedby=", StringComparison.Ordinal),
            "aria-describedby must follow the UserAttributes splat, or a call site's value overrides it.");
    }

    /// <summary>
    /// The claim-locked dimming never composites the helper line (WCAG 1.4.3 Level AA; PR #449,
    /// accessibility review).
    ///
    /// <para>
    /// The two field shapes need different targets to mean the same thing. On the Frame shape the
    /// helper line is a SIBLING of <c>.odc-sfield-frame</c>; on the Tile shape it is a DESCENDANT of
    /// <c>.odc-sfield-tile</c>, sitting in <c>.odc-sfield-tile-main</c> beside the label. So dimming
    /// the tile greys the one sentence that says why the row is locked — the same compounding failure
    /// PR #448 fixed once already on <c>OdsSettingRow</c>. The control lives in a separate
    /// <c>.odc-sfield-tile-ctrl</c>, which is what the rule must name.
    /// </para>
    /// </summary>
    [Fact]
    public void The_locked_dimming_spares_the_tile_helper_line()
    {
        var css = Regex.Replace(
            File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.css")),
            @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        foreach (Match rule in Regex.Matches(css, @"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}"))
        {
            if (!Regex.IsMatch(rule.Groups["body"].Value, @"\b(opacity|filter|animation)\s*:"))
            {
                continue;
            }

            foreach (var selector in rule.Groups["selectors"].Value.Split(','))
            {
                var trimmed = selector.Trim();
                if (!trimmed.Contains(".locked", StringComparison.Ordinal))
                {
                    continue;
                }

                // The tile itself encloses the helper line; only its control column may be dimmed.
                Assert.False(
                    trimmed.EndsWith(".odc-sfield-tile", StringComparison.Ordinal)
                        || trimmed.EndsWith(".locked", StringComparison.Ordinal),
                    $"'{trimmed}' composites the tile's helper line, which must stay legible.");
            }
        }

        // And the shape the rule is supposed to have, so deleting it outright fails too.
        Assert.Contains(".odc-sfield-tile-ctrl", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// A unit-bearing row states its unit in the input rather than in prose (PR #449). Three day-valued
    /// rows shipped without one, rendering a bare number beside siblings reading "30 days".
    /// </summary>
    [Fact]
    public void Day_valued_rows_carry_their_unit()
    {
        var catalogue = PageCodeBehind();

        foreach (var key in new[]
                 {
                     "calendarMaxWindowDays", "calendarMaxEventDurationDays",
                     "subscriptionRenewalWindowDays", "insurance-window",
                 })
        {
            var start = catalogue.IndexOf($"new(\"{key}\"", StringComparison.Ordinal);
            Assert.True(start > 0, $"Setting row '{key}' is no longer in the catalogue.");

            // The row ends at the next catalogue entry, or the end of the section.
            var next = catalogue.IndexOf("new(\"", start + 5, StringComparison.Ordinal);
            var row = next > 0 ? catalogue[start..next] : catalogue[start..];

            Assert.True(row.Contains("Unit:", StringComparison.Ordinal),
                $"Setting row '{key}' is day-valued but names no Unit, so it renders a bare number.");
        }
    }

    /// <summary>
    /// The count pill carries its meaning as text, not as a shape (PR #449, test review).
    ///
    /// <para>
    /// The pill is decorative: "Save changes, 3" tells a screen-reader user nothing, so the visible
    /// glyph is <c>aria-hidden</c> and the count is repeated as a NAMED visually-hidden suffix inside
    /// the button — part of its accessible name rather than a separate node an author has to remember
    /// to associate. A badge with no <c>BadgeLabel</c> would announce a bare number, which is why the
    /// fallback names something rather than defaulting to empty.
    /// </para>
    /// </summary>
    [Fact]
    public void The_button_badge_announces_what_it_counts()
    {
        var markup = RenderedMarkup("OdsButton.razor");
        var badge = markup[markup.IndexOf("<span class=\"odc-btn-badge\"", StringComparison.Ordinal)..];
        badge = badge[..badge.IndexOf("</span>", badge.IndexOf("sr-only", StringComparison.Ordinal), StringComparison.Ordinal)];

        // The visible pill is decorative…
        Assert.Matches(@"class=""odc-btn-badge""\s+aria-hidden=""true""", badge);

        // …and the same count is restated for assistive tech, named by BadgeLabel.
        Assert.Contains("sr-only", badge, StringComparison.Ordinal);
        Assert.Contains("@Badge", badge, StringComparison.Ordinal);
        Assert.Contains("BadgeLabel", badge, StringComparison.Ordinal);

        // A bare number is never announced: the fallback names something.
        Assert.Matches(@"BadgeLabel \?\? ""[^""]+""", badge);
    }

    /// <summary>
    /// A call site that sets <c>Badge</c> also names what it counts. Without this the component's
    /// fallback ("pending") silently stands in for a label the author never considered.
    /// </summary>
    [Fact]
    public void Every_badge_call_site_names_its_count()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ClientSource.Root, "*.razor", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (Match use in Regex.Matches(source, @"<OdsButton\b"))
            {
                var close = source.IndexOf(">", use.Index, StringComparison.Ordinal);
                Assert.True(close > 0, $"Unterminated <OdsButton> in {ClientSource.Relative(file)}.");

                var element = source[use.Index..close];
                if (element.Contains("Badge=", StringComparison.Ordinal)
                    && !element.Contains("BadgeLabel=", StringComparison.Ordinal))
                {
                    offenders.Add($"{ClientSource.Relative(file)}:{source[..use.Index].Count(c => c == '\n') + 1}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "An OdsButton Badge must be paired with a BadgeLabel naming what it counts:\n"
            + string.Join('\n', offenders));
    }
}
