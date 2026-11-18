using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The OPML import dialog's source-lint (issue #509 AC 21, §8.5, S2): no restated import limit, no markup or link built
/// from a report value, and a fallback that names the shipped defaults rather than repeating them.
/// </summary>
public class ImportNewsFeedsSourceLintTests
{
    private static readonly string[] DialogFiles =
    [
        Path.Combine("Pages", "Internet", "ImportNewsFeedsDialog.razor"),
        Path.Combine("Pages", "Internet", "ImportNewsFeedsDialog.razor.cs"),
        Path.Combine("Pages", "Internet", "ImportNewsFeedsReport.razor"),
    ];

    private static IEnumerable<(string Name, string Source)> Sources() =>
        DialogFiles.Select(f => (f, ClientSource.WithoutComments(File.ReadAllText(Path.Combine(ClientSource.Root, f)))));

    [Fact]
    public void The_scan_set_exists() => Assert.All(DialogFiles, f => Assert.True(File.Exists(Path.Combine(ClientSource.Root, f)), f));

    [Theory] // AC 21: the shipped defaults (64 MB, 100,000 feeds), the byte factor and the entry floor
    [InlineData("64")]
    [InlineData("100000")]
    [InlineData("100_000")]
    [InlineData("100,000")]
    [InlineData("1024")]
    [InlineData("100")]
    public void No_import_limit_is_restated_as_a_literal(string literal)
    {
        var pattern = new Regex($@"(?<![\w.#,-]){Regex.Escape(literal)}(?![\w.,%-]|px)");
        var offenders = Sources().Where(s => pattern.IsMatch(s.Source)).Select(s => s.Name).ToList();

        Assert.True(offenders.Count == 0, $"Read the limit from IImportLimitsCache, not as {literal}: " + string.Join(", ", offenders));
    }

    [Fact] // AC 21 (S2)
    public void No_markup_or_link_is_built_from_report_values()
    {
        var pattern = new Regex(@"MarkupString|AddMarkupContent|innerHTML|href", RegexOptions.IgnoreCase);
        var offenders = Sources().Where(s => pattern.IsMatch(s.Source)).Select(s => s.Name).ToList();

        Assert.True(offenders.Count == 0, "Report values are text, never markup or links: " + string.Join(", ", offenders));
    }

    [Fact] // AC 21: the fallback limits name the SystemSettingsDefaults constants
    public void The_fallback_names_the_shipped_defaults()
    {
        var cache = File.ReadAllText(Path.Combine(ClientSource.Root, "Services", "ImportLimitsCache.cs"));

        Assert.Contains("NewsOpmlMaxImportEntries = SystemSettingsDefaults.NewsOpmlMaxImportEntries", cache, StringComparison.Ordinal);
        Assert.Contains("NewsOpmlMaxImportMegabytes = SystemSettingsDefaults.NewsOpmlMaxImportMegabytes", cache, StringComparison.Ordinal);
        Assert.Equal(Odyssey.Dtos.SystemSettingsDefaults.NewsOpmlMaxImportMegabytes, Odyssey.Client.Services.ImportLimitsCache.Fallback.NewsOpmlMaxImportMegabytes);
        Assert.Equal(Odyssey.Dtos.SystemSettingsDefaults.NewsOpmlMaxImportEntries, Odyssey.Client.Services.ImportLimitsCache.Fallback.NewsOpmlMaxImportEntries);
    }

    [Fact] // §8.5: the byte arithmetic is named
    public void The_byte_factor_is_a_named_constant()
    {
        var code = Sources().Single(s => s.Name.EndsWith(".razor.cs", StringComparison.Ordinal)).Source;

        Assert.Contains("const long BytesPerMegabyte", code, StringComparison.Ordinal);
    }
}
