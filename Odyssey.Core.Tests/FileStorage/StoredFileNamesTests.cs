using System.Text;
using Odyssey.Core.FileStorage;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>The on-disk naming and path-shape rules of issue #346 §3.3 and §7.5 (AC 2, 18).</summary>
public class StoredFileNamesTests
{
    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\x.jpg", "x")]
    [InlineData(".hidden", "hidden")]
    [InlineData("-rf.jpg", "rf")]
    [InlineData("CON.jpg", "_CON")]
    [InlineData("lpt9.tar.gz", "_lpt9.tar")]
    [InlineData("@#$%^&*", "file")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    [InlineData("Holiday photo.HTML", "Holiday photo")]
    [InlineData("  a   b__c  .png", "a b_c")]
    [InlineData("tab\there.txt", "tab_here")]
    [InlineData("archive.tar.gz", "archive.tar")]
    [InlineData("Ærlig ødeland.pdf", "Ærlig ødeland")]
    public void Sanitizing_produces_the_stated_stem(string? input, string expected) =>
        Assert.Equal(expected, StoredFileNames.SanitizeStem(input, "_x", ".jpg"));

    [Fact]
    public void An_nfd_name_is_normalised_to_nfc()
    {
        var nfd = "Café.jpg";
        var stem = StoredFileNames.SanitizeStem(nfd, null, ".jpg");

        Assert.Equal("Café", stem);
        Assert.True(stem.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void A_long_unicode_name_is_cut_on_a_grapheme_boundary_with_room_for_the_guid_marker()
    {
        // 400 bytes of a grapheme NFC cannot fold — an x with two combining marks, five bytes — so a cut that split
        // one would leave a dangling mark.
        var grapheme = "x\u0301\u0302";
        var input = string.Concat(Enumerable.Repeat(grapheme, 80)) + ".jpg";
        Assert.True(Encoding.UTF8.GetByteCount(input) > 400);

        var stem = StoredFileNames.SanitizeStem(input, "_256", ".webp");
        var longest = StoredFileNames.Compose(stem, "abcdef12", "_256", ".webp");

        Assert.True(Encoding.UTF8.GetByteCount(longest) <= StorageNames.MaxFileNameBytes);
        Assert.Equal(0, stem.Length % grapheme.Length);
        Assert.True(StorageNames.IsConformingFileName(longest));
    }

    [Fact]
    public void The_marker_sits_between_the_stem_and_the_suffix()
    {
        Assert.Equal("IMG_2041 (2)_256.webp", StoredFileNames.Compose("IMG_2041", "2", "_256", ".webp"));
        Assert.Equal("IMG_2041.jpg", StoredFileNames.Compose("IMG_2041", null, null, ".jpg"));
    }

    [Theory]
    [InlineData("_256", true)]
    [InlineData("-x", true)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("123456789012345678901234567890123", false)]
    public void Suffixes_are_validated_not_sanitized(string suffix, bool valid) =>
        Assert.Equal(valid, StorageNames.IsValidSuffix(suffix));

    [Theory]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("IMAGE/WEBP", ".webp")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("application/x-unknown", null)]
    [InlineData(null, null)]
    public void The_extension_comes_from_the_content_type(string? contentType, string? extension) =>
        Assert.Equal(extension, ContentTypeExtensions.For(contentType));

    [Theory]
    [InlineData("test-files/3f/a2/x/IMG.jpg", true)]
    [InlineData("test-files/a/b/c/d/e/IMG.jpg", true)]
    [InlineData("test-files/a/b/c/d/e/f/IMG.jpg", false)]
    [InlineData("test-files/IMG.jpg", false)]
    [InlineData("../outside.jpg", false)]
    [InlineData(".odyssey-volume.json", false)]
    [InlineData("test-files/../x/IMG.jpg", false)]
    [InlineData("test-files/A B/IMG.jpg", false)]
    [InlineData("test-files/x/.hidden.jpg", false)]
    [InlineData("test-files/x/-rf.jpg", false)]
    [InlineData("test-files/x/noext", false)]
    [InlineData("other/x/IMG.jpg", false)]
    public void The_structural_check_admits_only_what_the_store_composes(string path, bool conforming) =>
        Assert.Equal(conforming, StorageNames.IsConformingPath(path, "test-files"));

    [Theory]
    [InlineData("gone-feature/x/y.jpg", true)]
    [InlineData(".quarantine/x/y.jpg", false)]
    [InlineData(".tmp/x/y.jpg", false)]
    [InlineData("lost+found/x/y.jpg", false)]
    [InlineData("../x/y.jpg", false)]
    [InlineData("test-files/x/y.jpg", false)]
    public void The_catalogue_free_check_refuses_reserved_and_catalogue_roots(string path, bool conforming) =>
        Assert.Equal(conforming, StorageNames.IsConformingPathWithoutCatalogue(path, new StorageFeatureCatalogue(TestStorageFeatures.All)));

    [Fact]
    public void Shard_gives_two_hex_segments()
    {
        var id = Guid.Parse("3fa29c0e-8b7d-4e51-a6c2-d9f01b4e7a33");
        Assert.Equal(["3f", "a2"], StorageLayout.Shard(id));
    }

    [Fact]
    public void The_catalogue_refuses_duplicate_or_malformed_root_folders()
    {
        var duplicate = StorageFeature.Create<Guid>("Other", "test-files", _ => ["a"], StorageReleaseMode.Graced,
            StorageFeatureReference.To<TestFileAttachment>(a => a.StoredObjectId));
        var malformed = StorageFeature.Create<Guid>("Bad", "Bad_Root", _ => ["a"], StorageReleaseMode.Graced,
            StorageFeatureReference.To<TestFileAttachment>(a => a.StoredObjectId));

        Assert.Throws<InvalidOperationException>(() => new StorageFeatureCatalogue([TestStorageFeatures.Files, duplicate]));
        Assert.Throws<InvalidOperationException>(() => new StorageFeatureCatalogue([malformed]));
    }
}
