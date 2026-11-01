using Odyssey.Core.Imaging;
using Odyssey.Core.Journal;
using Xunit;

namespace Odyssey.Core.Tests.Thumbnails;

/// <summary>The two additive changes to shared code (issue #362 AC 6): <c>ImageWalkResult.IsProgressive</c> and
/// <c>PhotoMetadata.Orientation</c>.</summary>
public class PhotoThumbnailWalkTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void The_walk_reports_a_progressive_jpeg(bool progressive, bool expected)
    {
        var result = ImageContainerWalk.Walk(ThumbnailFixtures.Jpeg(640, 480, progressive), "image/jpeg");

        Assert.True(result.IsOk);
        Assert.Equal((640, 480, expected), (result.Width, result.Height, result.IsProgressive));
    }

    [Fact]
    public void A_walk_that_learns_no_dimensions_is_undecodable()
    {
        Assert.Equal(ImageWalkOutcome.Undecodable, ImageContainerWalk.Walk(ThumbnailFixtures.Png(0, 10), "image/png").Outcome);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(8, 8)]
    [InlineData(null, null)]
    public void The_extractor_reads_the_exif_orientation(int? stored, int? expected)
    {
        var metadata = new PhotoMetadataExtractor().Extract(ThumbnailFixtures.Jpeg(64, 48, orientation: stored));

        Assert.Equal(expected, metadata.Orientation);
    }
}
