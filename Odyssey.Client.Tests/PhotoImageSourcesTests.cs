using Moq;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>Issue #365 AC 9: the one client rule deciding a photo image's sources.</summary>
public class PhotoImageSourcesTests
{
    private static readonly Guid PhotoId = Guid.NewGuid();
    private static readonly Guid FileId = Guid.NewGuid();

    private static (IPhotosApiClient Photos, IFilesApiClient Files) Clients(string? thumb = "/thumb", string? original = "/original")
    {
        var photos = new Mock<IPhotosApiClient>();
        photos.Setup(p => p.ThumbnailUrl(It.IsAny<Guid>(), It.IsAny<ThumbnailSize>())).Returns(thumb!);
        var files = new Mock<IFilesApiClient>();
        files.Setup(f => f.ContentUrl(It.IsAny<Guid>())).Returns(original!);
        return (photos.Object, files.Object);
    }

    [Theory]
    [InlineData(true, true, true, "/thumb", "/original")]
    [InlineData(true, false, true, "/thumb", null)]
    [InlineData(false, true, true, null, "/original")]
    [InlineData(false, false, true, null, null)]
    [InlineData(true, true, false, null, "/original")]
    [InlineData(true, false, false, null, null)]
    [InlineData(false, true, false, null, "/original")]
    [InlineData(false, false, false, null, null)]
    public void Each_claim_and_flag_combination_yields_its_sources(bool photosRead, bool filesRead, bool ready, string? thumb, string? original)
    {
        var (photos, files) = Clients();

        var source = PhotoImageSources.For(PhotoId, FileId, ready, ThumbnailSize.Small, new PhotoImageAccess(photosRead, filesRead), photos, files);

        Assert.Equal(new PhotoImageSource(thumb, original), source);
    }

    [Fact]
    public void A_missing_photo_or_file_id_drops_that_source()
    {
        var (photos, files) = Clients();
        var all = new PhotoImageAccess(true, true);

        Assert.Equal(new PhotoImageSource(null, "/original"), PhotoImageSources.For(null, FileId, true, ThumbnailSize.Small, all, photos, files));
        Assert.Equal(new PhotoImageSource("/thumb", null), PhotoImageSources.For(PhotoId, null, true, ThumbnailSize.Small, all, photos, files));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_builder_returning_null_or_empty_is_absent_never_an_empty_src(string? url)
    {
        var (photos, files) = Clients(url, url);

        var source = PhotoImageSources.For(PhotoId, FileId, true, ThumbnailSize.Small, new PhotoImageAccess(true, true), photos, files);

        Assert.True(source.IsEmpty);
    }

    [Fact]
    public void The_requested_size_reaches_the_url_builder()
    {
        var photos = new Mock<IPhotosApiClient>();
        photos.Setup(p => p.ThumbnailUrl(PhotoId, ThumbnailSize.Medium)).Returns("/medium");
        var (_, files) = Clients();

        var source = PhotoImageSources.For(PhotoId, FileId, true, ThumbnailSize.Medium, new PhotoImageAccess(true, true), photos.Object, files);

        Assert.Equal("/medium", source.Thumb);
    }
}
