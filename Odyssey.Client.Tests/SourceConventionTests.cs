using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints in the style of <see cref="RazorStringBindingTests"/> and
/// <see cref="ListPageContractTests"/>: pure text checks over the checked-in <c>.razor</c> sources,
/// no bUnit and no render harness. Each one pins a convention that a previous review found broken
/// in several places at once — the kind of defect that compiles, survives code review, and only
/// shows up to a screen-reader user or in a bundle-size audit.
/// </summary>
public class SourceConventionTests
{
    // ─────────────────────────────────────────────────────────────────────────
    //  1. Boolean-valued ARIA states must carry a string, never a bare bool
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ARIA attributes whose value grammar is <c>"true" | "false"</c> (some also accept
    /// <c>"undefined"</c>/<c>"mixed"</c>, and <c>aria-invalid</c> accepts token values). All of them
    /// are the ones a C# <c>bool</c> gets mistakenly bound to.
    /// </summary>
    private static readonly string[] BooleanAria =
    [
        "aria-expanded", "aria-selected", "aria-checked", "aria-pressed", "aria-disabled",
        "aria-hidden", "aria-busy", "aria-invalid", "aria-modal", "aria-required",
        "aria-readonly", "aria-multiselectable", "aria-atomic", "aria-grabbed",
    ];

    private static readonly Regex BooleanAriaAttribute =
        new($@"\b({string.Join('|', BooleanAria)})\s*=\s*""", RegexOptions.Compiled);

    /// <summary>
    /// Blazor renders a <c>bool</c> attribute value through <c>ToString()</c>, which yields
    /// <c>"True"</c>/<c>"False"</c> — not valid ARIA. Assistive technology discards the invalid token
    /// and falls back to the attribute's implicit default, so <c>aria-expanded="@IsOpen"</c> reports
    /// *collapsed* to a screen reader no matter what the flag says, while looking perfectly correct in
    /// the markup and in the DOM inspector to anyone not reading the accessibility tree (issue #365,
    /// finding 3).
    /// </summary>
    /// <remarks>
    /// The house idiom is the explicit ternary — <c>@(IsOpen ? "true" : "false")</c>, or
    /// <c>@(cond ? "true" : null)</c> where the attribute should be absent rather than false. A value
    /// is accepted when the expression contains a string literal or an explicit lower-casing
    /// conversion; anything else is a bool (or an enum, which has the same defect) reaching the
    /// attribute raw.
    /// </remarks>
    [Fact]
    public void No_boolean_valued_aria_attribute_is_bound_to_a_bare_bool()
    {
        var violations = new List<string>();

        foreach (var file in ClientSource.RazorFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match attr in BooleanAriaAttribute.Matches(text))
            {
                var value = ReadAttributeValue(text, attr.Index + attr.Length);
                if (value is null || !value.StartsWith('@') || ProducesAString(value))
                    continue;

                violations.Add($"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, attr.Index)} — " +
                               $"{attr.Groups[1].Value}=\"{value}\" renders \"True\"/\"False\"; " +
                               $"use @(… ? \"true\" : \"false\")");
            }
        }

        Assert.True(violations.Count == 0,
            "ARIA states bound to a bare bool (screen readers read the implicit default, not the flag):\n" +
            string.Join('\n', violations));
    }

    /// <summary>
    /// Whether a Razor attribute expression evaluates to a string (or <c>null</c>) rather than a bool:
    /// it either selects between string literals, or converts explicitly to lower case.
    /// </summary>
    private static bool ProducesAString(string expression) =>
        expression.Contains('"') || expression.Contains("ToLowerInvariant") || expression.Contains("ToLower(");

    /// <summary>
    /// Reads a Razor attribute value starting just past its opening quote. Razor expressions embed
    /// their own string literals — <c>aria-current="@(today ? "date" : null)"</c> — so the terminating
    /// quote is the first one at paren-depth zero and outside a nested literal, not simply the next
    /// one. Returns <c>null</c> if the value is unterminated.
    /// </summary>
    private static string? ReadAttributeValue(string text, int start)
    {
        var value = new StringBuilder();
        var depth = 0;
        var inLiteral = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                if (inLiteral)
                    inLiteral = false;
                else if (depth > 0)
                    inLiteral = true;
                else
                    return value.ToString();
            }
            else if (!inLiteral && c == '(')
            {
                depth++;
            }
            else if (!inLiteral && c == ')')
            {
                depth--;
            }

            value.Append(c);
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  2. Loading spinners go through OdsSpinner
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The two components allowed to render a raw <c>MudProgressCircular</c>.</summary>
    private static readonly string[] SpinnerOwners = ["OdsSpinner.razor", "OdsButton.razor"];

    /// <summary>
    /// A raw <c>MudProgressCircular</c> renders an unlabelled, unsized busy indicator: no
    /// <c>role="status"</c>, no accessible name, and none of the design-system sizing. <c>OdsSpinner</c>
    /// exists to carry exactly that (issue #365, finding 1), and <c>OdsButton</c> owns the in-button
    /// variant. Every other surface must go through them, or the next loading state ships silent to a
    /// screen reader again.
    /// </summary>
    [Fact]
    public void No_component_renders_a_raw_MudProgressCircular_outside_OdsSpinner_and_OdsButton()
    {
        var violations = new List<string>();

        foreach (var file in ClientSource.RazorFiles())
        {
            if (SpinnerOwners.Contains(Path.GetFileName(file)))
                continue;

            var text = File.ReadAllText(file);
            foreach (Match use in Regex.Matches(text, @"<MudProgressCircular\b"))
            {
                violations.Add($"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, use.Index)} — " +
                               "raw <MudProgressCircular> has no accessible name or DS sizing; use <OdsSpinner>");
            }
        }

        Assert.True(violations.Count == 0,
            "Raw MudProgressCircular outside OdsSpinner/OdsButton:\n" + string.Join('\n', violations));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  3. Every Ods* component has a consumer (or says why it doesn't)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The opt-out a deliberate design-system-parity build carries, per issue #370.</summary>
    private const string ParityMarker = "DS parity";

    private static readonly Regex ParityOptOut =
        new(@"@\*(?:(?!\*@).)*DS parity(?:(?!\*@).)*\*@", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Empty, and it must stay that way. This list existed only to let the lint land ahead of the
    /// issue #370 cleanup while still failing on anything <b>new</b>; that cleanup has since resolved
    /// all eighteen entries — seven deleted as superseded (the OdsToast stack, which lost to
    /// <c>MudSnackbarProvider</c>, and the OdsDrawer/OdsNavItem chrome, which lost to the
    /// <c>.odn-*</c> rail), four given real consumers (OdsCardHeader/OdsCardBody in the dashboard's
    /// recent-transactions card, OdsSkeletonRow as OdsRecordTable's loading shape, OdsTooltip on the
    /// file-preview and calendar-export controls), and the rest marked as deliberate parity builds.
    /// A new unreferenced component must be deleted or marked, never appended here.
    /// </summary>
    private static readonly string[] PendingRemoval = [];

    /// <summary>
    /// An unreferenced component still ships its markup and its CSS, but the real cost is that a
    /// reviewer cannot tell "finished, waiting for a consumer" from "abandoned" — which makes every
    /// design-system-parity PR harder to assess (issue #370). Either it has a consumer, or it declares
    /// itself parity-only.
    /// </summary>
    [Fact]
    public void Every_Ods_component_is_referenced_or_marked_as_a_parity_build()
    {
        var unreferenced = UnreferencedComponents();

        var unexplained = unreferenced.Except(PendingRemoval).ToList();

        Assert.True(unexplained.Count == 0,
            $"Ods* components with no consumer anywhere in Odyssey.Client. Delete them, or mark a " +
            $"deliberate design-system-parity build with a '@* {ParityMarker} — no consumer yet *@' header:\n" +
            string.Join('\n', unexplained));
    }

    /// <summary>
    /// Keeps <see cref="PendingRemoval"/> honest: an entry that has since been deleted, given a
    /// consumer, or marked parity-only must come off the list, or the list quietly becomes a licence
    /// to leave the next dead component in place.
    /// </summary>
    [Fact]
    public void The_pending_removal_list_has_no_stale_entries()
    {
        var unreferenced = UnreferencedComponents();

        var stale = PendingRemoval.Except(unreferenced).ToList();

        Assert.True(stale.Count == 0,
            "PendingRemoval entries that are no longer unreferenced components — remove them from the list:\n" +
            string.Join('\n', stale));
    }

    /// <summary>
    /// The <c>Ods*</c> components under <c>Components/</c> that nothing else in the client uses as a
    /// tag (or resolves via <c>typeof</c>/<c>nameof</c>), excluding those carrying the parity marker.
    /// A component's own file does not count — several describe themselves by name in their header
    /// comment.
    /// </summary>
    private static IReadOnlyList<string> UnreferencedComponents()
    {
        var componentsDir = Path.Combine(ClientSource.Root, "Components");
        var sources = Directory.EnumerateFiles(ClientSource.Root, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(ClientSource.Root, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path: f, Text: File.ReadAllText(f)))
            .ToList();

        var unreferenced = new List<string>();

        foreach (var file in Directory.EnumerateFiles(componentsDir, "Ods*.razor", SearchOption.AllDirectories).Order())
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (ParityOptOut.IsMatch(File.ReadAllText(file)))
                continue;

            // A tag use ("<OdsFoo ", "<OdsFoo>", "<OdsFoo/>") or a programmatic reference. The trailing
            // delimiter keeps "<OdsTable" from counting as a use of a hypothetical "<OdsTab".
            var reference = new Regex($@"<{name}[\s/>]|(?:typeof|nameof)\(\s*{name}\s*\)");

            var referenced = sources.Any(s =>
                !string.Equals(s.Path, file, StringComparison.Ordinal) &&
                !string.Equals(s.Path, file + ".cs", StringComparison.Ordinal) &&
                reference.IsMatch(s.Text));

            if (!referenced)
                unreferenced.Add(name);
        }

        return unreferenced;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  4. The OdsInfoTile foot caption has exactly one implementation
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>OdsInfoTile.Caption</c> returns <c>null</c> for blank text so the tile renders no foot
    /// ELEMENT — the distinction the whole "a foot has to earn its place" rule rests on, and the one a
    /// re-implementation is most likely to lose by returning an empty fragment instead. Seven pages had
    /// grown a byte-identical private copy of it before it was pulled onto the component; they were
    /// still identical, which is exactly the window in which deduplicating is cheap. This keeps an
    /// eighth from appearing and quietly disagreeing with the other seven.
    /// </summary>
    [Fact]
    public void The_tile_foot_caption_helper_is_not_reimplemented_per_page()
    {
        var declaration = new Regex(@"RenderFragment\?\s+Caption\s*\(", RegexOptions.Compiled);

        var offenders = ClientSource.SourceFiles()
            .Select(file => (File: file, Text: File.ReadAllText(file)))
            .Where(f => declaration.IsMatch(f.Text))
            .Select(f => ClientSource.Relative(f.File))
            // The one real declaration, on the component whose Foot parameter it feeds.
            .Where(relative => relative.Replace('\\', '/') != "Components/OdsInfoTile.razor")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Private re-implementations of the tile foot caption — call OdsInfoTile.Caption instead:\n" +
            string.Join('\n', offenders));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  5. A .razor file's inline @code block stays small
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How many lines an inline <c>@code</c> block may hold before the component owes itself a
    /// <c>.razor.cs</c>. Not a formatting preference: the ten files issue #373 found above this line
    /// were the ones where nothing could be unit-tested, every change risked an unrelated feature, and
    /// a reviewer's attention ran out before the end of the file.
    /// </summary>
    private const int MaxInlineCodeLines = 150;

    /// <summary>
    /// Logic belongs in the code-behind partial, which is the convention the repo already follows
    /// (<c>AccountsCard</c>, <c>BudgetsCard</c>, <c>JournalCard</c>, <c>TasksPage</c>, …). What is
    /// allowed to stay in the markup file is what cannot leave it: members whose body contains inline
    /// Razor (<c>@&lt;div&gt;…</c> or a <c>__builder =&gt;</c> fragment), since C# files can't hold
    /// markup. If a block is over the limit even after those, the component itself is doing too much
    /// and wants splitting into child components — that is the move issue #373 made for
    /// <c>FileAnalysisDialog</c>, <c>Users</c> and <c>Account</c>.
    /// </summary>
    [Fact]
    public void No_razor_file_carries_an_oversized_inline_code_block()
    {
        var violations = new List<string>();

        foreach (var file in ClientSource.RazorFiles())
        {
            var lines = File.ReadAllLines(file);
            var start = Array.FindIndex(lines, l => l.TrimStart().StartsWith("@code", StringComparison.Ordinal));
            if (start < 0)
                continue;

            // The block runs to the last line that closes it; everything between the braces counts.
            var end = Array.FindLastIndex(lines, l => l.Trim() == "}");
            var length = end - start - 1;
            if (length <= MaxInlineCodeLines)
                continue;

            violations.Add($"{ClientSource.Relative(file)}:{start + 1} — @code block is {length} lines " +
                           $"(limit {MaxInlineCodeLines}); move it to {Path.GetFileName(file)}.cs");
        }

        Assert.True(violations.Count == 0,
            "Inline @code blocks that belong in a code-behind partial:\n" + string.Join('\n', violations));
    }
}
