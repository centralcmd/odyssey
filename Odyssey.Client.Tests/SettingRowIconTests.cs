using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Guards the settings-row icon failure mode that has actually shipped, as a real <strong>allow-list</strong>
/// (issue #434 goal 7).
///
/// <para>
/// The icon font is a self-hosted, frozen snapshot of the classic Material Icons set
/// (<c>wwwroot/fonts/materialicons-*.woff2</c>) — not Material Symbols, and not whatever
/// fonts.google.com serves today. A name the snapshot lacks fails quietly: the font ligates the longest
/// prefix it <em>does</em> know and renders the remainder as literal text, so <c>event_upcoming</c> — a
/// Symbols-only name whose <c>event</c> prefix IS classic — rendered an <c>event</c> glyph followed by
/// the text "_upcoming", i.e. two glyphs crammed into one icon tile. It was reported from the running
/// app, not caught here.
/// </para>
///
/// <para>
/// <strong>This used to be a deny-list, and that was the wrong shape.</strong> A list of names already
/// observed failing can only ever catch a repeat of a known mistake, and it left a growing surface —
/// issue #434 alone adds sixteen rows — resting on reviewer discipline. The objection recorded at the
/// time was that an allow-list could not be written honestly, because "appears in .razor markup" is a
/// bad proxy for "exists in the font": it rejects six icons that have shipped on this very page since
/// #349/#343 purely because the settings catalogue is their only consumer. That objection was right
/// about the proxy and wrong about the alternative — the ligature list is extractable from the woff2's
/// own GSUB table, so <c>MaterialIconsLigatures.txt</c> is checked in and asserted against the
/// <em>actual font</em> rather than against inference. Its header carries the regeneration recipe.
/// </para>
/// </summary>
public class SettingRowIconTests
{
    /// <summary>
    /// Row icons declared in the settings catalogue: the second positional argument of each
    /// <c>new("key", "icon", …)</c> entry.
    /// </summary>
    private static List<(string Key, string Icon)> CatalogueIcons()
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));

        return Regex.Matches(text, @"new\(""(?<key>[A-Za-z0-9-]+)"",\s*""(?<icon>[a-z0-9_]+)""")
            .Select(match => (match.Groups["key"].Value, match.Groups["icon"].Value))
            .ToList();
    }

    /// <summary>
    /// Every ligature the checked-in font snapshot actually defines, read from its GSUB extract.
    /// <c>#</c> lines are the file's own provenance header.
    /// </summary>
    private static HashSet<string> FontLigatures()
    {
        var path = ClientSource.Sibling(Path.Combine("Odyssey.Client.Tests", "MaterialIconsLigatures.txt"));
        var ligatures = File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        // Guard the guard: a truncated or unreadable data file would make every assertion below fail
        // for the wrong reason, or an empty one make them pass vacuously.
        Assert.True(ligatures.Count > 2000,
            $"Only {ligatures.Count} ligatures loaded from the font extract — the data file is wrong, not the icons.");

        return ligatures;
    }

    [Fact]
    public void Every_settings_row_icon_exists_in_the_checked_in_font()
    {
        var catalogue = CatalogueIcons();
        var ligatures = FontLigatures();

        // Guard the guard: a broken parser would make this pass vacuously.
        Assert.True(catalogue.Count >= 20,
            $"Only found {catalogue.Count} catalogue icons — the parser is broken, not the icons.");

        var offenders = catalogue
            .Where(entry => !ligatures.Contains(entry.Icon))
            .Select(entry => $"{entry.Key} -> '{entry.Icon}'")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These ligatures are absent from the frozen classic Material Icons snapshot and render as "
            + "their longest known prefix plus the rest as text — a glyph and a word in one icon tile: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The six icons whose only consumer is this catalogue must pass. They are why the deny-list was
    /// defended for as long as it was: a markup-presence allow-list would reject all six as absent,
    /// which is a false positive that trains people to weaken the test. A font-derived allow-list
    /// accepts them, and this test says so out loud so the next person does not re-derive the argument.
    /// </summary>
    [Fact]
    public void The_six_catalogue_only_icons_are_in_the_font()
    {
        var ligatures = FontLigatures();

        string[] catalogueOnly =
        [
            "how_to_reg", "mark_email_read", "download_for_offline",
            "format_list_numbered", "file_upload", "sd_storage",
        ];

        var missing = catalogueOnly.Where(name => !ligatures.Contains(name)).ToList();

        Assert.True(missing.Count == 0,
            "The font extract is missing icons this page has shipped since #349/#343, so the extract is "
            + "wrong rather than the catalogue: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The name that actually shipped broken. Kept as an explicit negative case, so the allow-list is
    /// shown to REJECT it rather than merely being asserted to accept everything currently in use.
    /// </summary>
    [Fact]
    public void A_symbols_only_name_is_rejected_by_the_allow_list()
    {
        Assert.DoesNotContain("event_upcoming", FontLigatures());
    }

    /// <summary>
    /// The whole catalogue must at least look like Material Icons ligatures — lowercase, digits and
    /// underscores. Catches an <c>Icons.Material.Filled.X</c> constant pasted into the row slot, which
    /// renders as its own class-path text.
    /// </summary>
    [Fact]
    public void Every_settings_row_icon_is_shaped_like_a_ligature()
    {
        var malformed = CatalogueIcons()
            .Where(entry => !Regex.IsMatch(entry.Icon, "^[a-z][a-z0-9_]*$"))
            .Select(entry => $"{entry.Key} -> '{entry.Icon}'")
            .ToList();

        Assert.True(malformed.Count == 0,
            "Row icons are ligature strings, not MudBlazor SVG constants (the GROUP slot is the "
            + "opposite — it needs Icons.Material.Filled.*): " + string.Join(", ", malformed));
    }

    /// <summary>
    /// <strong>No row title contains a comma or a period</strong> (issue #437 AC 16).
    ///
    /// <para>
    /// This is what makes the fault announcement's comma-separated list and sentence-terminal period
    /// unambiguous — for the character-for-character assertion in <c>SettingsFaultSurfaceTests</c> and
    /// for the spoken form alike. Nothing else protects it: a future title like "Max size, per file"
    /// would silently turn the list into nonsense, and no test of the announcement itself would fail.
    /// </para>
    ///
    /// <para>
    /// It deliberately does NOT ban double quotes. <c>"Expiring soon" window</c> already carries them
    /// and reads correctly, spoken and written; they are only a hazard inside the announcement
    /// template's own literals, which that template avoids.
    /// </para>
    ///
    /// <para>
    /// A source lint rather than reflection over the catalogue, so it sits beside the icon lint it
    /// shares its extraction with.
    /// </para>
    /// </summary>
    [Fact]
    public void No_settings_row_title_contains_a_comma_or_a_period()
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));

        var titles = Regex.Matches(
                text, @"new\(""(?<key>[A-Za-z0-9-]+)"",\s*""[a-z0-9_]+"",\s*""(?<title>(?:[^""\\]|\\.)*)""")
            .Select(match => (Key: match.Groups["key"].Value, Title: match.Groups["title"].Value))
            .ToList();

        // Guard the guard: an extraction that quietly matched nothing would pass vacuously.
        Assert.True(titles.Count >= 60, $"Only {titles.Count} titles extracted — the lint is stale.");

        var offenders = titles
            .Where(entry => entry.Title.Contains(',', StringComparison.Ordinal)
                         || entry.Title.Contains('.', StringComparison.Ordinal))
            .Select(entry => $"{entry.Key} -> \"{entry.Title}\"")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Row titles must contain no comma or period: the fault announcement separates them with "
            + "commas and ends its clause with a period, so either character inside a title makes the "
            + "spoken list ambiguous. Offenders: " + string.Join(", ", offenders));
    }
}
