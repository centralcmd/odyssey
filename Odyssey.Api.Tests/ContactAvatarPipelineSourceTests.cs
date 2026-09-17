using System.Text.RegularExpressions;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Source lints over the shared still-image pipeline (issue #86 §4.4, §5.3, §6; issue #94 §5).
///
/// <para>
/// These are lints rather than behavioural tests because every defect they catch <b>compiles, runs and
/// passes every other test</b> — a full EXIF parser on the request path produces the same dimensions a
/// container walk does, an <c>ExecuteDeleteAsync</c> works perfectly against MariaDB, and a fourth
/// release site that forgets the rule deletes files quietly and correctly-looking.
/// </para>
///
/// <para>
/// <b>They are re-pointed at <c>Odyssey.Core/Imaging</c>, and that was a precondition of the
/// extraction rather than a follow-up.</b> These lints are path-scoped, and
/// <c>ContactAvatarService.cs</c> / <c>ContactAvatarRelease.cs</c> stayed behind in
/// <c>Journal/Avatar</c> — so scanning the old directory would still have enumerated files, and every
/// lint would still have <b>passed</b>, while the files they exist to protect had silently left their
/// scope. <see cref="The_imaging_scan_set_is_not_empty"/> is the guard against the same thing
/// happening again.
/// </para>
/// </summary>
public class ContactAvatarPipelineSourceTests
{
    /// <summary>
    /// The shared pipeline: the container walk, the metadata strip, the magic-byte table and the
    /// parameterised validator. Storage-agnostic, so a contact image and a user profile picture run
    /// the identical parser.
    /// </summary>
    private static string ImagingDirectory =>
        Path.Combine(RepositoryRoot.Path, "Odyssey.Core", "Imaging");

    /// <summary>
    /// The surfaces that bind the pipeline to a store. They apply the same lints, because a raster
    /// dependency or a bulk statement is no more acceptable a step away from the walk.
    /// </summary>
    private static string AvatarDirectory =>
        Path.Combine(RepositoryRoot.Path, "Odyssey.Core", "Journal", "Avatar");

    private static string ProfileImageDirectory =>
        Path.Combine(RepositoryRoot.Path, "Odyssey.Core", "Profiles");

    private static IEnumerable<(string File, string Text)> AvatarSources() =>
        new[] { ImagingDirectory, AvatarDirectory, ProfileImageDirectory }
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Select(file => (File: Path.GetFileName(file), Text: WithoutComments(File.ReadAllText(file))));

    /// <summary>
    /// AC 28. A path-scoped lint that scans nothing passes vacuously, which is exactly how the
    /// extraction could have gone wrong: the files move, the directory still exists because two others
    /// stayed behind, and every assertion below keeps reporting success about code it no longer reads.
    /// </summary>
    [Fact]
    public void The_imaging_scan_set_is_not_empty()
    {
        var imaging = Directory.EnumerateFiles(ImagingDirectory, "*.cs", SearchOption.AllDirectories).ToList();

        Assert.True(imaging.Count > 0,
            $"No sources under {ImagingDirectory}: every lint in this class would pass having read "
            + "nothing. The shared pipeline has moved — re-point them.");

        // Named rather than counted, so a file that quietly leaves fails here and not somewhere subtler.
        var names = imaging.Select(Path.GetFileName).ToList();
        Assert.Contains("ImageContainerWalk.cs", names);
        Assert.Contains("StillImageValidator.cs", names);

        Assert.True(AvatarSources().Any(), "The combined scan set is empty.");
    }

    /// <summary>
    /// AC 29. The invariant the whole extraction exists for: there is exactly ONE implementation of
    /// the container walk and the metadata strip in the solution. Two copies of a parser on an
    /// untrusted-input path will diverge, and the copy with fewer eyes on it is the one that will.
    /// </summary>
    [Fact]
    public void Exactly_one_container_walk_exists_and_it_is_under_Odyssey_Core_Imaging()
    {
        var declarations = Directory
            .EnumerateFiles(RepositoryRoot.Path, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Select(file => (File: file, Text: WithoutComments(File.ReadAllText(file))))
            .Where(pair => Regex.IsMatch(pair.Text, @"\b(class|record|struct)\s+\w*ImageContainerWalk\w*\b")
                        || Regex.IsMatch(pair.Text, @"\b(class|record|struct)\s+\w*(MetadataStrip|ImageStripper)\w*\b"))
            .Select(pair => Path.GetRelativePath(RepositoryRoot.Path, pair.File))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            declarations.Count == 1
            && declarations[0].Replace('\\', '/') == "Odyssey.Core/Imaging/ImageContainerWalk.cs",
            "The container walk must be declared exactly once, under Odyssey.Core/Imaging. Found: "
            + string.Join(", ", declarations));
    }

    private static bool IsBuildOutput(string file)
    {
        var relative = Path.GetRelativePath(RepositoryRoot.Path, file);
        return relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Comments discuss the very identifiers these lints ban — the point of several of them is to say
    /// WHY <c>MetadataExtractor</c> is absent. A lint a doc comment can fail is a lint that gets the
    /// doc deleted.
    /// </summary>
    private static string WithoutComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(source, @"^\s*///?.*$", string.Empty, RegexOptions.Multiline);
    }

    // ── AC 8: dimensions come from the walk, not from a metadata parser ───────────────────────────

    [Fact]
    public void MetadataExtractor_is_absent_from_the_avatar_request_path()
    {
        // ImageMetadataReader is a full EXIF/IPTC/ICC/XMP parse, so using it here would put a large
        // parser on the untrusted-input path to learn two integers — and a bounded prefix would fail
        // closed on a legitimate photo whose frame header sits behind large EXIF/MPF/ICC segments. The
        // stripper already walks every segment to decide what to keep, so the dimensions fall out of a
        // walk that has to happen anyway.
        var offenders = AvatarSources()
            .Where(pair => pair.Text.Contains("MetadataExtractor", StringComparison.Ordinal)
                        || pair.Text.Contains("ImageMetadataReader", StringComparison.Ordinal))
            .Select(pair => pair.File)
            .ToList();

        Assert.True(offenders.Count == 0,
            "MetadataExtractor is on the avatar request path. It belongs in TESTS only, where it is the "
            + "independent oracle that makes strip-then-verify meaningful: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_raster_graphics_dependency_reaches_the_avatar_pipeline()
    {
        // The solution deliberately ships none (Directory.Packages.props records the choice of
        // MetadataExtractor over ImageSharp/Magick/SkiaSharp). The two accepted consequences — EXIF
        // Orientation and colour management are lost — follow from that, and a smuggled-in decoder would
        // quietly change what "the stored bytes are the uploaded bytes minus metadata" means.
        string[] banned = ["ImageSharp", "SkiaSharp", "Magick", "System.Drawing"];

        var offenders = AvatarSources()
            .SelectMany(pair => banned
                .Where(name => pair.Text.Contains(name, StringComparison.Ordinal))
                .Select(name => $"{pair.File} ({name})"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A raster-graphics dependency reached the avatar pipeline: " + string.Join(", ", offenders));
    }

    // ── The fast tiers have to be able to run the cleanup ─────────────────────────────────────────

    [Fact]
    public void The_avatar_pipeline_uses_tracked_removes_never_ExecuteDelete()
    {
        // ExecuteDeleteAsync/ExecuteUpdateAsync live in EntityFrameworkCore.Relational and THROW on the
        // EF InMemory provider — so cleanup written that way is unrunnable on the tier the
        // application-code cascade exists to serve. It would pass review and never pass its own test.
        var offenders = AvatarSources()
            .Where(pair => pair.Text.Contains("ExecuteDeleteAsync", StringComparison.Ordinal)
                        || pair.Text.Contains("ExecuteUpdateAsync", StringComparison.Ordinal))
            .Select(pair => pair.File)
            .ToList();

        Assert.True(offenders.Count == 0,
            "The avatar pipeline uses a relational-only bulk statement; use tracked Remove/RemoveRange: "
            + string.Join(", ", offenders));
    }

    // ── The release rule is expressed once (§5) ───────────────────────────────────────────────────

    [Fact]
    public void Every_path_that_releases_an_avatar_file_goes_through_the_shared_rule()
    {
        // THREE paths release a contact's avatar file — DELETE, the contact-delete cascade, and the
        // REPLACE half of POST. An earlier draft guarded the first two and missed the third, so a
        // contact mis-pointed at a PDF would have had that PDF destroyed by the next upload: the
        // likeliest of the three to be reached, since replacing an image is ordinary and deleting one
        // is not.
        var callers = new[]
        {
            Path.Combine(RepositoryRoot.Path, "Odyssey.Core", "Journal", "Avatar", "ContactAvatarService.cs"),
            Path.Combine(RepositoryRoot.Path, "Odyssey.Core", "Journal", "ContactService.cs"),
            Path.Combine(RepositoryRoot.Path, "Odyssey.Core", "Journal", "ContactVCardService.cs"),
        };

        foreach (var caller in callers)
        {
            var text = WithoutComments(File.ReadAllText(caller));
            if (!text.Contains("AvatarFileId", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(
                text.Contains("ContactAvatarRelease.StageAsync", StringComparison.Ordinal)
                || text.Contains("StageReleaseAsync", StringComparison.Ordinal),
                $"{Path.GetFileName(caller)} touches AvatarFileId without going through the shared "
                + "release rule; a site that releases a file outside it can destroy a mis-pointed one.");
        }
    }

    [Fact]
    public void The_release_rule_checks_the_outgoing_files_content_type()
    {
        var rule = WithoutComments(File.ReadAllText(
            Path.Combine(AvatarDirectory, "ContactAvatarRelease.cs")));

        // Detach-never-delete for a file that is not avatar-legal, chosen over refuse-and-404 because it
        // keeps the row recoverable: a mis-pointed reference is a defect to investigate, not a licence
        // to destroy what it points at.
        Assert.Contains("IsAllowedContentType", rule, StringComparison.Ordinal);
        Assert.Contains("DetachedOnly", rule, StringComparison.Ordinal);
        Assert.Contains("LogWarning", rule, StringComparison.Ordinal);
    }

    // ── Log redaction (§10.11) ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Odyssey.Core", "Journal", "Avatar", "ContactAvatarService.cs")]
    [InlineData("Odyssey.Core", "Profiles", "UserProfileImageService.cs")]
    public void No_image_log_line_carries_the_client_declared_content_type_or_a_filename(params string[] path)
    {
        var service = WithoutComments(File.ReadAllText(
            Path.Combine([RepositoryRoot.Path, .. path])));

        foreach (Match log in Regex.Matches(service, @"Log(Information|Warning|Error)\((?<body>[^;]*);", RegexOptions.Singleline))
        {
            var body = log.Groups["body"].Value;

            // The client-declared string is attacker-controlled and a log-injection vector; the
            // VALIDATED type is what gets recorded. A filename or a hash is personal data adjacent.
            Assert.DoesNotContain("declaredContentType", body, StringComparison.Ordinal);
            Assert.DoesNotContain("FileName", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Sha256", body, StringComparison.Ordinal);
        }
    }

    // ── The column's on-delete behaviour is explicit (§6) ─────────────────────────────────────────

    [Fact]
    public void The_avatar_foreign_key_declares_SetNull_explicitly()
    {
        var context = WithoutComments(File.ReadAllText(
            Path.Combine(RepositoryRoot.Path, "Odyssey.Context", "OdysseyContext.cs")));

        var declaration = context[context.IndexOf("contact.AvatarFileId", StringComparison.Ordinal)..];
        declaration = declaration[..declaration.IndexOf(';', StringComparison.Ordinal)];

        // EF's default for an OPTIONAL relationship is ClientSetNull, which emits RESTRICT — under which
        // deleting an avatar's file from the Files page would surface as a 500 rather than the graceful
        // detach that leaves the contact on its type glyph.
        Assert.Contains("DeleteBehavior.SetNull", declaration, StringComparison.Ordinal);
    }

    [Fact]
    public void The_avatar_column_is_uniquely_indexed()
    {
        var entity = WithoutComments(File.ReadAllText(
            Path.Combine(RepositoryRoot.Path, "Odyssey.Context", "Contact.cs")));

        // A file is the avatar of at most one contact, which is what makes "deleting the contact deletes
        // its avatar file" safe. Nullable, so MariaDB permits many NULLs.
        Assert.Contains("[Index(nameof(AvatarFileId), IsUnique = true)]", entity, StringComparison.Ordinal);
        Assert.Contains("public Guid? AvatarFileId", entity, StringComparison.Ordinal);
    }
}
