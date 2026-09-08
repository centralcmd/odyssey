using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The settle gate: a surface that submits ids produced by an inline create must wait for those
/// creates to land first.
/// </summary>
/// <remarks>
/// <para>
/// An inline "Add ‹name›" hands its picker a <b>temporary</b> id and POSTs behind it, because the
/// picker's callback cannot await. Submitting before that POST resolves sends an id no server row
/// backs. Every host therefore awaits its creator's <c>WhenSettledAsync()</c> (or, in the
/// file-analysis dialog, its own <c>WhenCreatesSettledAsync()</c>) before it builds a request.
/// </para>
/// <para>
/// This is a source lint rather than a behavioural test because the defect is an ORDERING one: the
/// happy path passes either way, and it only bites when a reviewer clicks submit inside the round
/// trip. A lint is the cheapest thing that fails when the await is dropped or moved after the build.
/// </para>
/// </remarks>
public class StagedCreateSettleGateTests
{
    /// <summary>
    /// Anything that maps a staged id through <c>Resolve(</c> must also await the settle first —
    /// otherwise the map runs while the answer is still unknown and quietly yields null.
    /// </summary>
    [Fact]
    public void Every_host_that_resolves_a_staged_id_also_waits_for_it()
    {
        var offenders = new List<string>();
        var checkedHosts = new List<string>();

        foreach (var file in ClientSource.SourceFiles())
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("Creator.Resolve(", StringComparison.Ordinal))
                continue;

            checkedHosts.Add(ClientSource.Relative(file));
            if (!text.Contains("WhenSettledAsync()", StringComparison.Ordinal))
                offenders.Add(ClientSource.Relative(file));
        }

        Assert.True(offenders.Count == 0,
            "These surfaces map a staged id without awaiting the create that produced it, so a submit "
            + "inside the round trip resolves to null and silently drops the link:\n"
            + string.Join("\n", offenders));

        Assert.True(checkedHosts.Count >= 5,
            "The lint found almost no host to check, so it proves little. Scanned: "
            + string.Join(", ", checkedHosts));
    }

    /// <summary>
    /// The file-analysis import is the one host that stages its creates through the session rather
    /// than a shared creator, and the one whose request parses row ids straight into GUIDs. Its gate
    /// has to run BEFORE the request is built, not merely somewhere in the method.
    /// </summary>
    [Fact]
    public void The_file_analysis_import_settles_before_it_builds_its_request()
    {
        var path = Path.Combine(ClientSource.Root, "Pages", "Finance", "FileAnalysisDialog.razor.cs");
        var text = File.ReadAllText(path);

        var settle = text.IndexOf("await WhenCreatesSettledAsync();", StringComparison.Ordinal);
        var build = text.IndexOf("_session.BuildImportRequest()", StringComparison.Ordinal);

        Assert.True(settle >= 0,
            "ImportAsync no longer waits for staged merchant/category creates, so a reviewer who "
            + "creates one and imports immediately posts a temporary id no server row backs.");
        Assert.True(build > settle,
            "The settle gate must run before BuildImportRequest — after it, the request has already "
            + "captured the temporary ids.");
    }

    /// <summary>
    /// Every staged create must be captured for that gate to have anything to wait on. A callback
    /// raised fire-and-forget without recording its task is invisible to the settle.
    /// </summary>
    [Fact]
    public void The_file_analysis_dialog_records_every_staged_create()
    {
        var path = Path.Combine(ClientSource.Root, "Pages", "Finance", "FileAnalysisDialog.razor.cs");
        var text = File.ReadAllText(path);

        var recorded = Regex.Matches(text, @"_pendingCreates\.Add\(task\);").Count;

        Assert.True(recorded >= 2,
            "Both the merchant and the category create must register their task with _pendingCreates; "
            + $"found {recorded} registration(s).");
    }
}
