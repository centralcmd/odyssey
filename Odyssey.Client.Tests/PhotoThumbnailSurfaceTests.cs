using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #365 AC 12: the source-lint that keeps a photo surface from going back to originals by accident. The defect
/// is a literal in source — a <c>ContentUrl</c> call, a <c>background-image</c> — which a render test would not see.
/// </summary>
public class PhotoThumbnailSurfaceTests
{
    /// <summary>The eight surfaces (§3.2), with the file where each builds its photo image.</summary>
    private static readonly string[] Surfaces =
    [
        "Pages/Photos/PhotosCard.razor", "Pages/Photos/PhotosCard.razor.cs",
        "Pages/Photos/AlbumsPage.razor", "Pages/Photos/AlbumsPage.razor.cs",
        "Pages/Photos/AlbumFormDialog.razor", "Pages/Photos/AlbumFormDialog.razor.cs",
        "Pages/Photos/AddToAlbumDialog.razor",
        "Pages/Attachments/AttachPhotosDialog.razor", "Pages/Attachments/AttachPhotosDialog.razor.cs",
        "Pages/Journal/JournalCard.razor.cs",
        "Pages/Journal/JournalEntryFields.razor",
        "Components/OdsPhotoTile.razor",
        "Components/JournalPhotoGallery.razor",
    ];

    /// <summary>The viewer, "Open original" and the edit banner always show the original (§2 non-goals).</summary>
    private static readonly string[] OriginalAllowed = ["Pages/Photos/EditPhotoDialog.razor", "Pages/Photos/PhotoDetailDialog.razor"];

    private static string Code(string relative) =>
        ClientSource.WithoutComments(File.ReadAllText(Path.Combine(ClientSource.Root, relative)));

    [Fact]
    public void The_scan_set_exists()
    {
        Assert.All(Surfaces.Concat(OriginalAllowed), relative => Assert.True(File.Exists(Path.Combine(ClientSource.Root, relative)), relative));
    }

    [Fact]
    public void No_surface_builds_a_content_url_outside_the_source_rule()
    {
        var offenders = Surfaces.Where(file => Code(file).Contains("ContentUrl(", StringComparison.Ordinal)).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_the_viewer_and_the_edit_banner_build_a_content_url_for_a_photo_page()
    {
        var offenders = ClientSource.RazorFilesIn("Pages/Photos", "Components")
            .Select(ClientSource.Relative)
            .Select(file => file.Replace(Path.DirectorySeparatorChar, '/'))
            .Where(file => !OriginalAllowed.Contains(file))
            .Where(file => Code(file).Contains("ContentUrl(", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_surface_puts_a_photo_url_in_a_css_background()
    {
        var offenders = Surfaces
            .Where(file => Regex.IsMatch(Code(file), @"background(-image)?\s*:[^;""]*url\(", RegexOptions.IgnoreCase))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_surface_draws_its_photo_through_OdsPhotoImage()
    {
        var markup = Surfaces.Where(f => f.EndsWith(".razor", StringComparison.Ordinal) && !f.StartsWith("Components/OdsPhotoTile", StringComparison.Ordinal)
                && !f.EndsWith("PhotosCard.razor", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(markup);

        Assert.All(markup, file => Assert.Contains("<OdsPhotoImage", Code(file), StringComparison.Ordinal));
        Assert.Contains("<OdsPhotoImage", Code("Components/OdsPhotoTile.razor"), StringComparison.Ordinal);
        Assert.All(Surfaces.Where(f => f.EndsWith(".razor", StringComparison.Ordinal)),
            file => Assert.DoesNotMatch(@"<img\b", Code(file)));
    }

    [Fact]
    public void OdsPhotoImage_uses_blazor_onerror_and_no_mudimage_or_crossorigin()
    {
        var code = Code("Components/OdsPhotoImage.razor");

        Assert.Contains("@onerror=", code, StringComparison.Ordinal);
        Assert.DoesNotContain("<MudImage", code, StringComparison.Ordinal);
        Assert.DoesNotContain("crossorigin", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror=\"", code.Replace("@onerror=\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void The_thumbnail_url_carries_no_cache_busting_parameter()
    {
        var code = ClientSource.WithoutComments(File.ReadAllText(ClientSource.Sibling("Odyssey.ApiClient/Resources/PhotosApiClient.cs")));
        var builder = Regex.Match(code, @"ThumbnailUrl\(Guid photoId, ThumbnailSize size\)\s*=>\s*(?<body>[^;]+);");

        Assert.True(builder.Success);
        Assert.DoesNotContain("v=", builder.Groups["body"].Value, StringComparison.Ordinal);
    }
}
