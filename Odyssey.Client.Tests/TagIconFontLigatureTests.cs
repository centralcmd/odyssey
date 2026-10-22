using System.Security.Cryptography;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Every <see cref="TagIcons"/> key is a ligature the bundled Material Icons font draws (issue #345 §4,
/// AC 14). A key the font lacks renders as its literal text, and nothing else would notice: the key is
/// valid to the server, and the glyph only fails in a browser.
/// </summary>
/// <remarks>
/// The ligature names come from a checked-in fixture rather than from parsing the WOFF2 here, so the test
/// needs no font parser. The fixture records the font's SHA-256, which is what keeps it honest: swapping
/// the font fails the first test until <c>Fixtures/materialicons-ligatures.py</c> is re-run.
/// </remarks>
public class TagIconFontLigatureTests
{
    private static readonly string FixturePath = ClientSource.Sibling("Odyssey.Client.Tests/Fixtures/materialicons-ligatures.txt");

    [Fact]
    public void The_fixture_was_generated_from_the_bundled_font()
    {
        var header = Header();
        var font = Path.Combine(ClientSource.Root, "wwwroot", "fonts", header["font"]);

        Assert.True(File.Exists(font), $"The fixture names {header["font"]}, which is not in wwwroot/fonts. Re-run the generator.");
        Assert.Equal(header["sha256"], Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(font))));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(font)!, "materialicons-*.woff2"));
    }

    [Fact]
    public void Every_catalogue_key_and_the_default_is_a_ligature_of_the_bundled_font()
    {
        var ligatures = File.ReadLines(FixturePath).Where(line => !line.StartsWith('#')).ToHashSet(StringComparer.Ordinal);

        // A fixture that silently emptied would make the membership check below vacuous.
        Assert.True(ligatures.Count > 1000, $"Only {ligatures.Count} ligatures in the fixture.");

        var missing = TagIcons.All.Select(option => option.Key)
            .Append(TagIcons.Default)
            .Where(key => !ligatures.Contains(key))
            .ToList();
        Assert.True(missing.Count == 0, "Not drawn by the bundled font: " + string.Join(", ", missing));
    }

    private static Dictionary<string, string> Header() =>
        File.ReadLines(FixturePath)
            .TakeWhile(line => line.StartsWith('#'))
            .Select(line => line.TrimStart('#', ' ').Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);
}
