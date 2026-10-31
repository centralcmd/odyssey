using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>Issue #365 AC 11: the thumbnail URL is absolute, carries the size, and nothing else.</summary>
public class PhotosApiClientThumbnailTests
{
    [Theory]
    [InlineData(ThumbnailSize.Small, "Small")]
    [InlineData(ThumbnailSize.Medium, "Medium")]
    public void ThumbnailUrl_names_the_photo_and_size_with_no_other_parameter(ThumbnailSize size, string expected)
    {
        var photoId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var api = new OdysseyApi(new HttpClient { BaseAddress = new Uri("http://localhost:5188/") });

        var url = new PhotosApiClient(api).ThumbnailUrl(photoId, size);

        Assert.Equal($"http://localhost:5188/api/photos/{photoId}/thumbnail?size={expected}", url);
    }
}
