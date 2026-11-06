using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Background jobs page never renders server text (issue #410 §8, FS6; AC 18): no <c>ApiProblem.Detail</c> or
/// <c>Message</c>, no <c>ApiResult.Error</c>, no raw response body, nothing written to the console, no markup built
/// from a string — and no reference to a user id, worker id, file name, path or place-name field. Modelled on
/// <see cref="LocationMapSourceLintTests"/>; the scan set is the page and its page-local folder.
/// </summary>
public class BackgroundJobsSourceLintTests
{
    private static readonly (string Name, Regex Pattern)[] Forbidden =
    [
        ("ApiProblem.Detail", new Regex(@"\.Detail\b")),
        ("ApiProblem.Message", new Regex(@"\.Message\b")),
        ("ApiResult.Error", new Regex(@"(?<!Severity|Tone)\.Error\b")),
        ("raw response body", new Regex(@"ReadAsString|ReadFromJson|\.Content\b")),
        ("console output", new Regex(@"\bConsole\.|console\.(log|error|warn)")),
        ("markup from a string", new Regex(@"MarkupString|innerHTML|AddMarkupContent")),
        ("a user id", new Regex(@"UserId\b|ActingUser")),
        ("a worker id", new Regex(@"WorkerId\b|InstanceId\b|MachineName")),
        ("a file name", new Regex(@"FileName\b|OriginalName\b")),
        ("a path", new Regex(@"\bRelativePath\b|\bRootPath\b|\.Path\b|FilePath\b")),
        ("a place name", new Regex(@"PlaceName\b|LocationName\b|PointName\b|AddressText\b")),
    ];

    private static IReadOnlyList<string> ScanSet()
    {
        var pages = Path.Combine(ClientSource.Root, "Pages");
        return
        [
            .. Directory.EnumerateFiles(pages, "BackgroundJobs.razor*"),
            .. Directory.EnumerateFiles(Path.Combine(pages, "JobRuns")),
        ];
    }

    [Fact]
    public void The_scan_set_is_the_page_and_its_folder()
    {
        var names = ScanSet().Select(Path.GetFileName).ToList();

        Assert.Contains("BackgroundJobs.razor", names);
        Assert.Contains("BackgroundJobs.razor.cs", names);
        Assert.Contains("JobRunOutcome.cs", names);
        Assert.Contains("JobRunDetailPanel.razor", names);
        Assert.Contains("BackgroundJobTile.razor", names);
    }

    [Fact]
    public void No_file_in_the_scan_set_renders_server_text_or_names_a_person_a_worker_a_file_or_a_place()
    {
        var hits = ScanSet()
            .SelectMany(file => File.ReadAllLines(file).Select((line, i) => (file, line, i)))
            .Where(x => !IsComment(x.line))
            .SelectMany(x => Forbidden.Where(f => f.Pattern.IsMatch(x.line))
                .Select(f => $"{Path.GetFileName(x.file)}:{x.i + 1} uses {f.Name}: {x.line.Trim()}"))
            .ToList();

        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void The_lint_catches_what_it_is_for()
    {
        string[] offenders =
        [
            "Snackbar.Add(result.Problem?.Detail);", "Text = result.Error;", "var body = await r.Content.ReadAsStringAsync();",
            "Console.WriteLine(x);", "@((MarkupString)x)", "run.UserId", "worker.WorkerId", "file.FileName", "obj.RelativePath",
            "place.PlaceName",
        ];

        Assert.All(offenders, line => Assert.Contains(Forbidden, f => f.Pattern.IsMatch(line)));
        Assert.DoesNotContain(Forbidden, f => f.Pattern.IsMatch("Severity=\"Severity.Error\" Tone=\"OdsChipTone.Error\""));
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("@*", StringComparison.Ordinal);
    }
}
