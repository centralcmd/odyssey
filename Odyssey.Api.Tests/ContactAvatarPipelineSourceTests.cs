using System.Text.RegularExpressions;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Source lints over the contact-image pipeline (issue #86 §4.4, §5.3, §6).
///
/// <para>
/// These are lints rather than behavioural tests because every defect they catch <b>compiles, runs and
/// passes every other test</b> — a full EXIF parser on the request path produces the same dimensions a
/// container walk does, an <c>ExecuteDeleteAsync</c> works perfectly against MariaDB, and a fourth
/// release site that forgets the rule deletes files quietly and correctly-looking.
/// </para>
/// </summary>
public class ContactAvatarPipelineSourceTests
{
    private static string AvatarDirectory =>
        Path.Combine(RepositoryRoot.Path, "Odyssey.Core", "Journal", "Avatar");

    private static IEnumerable<(string File, string Text)> AvatarSources() =>
        Directory.EnumerateFiles(AvatarDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(file => (File: Path.GetFileName(file), Text: WithoutComments(File.ReadAllText(file))));

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

    [Fact]
    public void No_avatar_log_line_carries_the_client_declared_content_type_or_a_filename()
    {
        var service = WithoutComments(File.ReadAllText(
            Path.Combine(AvatarDirectory, "ContactAvatarService.cs")));

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
