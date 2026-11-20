using System.Text.RegularExpressions;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Keeps every clipboard toast on one wording (Odyssey Design System · ActionMenu, "Copy ID"):
/// <c>"Account ID copied."</c> and <c>"Currency code copied."</c>, built by <see cref="OdsCopied"/>.
/// Before it existed the same action toasted "File ID copied.", "ID copied to clipboard." and
/// "Exchange rate ID copied to clipboard." depending on the page.
/// </summary>
public class CopyToastWordingLintTests
{
    private static readonly Regex ToClipboard = new(@"copied to (the )?clipboard", RegexOptions.IgnoreCase);
    private static readonly Regex IdLiteral = new(@"""[^""]*\bID copied");

    private static IEnumerable<(string Path, string Text)> Sources() =>
        ClientSource.SourceFiles()
            .Where(p => !p.EndsWith("OdsCopied.cs", StringComparison.Ordinal))
            .Select(p => (Path.GetRelativePath(ClientSource.Root, p), File.ReadAllText(p)));

    /// <summary>A path-scoped lint that scans nothing passes for the wrong reason.</summary>
    [Fact]
    public void The_scan_reaches_the_client_and_its_copy_toasts()
    {
        var sources = Sources().ToList();
        Assert.NotEmpty(sources);
        Assert.Contains(sources, s => s.Text.Contains("OdsCopied.Id(", StringComparison.Ordinal));
    }

    [Fact]
    public void No_toast_says_to_clipboard()
    {
        var offenders = Sources().Where(s => ToClipboard.IsMatch(s.Text)).Select(s => s.Path).ToList();
        Assert.True(offenders.Count == 0, "Use OdsCopied instead of \"… copied to clipboard\": " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_copy_id_toast_goes_through_OdsCopied()
    {
        var offenders = Sources().Where(s => IdLiteral.IsMatch(s.Text)).Select(s => s.Path).ToList();
        Assert.True(offenders.Count == 0, "Build the Copy ID toast with OdsCopied.Id(noun): " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Account", "Account ID copied.")]
    [InlineData("Exchange rate", "Exchange rate ID copied.")]
    public void The_id_wording_names_the_record(string noun, string expected) =>
        Assert.Equal(expected, OdsCopied.Id(noun));

    [Fact]
    public void The_value_wording_names_what_was_copied() =>
        Assert.Equal("Currency code copied.", OdsCopied.Value("Currency code"));
}
