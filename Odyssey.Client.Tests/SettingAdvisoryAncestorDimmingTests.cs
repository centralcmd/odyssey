using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// No ancestor of the advisory may dim it (issue #437 §3).
///
/// <para>
/// <strong>The hazard is real arithmetic, and it is now LIVE rather than one repair away.</strong> At
/// 0.55 the advisory drops to 2.52:1 light / 2.89:1 dark for read-only administrators — the persona the
/// fault surfaces exist for. Under the old one-card-per-setting layout the rule that would have done it
/// (<c>.ss-row.ss-disabled { opacity: .55 }</c>) was dead, because <c>ss-row</c> was <em>forwarded</em>
/// into <c>OdsSettingRow</c> as a <c>Class</c> and landed on <c>MudPaper</c>'s div, which never carries
/// the page stylesheet's scope attribute — and because the advisory was a <em>sibling</em> of the row
/// body inside <c>OdsCard</c>'s root, so nothing behind a <c>::deep</c> could be an ancestor of it.
/// </para>
///
/// <para>
/// Neither of those safeties survives the settings grid. <c>OdsSettingField</c> renders the advisory as
/// a CHILD of the field root (<c>.odc-sfield</c>), alongside the frame, the error and the helper line,
/// and the page reaches into that subtree with <c>::deep</c> to grey a claim-locked row. So a rule
/// aimed one level too high — <c>.odc-sfield.locked</c> rather than <c>.odc-sfield.locked
/// .odc-sfield-frame</c> — dims the advisory for real, and a descendant cannot opt out of an ancestor's
/// <c>opacity</c>. That is exactly the design-system contract this guard now enforces: the frame and its
/// value grey out, the helper line and the advisory stay fully legible, because the reason a row is
/// locked is the part still worth reading.
/// </para>
///
/// <para>
/// <strong>Why this is a rule over the markup rather than a list of classes.</strong> Hard-coding the
/// ancestor set goes green through any layout change that renames it — and this file has now survived
/// one, which is the argument for the derivation rather than against it.
/// </para>
///
/// <para>
/// The existing <c>The_advisory_avoids_the_two_opacity_traps</c> looks only inside
/// <c>OdsSettingRow.razor.css</c>, which is exactly why an ancestor rule in another file was invisible
/// to it.
/// </para>
/// </summary>
public class SettingAdvisoryAncestorDimmingTests
{
    /// <summary>
    /// Compositing vectors, all three of them. <c>filter: opacity(.55)</c> and
    /// <c>filter: brightness(…)</c> composite the subtree exactly as <c>opacity</c> does and would slip
    /// past an opacity-only lint. <c>animation</c> is the third, and the proof is in this very sheet:
    /// <c>@keyframes ssPulse</c> sets <c>opacity</c> and is applied at three call sites, so a
    /// row-ancestor rule with <c>animation: ssPulse …</c> composites the subtree the same way. Today's
    /// call sites are skeleton elements, so banning it costs nothing — and it closes the vector before
    /// someone reaches for it as the "gentler" disabled affordance.
    /// </summary>
    private static readonly string[] BannedProperties = ["opacity", "filter", "animation", "animation-name"];

    private static string PageMarkup() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor"));

    private static string PageCss() =>
        Regex.Replace(
            File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.css")),
            @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    private static string PageCodeBehind() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));

    /// <summary>The advisory element's own class, and the field root that encloses it.</summary>
    private const string AdvisoryClass = "odc-sfield-advisory";

    private const string FieldRootClass = "odc-sfield";

    /// <summary>
    /// Both names are asserted to still exist in <c>OdsSettingField</c>, and the advisory to still be a
    /// child of the field root rather than of the frame. Without this, renaming either class would
    /// leave the <c>::deep</c> carve-out below matching nothing — and a carve-out that matches nothing
    /// waves every rule through. That is exactly how this guard went quietly vacuous when the settings
    /// page moved from <c>OdsSettingRow</c> (advisory a SIBLING of the row body, so no <c>::deep</c>
    /// could reach it) to <c>OdsSettingField</c> (advisory a CHILD of the field root, so one can).
    /// </summary>
    [Fact]
    public void The_advisory_is_a_child_of_the_field_root()
    {
        var component = File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "OdsSettingField.razor"));

        Assert.Contains(AdvisoryClass, component, StringComparison.Ordinal);
        Assert.Contains($"\"{FieldRootClass}\"", component, StringComparison.Ordinal);

        // The advisory renders outside the frame/tile, as a sibling of the helper line — so the ONLY
        // in-component ancestor it has is the field root.
        var frameEnd = component.IndexOf("</fieldset>", StringComparison.Ordinal);
        Assert.True(frameEnd > 0, "OdsSettingField no longer renders a <fieldset> frame.");
        Assert.True(component.IndexOf(AdvisoryClass, frameEnd, StringComparison.Ordinal) > 0,
            "The advisory must render outside the notched frame, not inside it.");
    }

    /// <summary>
    /// Derives the class tokens of every element that <em>encloses</em> a rendered setting field, split
    /// by how they got there.
    ///
    /// <para>
    /// A rule, not a list. The page-declared half comes from the <c>class="…"</c> of each element still
    /// open at the <c>@SettingField(item)</c> call site — the one place in the markup where a field is
    /// actually placed in the DOM, as opposed to the <c>@code</c> fragments that define its shape. The
    /// forwarded half comes from the string literals in <c>FieldClass</c>, which is what the field's own
    /// <c>Class</c> parameter resolves to; reading the attribute itself would yield nothing, since it is
    /// a method call rather than an interpolated literal.
    /// </para>
    ///
    /// <para>
    /// Today that yields <c>{ss-sect-stack, ss-sect, ss-sect-body, odc-sfield-grid}</c> and
    /// <c>{locked, ss-flash}</c> — the expected OUTPUT, not the definition, so the derivation keeps
    /// working when the markup changes.
    /// </para>
    /// </summary>
    private static (List<string> Declared, List<string> Forwarded) AncestorClasses()
    {
        var markup = PageMarkup();
        var callSite = markup.IndexOf("@SettingField(item)", StringComparison.Ordinal);
        Assert.True(callSite >= 0, "No @SettingField(item) call site found in Settings.razor.");

        // Open elements, innermost last. Only the ones still OPEN at the call site enclose the field —
        // collecting every element's classes instead would sweep in siblings (the section head, the
        // skeleton blocks, the page-level banners), which is the false-positive surface the whole-token
        // matching below also guards.
        var stack = new List<(string Name, string[] Classes)>();

        foreach (Match tag in Regex.Matches(markup, @"<(?<close>/?)(?<name>[A-Za-z][\w.]*)(?<attrs>[^>]*?)(?<self>/?)>"))
        {
            if (tag.Index > callSite)
            {
                break;
            }

            var name = tag.Groups["name"].Value;

            if (tag.Groups["close"].Value.Length > 0)
            {
                var last = stack.FindLastIndex(open => open.Name == name);
                if (last >= 0)
                {
                    stack.RemoveRange(last, stack.Count - last);
                }

                continue;
            }

            if (tag.Groups["self"].Value.Length > 0)
            {
                continue;
            }

            var classAttribute = Regex.Match(tag.Groups["attrs"].Value, @"\bclass=""(?<value>[^""]*)""");
            var classes = classAttribute.Success
                ? classAttribute.Groups["value"].Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(token => Regex.IsMatch(token, "^[a-z0-9-]+$"))
                    .ToArray()
                : [];

            stack.Add((name, classes));
        }

        var declared = stack.SelectMany(open => open.Classes).Distinct(StringComparer.Ordinal).ToList();

        // The forwarded half: every string literal in FieldClass's body. Extracted rather than read as
        // a literal list, because this is the half that can silently yield nothing.
        var code = PageCodeBehind();
        var fieldClass = code.IndexOf("private string? FieldClass(SettingItem item)", StringComparison.Ordinal);
        Assert.True(fieldClass >= 0, "No FieldClass(SettingItem) found in Settings.razor.cs.");

        var body = code[fieldClass..];
        var bodyEnd = body.IndexOf("\n    }", StringComparison.Ordinal);
        Assert.True(bodyEnd > 0, "FieldClass's body could not be delimited.");

        var forwarded = Regex.Matches(body[..bodyEnd], @"""(?<token>[a-z0-9][a-z0-9-]*)""")
            .Select(match => match.Groups["token"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return (declared, forwarded);
    }

    /// <summary>
    /// The derivation itself is asserted, because a derivation that quietly returns nothing makes every
    /// assertion below pass vacuously.
    ///
    /// <para>
    /// The <strong>forwarded half specifically</strong> is asserted non-empty — that is the half that
    /// can silently yield nothing, since the <c>Class</c> it reads is an interpolated expression rather
    /// than a literal. Asserting only the union would be satisfied by the page-declared literals alone
    /// while the extraction still returned nothing, leaving the vacuous pass intact behind an assertion
    /// that reads as closing it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_derivation_finds_both_halves()
    {
        var (declared, forwarded) = AncestorClasses();

        Assert.NotEmpty(forwarded);
        Assert.Contains("locked", forwarded);
        Assert.Contains("ss-flash", forwarded);

        // These four are page-declared elements, so their rules are LIVE today — they are the half of
        // the guard that keeps doing work whatever happens to the forwarded classes. ss-sect-body in
        // particular is the one the page actually reaches through, with ::deep.
        Assert.Contains("ss-sect-stack", declared);
        Assert.Contains("ss-sect", declared);
        Assert.Contains("ss-sect-body", declared);
        Assert.Contains("odc-sfield-grid", declared);
    }

    /// <summary>
    /// No rule whose selector names a row-ancestor class may set a compositing property.
    ///
    /// <para>
    /// Matching is on the declaration's <strong>property name</strong>, not a substring: <c>\bopacity\b</c>
    /// matches <c>transition: opacity …</c>, and <c>\bfilter\b</c> matches <em>inside</em>
    /// <c>backdrop-filter</c>, because <c>-</c>→<c>f</c> is a word boundary — so the two exclusions the
    /// obvious mechanism promises are not the ones it delivers.
    /// </para>
    ///
    /// <para>
    /// Classes are matched as <strong>whole tokens</strong> for the same reason. Substring matching on
    /// the selector pulls in <c>.set-section-head</c>, <c>.set-section-ic</c>, <c>.set-section-title</c>
    /// and <c>.set-section-rule</c>, which sit inside the section head — siblings of <c>.pref-list</c>,
    /// not ancestors of the row. None declares either property today, so that is a latent
    /// <em>false-positive</em> surface, and a false positive is exactly how this guard's failure mode
    /// arrives: someone hits it and writes the exception.
    /// </para>
    ///
    /// <para>
    /// The carve-out is a <c>::deep</c> segment aimed at something that is neither the advisory nor a
    /// descendant of it — which is what keeps <c>filter: grayscale(.4)</c> on the icon legal, and what
    /// lets the eventual repair's <c>::deep .odc-setting-control</c> fix pass.
    /// </para>
    /// </summary>
    [Fact]
    public void No_row_ancestor_rule_composites_the_subtree()
    {
        var (declared, forwarded) = AncestorClasses();
        var ancestors = declared.Concat(forwarded).Distinct(StringComparer.Ordinal).ToList();

        var offenders = new List<string>();

        foreach (Match rule in Regex.Matches(PageCss(), @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}"))
        {
            var selector = rule.Groups["selector"].Value.Trim();

            var namesAnAncestor = ancestors.Any(cls =>
                Regex.IsMatch(selector, $@"\.{Regex.Escape(cls)}(?![A-Za-z0-9_-])"));
            if (!namesAnAncestor)
            {
                continue;
            }

            // A ::deep segment reaches DESCENDANTS of the page's own elements, so whether it is an
            // ancestor of the advisory depends on where it lands. The advisory is a child of the FIELD
            // ROOT and a sibling of the frame, the helper line and the error — so a tail ending at
            // `.odc-sfield-frame`, `.odc-sfield-ctrl` or any other sibling is safe, while one ending at
            // the field root itself (or at the advisory) is not.
            //
            // Only the RIGHTMOST compound decides: `.odc-sfield.locked .odc-sfield-frame` names the
            // root on its way past, but what it styles is the frame.
            var deep = selector.IndexOf("::deep", StringComparison.Ordinal);
            if (deep >= 0 && !TargetsTheAdvisoryOrItsRoot(selector[deep..]))
            {
                continue;
            }

            foreach (var declaration in rule.Groups["body"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = declaration.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                var property = declaration[..colon].Trim();
                if (!BannedProperties.Contains(property, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // `animation` is banned for what its keyframes DO, not for its name: the vector is
                // `@keyframes ssPulse`, which sets `opacity`. A keyframe set that only moves
                // `box-shadow` — the jump-to-field attention ring — composites nothing and dims
                // nothing, so flagging it would be a false positive, and a false positive is exactly
                // how this guard's failure mode arrives: someone hits it and writes the exception.
                // Unresolvable keyframes fail CLOSED.
                if (property.StartsWith("animation", StringComparison.OrdinalIgnoreCase)
                    && !KeyframesComposite(declaration[(colon + 1)..]))
                {
                    continue;
                }

                offenders.Add($"{selector} {{ {property}: … }}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Settings.razor.css rules that composite an ancestor of the setting-row advisory, dimming it "
            + "below 4.5:1 for read-only administrators. Move the affordance behind a `::deep` aimed at "
            + "the row's own content instead: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// Whether a <c>::deep</c> tail lands ON the advisory or on the field root that encloses it, as
    /// opposed to a sibling inside the same field.
    /// </summary>
    private static bool TargetsTheAdvisoryOrItsRoot(string deepTail)
    {
        // Selector lists are already split by the caller's rule regex only at `{`, so a grouped
        // selector reaches here whole; each alternative is judged on its own rightmost compound.
        foreach (var alternative in deepTail.Split(','))
        {
            var compounds = alternative
                .Replace(">", " ").Replace("+", " ").Replace("~", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (compounds.Length == 0)
            {
                continue;
            }

            var last = compounds[^1];
            if (Regex.IsMatch(last, $@"\.{Regex.Escape(AdvisoryClass)}(?![A-Za-z0-9_-])")
                || Regex.IsMatch(last, $@"\.{Regex.Escape(FieldRootClass)}(?![A-Za-z0-9_-])"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the keyframes an <c>animation</c> shorthand names composite the subtree — i.e. whether
    /// any of their steps sets <c>opacity</c> or <c>filter</c>. An animation whose keyframes cannot be
    /// found in this sheet counts as compositing, so the escape hatch is not "define it elsewhere".
    /// </summary>
    private static bool KeyframesComposite(string animationValue)
    {
        var css = PageCss();

        // `animation: none` is the reduced-motion opt-OUT — it removes an animation rather than adding
        // one, so it can composite nothing. Handled before the fail-closed branch below, which would
        // otherwise flag every `prefers-reduced-motion` override on the page.
        if (Regex.IsMatch(animationValue.Trim(), "^none$", RegexOptions.IgnoreCase))
        {
            return false;
        }

        var names = animationValue
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => Regex.IsMatch(token, "^[A-Za-z_][A-Za-z0-9_-]*$"))
            .Where(token => !Regex.IsMatch(
                token,
                "^(none|infinite|normal|reverse|alternate|alternate-reverse|forwards|backwards|both|"
                + "running|paused|linear|ease|ease-in|ease-out|ease-in-out|step-start|step-end|initial|"
                + "inherit|unset)$",
                RegexOptions.IgnoreCase))
            .ToList();

        if (names.Count == 0)
        {
            return true;
        }

        foreach (var name in names)
        {
            var block = Regex.Match(
                css,
                $@"@keyframes\s+{Regex.Escape(name)}\s*\{{(?<body>(?:[^{{}}]|\{{[^{{}}]*\}})*)\}}");
            if (!block.Success)
            {
                return true;
            }

            if (Regex.IsMatch(block.Groups["body"].Value, @"(^|[;{\s])(opacity|filter)\s*:"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The same ban applies to inline styles, because a guard that reads only the stylesheet misses
    /// them — and this page already carries one (on a loading-branch element, harmless where it is).
    /// The derivation already parses the markup, so the assertion is nearly free.
    /// </summary>
    [Fact]
    public void No_row_ancestor_element_composites_itself_inline()
    {
        var (declared, forwarded) = AncestorClasses();
        var ancestors = declared.Concat(forwarded).ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();

        foreach (Match tag in Regex.Matches(PageMarkup(), @"<[A-Za-z][\w.]*(?<attrs>[^>]*?)/?>"))
        {
            var attrs = tag.Groups["attrs"].Value;

            var classAttribute = Regex.Match(attrs, @"\bclass=""(?<value>[^""]*)""");
            if (!classAttribute.Success)
            {
                continue;
            }

            var carriesAncestor = classAttribute.Groups["value"].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(ancestors.Contains);
            if (!carriesAncestor)
            {
                continue;
            }

            var style = Regex.Match(attrs, @"\bstyle=""(?<value>[^""]*)""");
            if (!style.Success)
            {
                continue;
            }

            foreach (var declaration in style.Groups["value"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = declaration.IndexOf(':');
                var property = colon < 0 ? declaration.Trim() : declaration[..colon].Trim();
                if (property is "opacity" or "filter")
                {
                    offenders.Add($"{classAttribute.Groups["value"].Value}: {property}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Elements enclosing a setting field that dim themselves inline: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// The dead rule is <strong>deleted</strong>, not left in place, because otherwise the guard above
    /// is red on arrival — and the cheapest escape for whoever hits it is an exception for
    /// <c>.ss-row.ss-disabled</c>, which is the hard-coded formulation this guard exists to avoid,
    /// arriving from the other end. Deleting it is behaviour-neutral precisely because it is dead.
    /// </summary>
    [Fact]
    public void The_dead_row_dimming_rule_is_gone()
    {
        var css = PageCss();
        var rule = Regex.Match(css, @"\.ss-row\.ss-disabled\s*\{(?<body>[^}]*)\}");

        Assert.False(rule.Success,
            "The dead `.ss-row.ss-disabled { … }` rule is back. It never matched (ss-row is forwarded "
            + "into a child component, so it lands on an element with no scope attribute) and it is the "
            + "one declaration in this sheet that trips the ancestor-dimming guard.");
    }
}
