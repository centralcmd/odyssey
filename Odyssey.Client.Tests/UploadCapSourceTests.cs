using System.Text.RegularExpressions;
using Odyssey.Dtos.Application;
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
    private static IEnumerable<(string File, string Text)> UploadSurfaces()
    {
        // Components/ as well as Pages/ (issue #86 §13). It scanned only Pages/ until a shared component
        // rendered an upload field, at which point all three lints below went BLIND to it — a dialog
        // under Components/ could hardcode a cap and every one of them would pass vacuously.
        var all = ClientSource.RazorFilesIn("Pages", "Components")
            .Select(file => (File: file, Text: File.ReadAllText(file)))
            .ToList();

        // A COMPONENT AND ITS CODE-BEHIND ARE ONE SURFACE (issue #94 §6). The markup renders the picker
        // and the code-behind constructs the ApiUpload, so matching each file alone let the crop dialog
        // — the one surface that generates its own bytes — satisfy "reads a live cap" vacuously, by
        // being in neither half of the filter. Pairing them is what makes that check mean something
        // here; it is also what the spec warns splitting the two would break.
        var matched = all
            .Where(pair => pair.Text.Contains("OdsFileUpload", StringComparison.Ordinal)
                        || pair.Text.Contains("MudFileUpload", StringComparison.Ordinal))
            .Select(pair => pair.File)
            .ToHashSet(StringComparer.Ordinal);

        var partners = matched
            .Select(file => file.EndsWith(".razor", StringComparison.Ordinal) ? file + ".cs" : file)
            .Where(File.Exists)
            .ToHashSet(StringComparer.Ordinal);

        return all.Where(pair => matched.Contains(pair.File) || partners.Contains(pair.File));
    }

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
    /// Keyed on the two calls that turn picked content into a request — <c>ToApiUpload</c> for a browser
    /// file, and a direct <c>new ApiUpload(</c> for bytes a surface produced itself (the contact-image
    /// crop encodes its own). A pure markup fragment like <c>JournalEntryFields.razor</c> is exempt: it
    /// renders the picker but hands the files to its parent, which resolves the cap in
    /// <c>JournalWrite</c>, so asserting against the fragment would be asserting in the wrong file.
    /// </para>
    ///
    /// <para>
    /// The second key matters because the first alone reads as "browser file in, request out" — a
    /// surface that generates its own bytes and constructs the upload directly would slip past it while
    /// pre-validating against nothing at all.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_surface_that_uploads_reads_a_live_cap()
    {
        var offenders = UploadSurfaces()
            .Where(pair => pair.Text.Contains("ToApiUpload", StringComparison.Ordinal)
                        || pair.Text.Contains("new ApiUpload(", StringComparison.Ordinal))
            .Where(pair => !pair.Text.Contains("uploadLimits", StringComparison.OrdinalIgnoreCase)
                        && !pair.Text.Contains("importLimits", StringComparison.OrdinalIgnoreCase))
            .Select(pair => ClientSource.Relative(pair.File))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Surfaces that upload without consulting a live cap — they would pre-validate against "
            + "nothing, or against a literal: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// AC 30. The three lints above are <b>path-and-content scoped</b>, and the profile-picture surface
    /// is exactly the shape that can slip past all three: <see cref="UploadSurfaces"/> only sees files
    /// containing <c>OdsFileUpload</c>, and "reads a live cap" keys on <c>new ApiUpload(</c> — so
    /// splitting the picker from the upload construction would make every check vacuous for it.
    ///
    /// <para>
    /// It does <b>not</b> split them: <c>OdsImageCropDialog</c> is the file that renders the picker and
    /// the file that constructs the <c>ApiUpload</c>, which is why it keeps <c>IUploadLimitsCache</c>
    /// rather than taking fully-resolved caps as parameters. This asserts the scan set actually
    /// contains it, so the three above are known not to be passing about nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void The_crop_dialog_is_inside_the_scanned_upload_surfaces()
    {
        var scanned = UploadSurfaces().Select(pair => ClientSource.Relative(pair.File)).ToList();

        Assert.True(scanned.Count > 0, "The upload-surface scan set is empty; the three lints above prove nothing.");

        Assert.Contains(
            scanned,
            name => name.Replace('\\', '/').EndsWith("Components/OdsImageCropDialog.razor", StringComparison.Ordinal));

        Assert.Contains(
            scanned,
            name => name.Replace('\\', '/').EndsWith("Components/OdsImageCropDialog.razor.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// AC 30's other half: no <see cref="UserProfileImageLimits"/> number may appear as a literal
    /// anywhere in the client. Its reach is client-only on purpose — the server's own use of the same
    /// constants is not a client-side copy of anything.
    /// </summary>
    [Fact]
    public void No_profile_image_limit_is_written_as_a_literal_in_the_client()
    {
        // Each number paired with the constant that should have been named instead, so a failure says
        // what to write rather than only what not to.
        var forbidden = new (string Pattern, string Constant)[]
        {
            (@"\b2 \* 1024 \* 1024\b", nameof(UserProfileImageLimits.MaxImageBytes)),
            (@"\b20 \* 1024 \* 1024\b", nameof(UserProfileImageLimits.MaxSourceBytes)),
            (@"\b1024 (×|x) 1024\b", nameof(UserProfileImageLimits.MaxImageDimension)),
            (@"\b8192 (×|x) 8192\b", nameof(UserProfileImageLimits.MaxSourceDimension)),
            (@"\b512 (×|x) 512\b", nameof(UserProfileImageLimits.OutputDimension)),
        };

        var scanned = new List<string>();
        var offenders = new List<string>();

        foreach (var file in ClientSource.SourceFiles())
        {
            var text = File.ReadAllText(file);
            scanned.Add(file);

            foreach (var (pattern, constant) in forbidden)
            {
                foreach (Match match in Regex.Matches(text, pattern))
                {
                    offenders.Add(
                        $"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, match.Index)} "
                        + $"('{match.Value}' — name {constant})");
                }
            }
        }

        Assert.True(scanned.Count > 0, "Scanned no client sources at all.");
        Assert.True(offenders.Count == 0,
            "A profile-picture limit is written as a literal in the client; name the constant on "
            + "UserProfileImageLimits instead: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// AC 31. The navigation rail stays unchanged (issue #94 §2 non-goal 2): the chrome is not an
    /// identity surface in this version, and its Account entry keeps its <c>account_circle</c>
    /// ligature. A lint rather than a review note because adding an avatar to the rail foot is a
    /// one-line change that would look like an improvement.
    /// </summary>
    [Fact]
    public void No_layout_component_references_the_profile_image_surface()
    {
        var layout = Path.Combine(ClientSource.Root, "Layout");
        var files = Directory.EnumerateFiles(layout, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(layout, "*.cs", SearchOption.AllDirectories))
            .ToList();

        Assert.True(files.Count > 0, $"Scanned no files under {layout}.");

        var offenders = files
            .Select(file => (File: file, Text: File.ReadAllText(file)))
            .Where(pair => pair.Text.Contains("profile-images", StringComparison.Ordinal)
                        || pair.Text.Contains("ProfileImageVersion", StringComparison.Ordinal)
                        || pair.Text.Contains("OdsProfilePictureField", StringComparison.Ordinal)
                        || pair.Text.Contains("ImageUrl(", StringComparison.Ordinal))
            .Select(pair => ClientSource.Relative(pair.File))
            .ToList();

        Assert.True(offenders.Count == 0,
            "The navigation chrome is not an identity surface in this version (issue #94 §2 non-goal 2): "
            + string.Join(", ", offenders));
    }
}
