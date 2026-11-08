using System.Text.RegularExpressions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #436 AC 28: <see cref="WeatherHistoryWindowRules"/> is public, takes <c>today</c>, and is the <b>only</b>
/// implementation of the overlap and open-end rules. The server's model validation and the WASM client both call it
/// (#437 F1); a second copy in <c>Odyssey.Core</c>, <c>Odyssey.Api</c> or <c>Odyssey.Client</c> would let the two
/// halves disagree about which sets are valid. The patterns are proven live against the one sanctioned home, and the
/// scan set is asserted non-empty, so a moved folder cannot make the lint pass forever (the
/// <c>EstimateEffectiveDatingTests</c> precedent).
/// </summary>
public class WeatherHistoryWindowRulesSourceLintTests
{
    private static readonly string RulesFile = Path.Combine("Odyssey.Dtos", "Journal", "WeatherHistoryWindowDtos.cs");

    private static readonly string[] ScannedProjects = ["Odyssey.Core", "Odyssey.Api", "Odyssey.Client", "Odyssey.Dtos"];

    /// <summary>A file that handles history windows at all — the only files where a window rule could be restated.</summary>
    private static readonly Regex TouchesWindows =
        new(@"\b(HistoryWindows?|WeatherHistoryWindow\w*|WeatherWindow\w*|WindowRule\w*)\b", RegexOptions.Compiled);

    /// <summary>Producing a violation is the rules' job; a caller only consumes them.</summary>
    private static readonly Regex ProducesAViolation = new(@"new\s+WindowRuleViolation\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Naming the overlap/open-end codes other than as a message-switch arm (<c>WindowRule.Overlap =&gt; "…"</c>), which
    /// is how a caller words a violation it was handed.
    /// </summary>
    private static readonly Regex DecidesAnOverlapCode =
        new(@"WindowRule\.(Overlap|OpenEndNotLast)\b(?!\s*=>)", RegexOptions.Compiled);

    /// <summary>
    /// The overlap test itself: one window's end compared with a DIFFERENT window's start
    /// (<c>previous.To.Value &gt;= current.From</c>). A window compared with its own start (<c>r.To &gt;= r.From</c>, the
    /// to-before-from check) is not an overlap and does not match.
    /// </summary>
    private static readonly Regex OverlapComparison = new(
        @"\b(\w+)\.To(?:\.Value)?\s*>=\s*(?!\1\b)\w+\.From\b|\b(\w+)\.From\s*<=\s*(?!\2\b)\w+\.To\b",
        RegexOptions.Compiled);

    /// <summary>The open-end test: a window with no end that is not the last in start order.</summary>
    private static readonly Regex OpenEndComparison =
        new(@"\b(previous|prev|last|earlier)\w*\.To\s+is\s+null\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IReadOnlyList<(string Path, string Code)> ScannedFiles() =>
        ScannedProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(RepositoryRoot.Path, project), "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".razor", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.EndsWith(RulesFile, StringComparison.Ordinal))
            .Select(path => (Path: path, Code: StripComments(File.ReadAllText(path))))
            .Where(file => TouchesWindows.IsMatch(file.Code))
            .ToList();

    [Fact]
    public void The_rules_are_public_static_and_take_today()
    {
        var type = typeof(WeatherHistoryWindowRules);
        Assert.True(type.IsPublic && type.IsAbstract && type.IsSealed, "WeatherHistoryWindowRules must be a public static class.");
        var validate = Assert.Single(type.GetMethods(), m => m.Name == nameof(WeatherHistoryWindowRules.Validate));
        Assert.Contains(validate.GetParameters(), p => p.ParameterType == typeof(DateOnly) && p.Name == "today");
        Assert.Equal("Odyssey.Dtos", type.Assembly.GetName().Name);
    }

    [Fact]
    public void The_lint_scans_every_window_consumer_and_its_patterns_fire_on_the_rules_themselves()
    {
        var names = ScannedFiles().Select(file => Path.GetFileName(file.Path)).ToList();
        foreach (var expected in new[]
                 {
                     "WeatherPointService.cs", "WeatherFetchService.cs", "WeatherHistoryPlan.cs", "WeatherDtos.cs",
                     "WeatherHistoryWindows.cs", "WeatherHistoryWindowsEditor.razor", "WeatherPointDialog.razor.cs",
                 })
        {
            Assert.Contains(expected, names);
        }

        Assert.DoesNotContain("WeatherHistoryWindowDtos.cs", names);

        var rules = StripComments(File.ReadAllText(Path.Combine(RepositoryRoot.Path, RulesFile)));
        Assert.Matches(ProducesAViolation, rules);
        Assert.Matches(DecidesAnOverlapCode, rules);
        Assert.Matches(OverlapComparison, rules);
        Assert.Matches(OpenEndComparison, rules);

        // And the exemptions are real: a message arm and a to-before-from check do not match.
        Assert.DoesNotMatch(DecidesAnOverlapCode, "WindowRule.Overlap => \"overlaps\",");
        Assert.DoesNotMatch(OverlapComparison, ".Where(r => r.To >= r.From)");
    }

    [Fact]
    public void No_other_file_restates_the_overlap_or_open_end_rule()
    {
        var offenders = ScannedFiles()
            .SelectMany(file => new (Regex Pattern, string Rule)[]
                {
                    (ProducesAViolation, "constructs a WindowRuleViolation"),
                    (DecidesAnOverlapCode, "decides an Overlap/OpenEndNotLast code"),
                    (OverlapComparison, "compares one window's end with another's start"),
                    (OpenEndComparison, "tests an earlier window for an open end"),
                }
                .Where(check => check.Pattern.IsMatch(file.Code))
                .Select(check => $"{Path.GetRelativePath(RepositoryRoot.Path, file.Path)} {check.Rule}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These files restate a history-window rule instead of calling WeatherHistoryWindowRules: " + string.Join("; ", offenders));
    }

    private static string StripComments(string code) =>
        Regex.Replace(Regex.Replace(code, @"/\*.*?\*/|@\*.*?\*@", string.Empty, RegexOptions.Singleline),
            @"//.*?$", string.Empty, RegexOptions.Multiline);
}
