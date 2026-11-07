using Moq;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #365 AC 9, as amended by issue #428 §5.2: the one client rule deciding a photo image's sources. Both the
/// thumbnail and the original are photo data under <c>photos.read</c>; <c>files.read</c> reaches neither.
/// </summary>
public class PhotoImageSourcesTests
{
    private static readonly Guid PhotoId = Guid.NewGuid();

    private static IPhotosApiClient Photos(string? thumb = "/thumb", string? original = "/original")
    {
        var photos = new Mock<IPhotosApiClient>();
        photos.Setup(p => p.ThumbnailUrl(It.IsAny<Guid>(), It.IsAny<ThumbnailSize>())).Returns(thumb!);
        photos.Setup(p => p.ContentUrl(It.IsAny<Guid>())).Returns(original!);
        return photos.Object;
    }

    [Theory]
    [InlineData(true, true, "/thumb", "/original")]
    [InlineData(true, false, null, "/original")]
    [InlineData(false, true, null, null)]
    [InlineData(false, false, null, null)]
    public void Each_claim_and_flag_combination_yields_its_sources(bool photosRead, bool ready, string? thumb, string? original)
    {
        var source = PhotoImageSources.For(PhotoId, ready, ThumbnailSize.Small, new PhotoImageAccess(photosRead), Photos());

        Assert.Equal(new PhotoImageSource(thumb, original), source);
    }

    [Fact]
    public void A_missing_photo_id_yields_no_source()
    {
        Assert.Equal(PhotoImageSource.None, PhotoImageSources.For(null, true, ThumbnailSize.Small, new PhotoImageAccess(true), Photos()));
    }

    [Fact]
    public void The_original_comes_from_the_photo_content_endpoint()
    {
        var photos = new Mock<IPhotosApiClient>();
        photos.Setup(p => p.ContentUrl(PhotoId)).Returns("/api/photos/x/content");

        var source = PhotoImageSources.For(PhotoId, false, ThumbnailSize.Small, new PhotoImageAccess(true), photos.Object);

        Assert.Equal("/api/photos/x/content", source.Original);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_builder_returning_null_or_empty_is_absent_never_an_empty_src(string? url)
    {
        var source = PhotoImageSources.For(PhotoId, true, ThumbnailSize.Small, new PhotoImageAccess(true), Photos(url, url));

        Assert.True(source.IsEmpty);
    }

    [Fact]
    public void The_requested_size_reaches_the_url_builder()
    {
        var photos = new Mock<IPhotosApiClient>();
        photos.Setup(p => p.ThumbnailUrl(PhotoId, ThumbnailSize.Medium)).Returns("/medium");

        var source = PhotoImageSources.For(PhotoId, true, ThumbnailSize.Medium, new PhotoImageAccess(true), photos.Object);

        Assert.Equal("/medium", source.Thumb);
    }
}
