using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for the upload cap (issue #421 Wave 4).
///
/// <para>
/// Nine upload surfaces used to hold the cap as a <c>private const</c> <em>and</em> interpolate the
/// literal number into their own user-visible text. That combination made the setting useless in both
/// directions: lowering it meant the user uploaded the whole file before the server rejected it, and
/// raising it was unusable because the local pre-check still refused at the old number. Three of them
/// said 25 MB while the server was configured for 64, so the client and the server already disagreed
/// before any of this became editable.
/// </para>
///
/// <para>
/// These are lints rather than reflection tests because the defect is a literal in source: a surface
/// that reintroduces its own constant compiles, passes every behavioural test, and simply stops
/// honouring the administrator's value.
/// </para>
/// </summary>
public class UploadCapSourceTests
{
    /// <summary>
    /// Scoped to files that actually render a file-upload control, which is what makes these lints
    /// precise. Scoping instead to "files that mention the cap" would go blind exactly where it
    /// matters — a brand-new dialog that hardcodes a literal and never references the cache — while
    /// scoping to all of <c>Pages/</c> flags the byte-formatting helpers and the settings catalogue's
    /// prose about the reverse-proxy ceiling, neither of which is an upload cap.
    /// </summary>
    private static IEnumerable<(string File, string Text)> UploadSurfaces() =>
        ClientSource.RazorFilesIn("Pages")
            .Select(file => (File: file, Text: File.ReadAllText(file)))
            .Where(pair => pair.Text.Contains("OdsFileUpload", StringComparison.Ordinal)
                        || pair.Text.Contains("MudFileUpload", StringComparison.Ordinal));

    /// <summary>
    /// A cap-shaped byte literal (<c>N * 1024 * 1024</c>) on an upload surface. The cap belongs to
    /// <see cref="Odyssey.Client.Services.UploadLimitsCache"/>, narrowed by a named per-surface
    /// megabyte constant where a surface is deliberately stricter — never to a page as raw bytes.
    /// </summary>
    [Fact]
    public void No_upload_surface_hardcodes_a_byte_sized_cap()
    {
        var offenders = new List<string>();

        foreach (var (file, text) in UploadSurfaces())
        {
            foreach (Match match in Regex.Matches(text, @"\d+L? \* 1024 \* 1024"))
            {
                offenders.Add($"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, match.Index)} ('{match.Value}')");
            }
        }

        Assert.True(offenders.Count == 0,
            "Upload surfaces holding a hardcoded byte-size cap — it must come from IUploadLimitsCache, "
            + "tightened by a named per-surface megabyte constant where the surface is stricter: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// A hardcoded "NN MB" in an upload surface's user-visible text. The message must name the number
    /// actually in force, or the literal-mismatch defect returns the moment an administrator changes
    /// the cap — which is exactly how the 25-versus-64 disagreement went unnoticed.
    /// </summary>
    [Fact]
    public void No_upload_surface_states_a_literal_megabyte_limit()
    {
        var offenders = new List<string>();

        foreach (var (file, text) in UploadSurfaces())
        {
            foreach (Match match in Regex.Matches(text, @"\b\d+ MB\b"))
            {
                offenders.Add($"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, match.Index)} ('{match.Value}')");
            }
        }

        Assert.True(offenders.Count == 0,
            "Upload surfaces stating a literal megabyte limit — interpolate the effective cap instead, "
            + "or the text goes stale the moment an administrator changes it: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Every surface that actually performs an upload must reach a live cap through a cache. Without
    /// this the two lints above are satisfiable by simply not mentioning a limit at all — a dialog that
    /// pre-validates against nothing, sends the whole file, and lets the server reject it after the
    /// bytes are on the wire.
    ///
    /// <para>
    /// Either cache counts. The four ICS/vCard import dialogs are bound by the <em>import</em> caps
    /// (issue #343), which are a different setting with a different endpoint; requiring the upload cap
    /// there would be wrong, not stricter.
    /// </para>
    ///
    /// <para>
    /// Keyed on <c>ToApiUpload</c> — the one call that turns a picked browser file into a request — so
    /// a pure markup fragment like <c>JournalEntryFields.razor</c> is exempt. It renders the picker but
    /// hands the files to its parent, which resolves the cap in <c>JournalWrite</c>; asserting against
    /// the fragment would be asserting in the wrong file.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_surface_that_uploads_reads_a_live_cap()
    {
        var offenders = UploadSurfaces()
            .Where(pair => pair.Text.Contains("ToApiUpload", StringComparison.Ordinal))
            .Where(pair => !pair.Text.Contains("uploadLimits", StringComparison.OrdinalIgnoreCase)
                        && !pair.Text.Contains("importLimits", StringComparison.OrdinalIgnoreCase))
            .Select(pair => ClientSource.Relative(pair.File))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Surfaces that upload without consulting a live cap — they would pre-validate against "
            + "nothing, or against a literal: " + string.Join(", ", offenders));
    }
}
