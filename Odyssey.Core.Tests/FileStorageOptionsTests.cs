using Odyssey.Core.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

public class FileStorageOptionsTests
{
    [Fact]
    public void Defaults_Are64MbCapWith1MbEnvelopeHeadroom()
    {
        var options = new FileStorageOptions();

        Assert.Equal(64L * 1024 * 1024, options.MaxFileSizeBytes);
        Assert.Equal(1L * 1024 * 1024, options.RequestEnvelopeHeadroomBytes);
    }

    [Fact]
    public void Defaults_MatchAppsettingsValue()
    {
        // appsettings.json pins FileStorage:MaxFileSizeBytes to this literal; keep them in lockstep.
        Assert.Equal(67108864L, new FileStorageOptions().MaxFileSizeBytes);
    }

    [Fact]
    public void MaxRequestBodyBytes_IsCapPlusHeadroom()
    {
        var options = new FileStorageOptions { MaxFileSizeBytes = 10_000, RequestEnvelopeHeadroomBytes = 500 };

        Assert.Equal(10_500, options.MaxRequestBodyBytes);
    }

    [Fact]
    public void MaxRequestBodyBytes_ExceedsTheFileCap_SoFullSizeUploadsAreNotPreEmpted()
    {
        var options = new FileStorageOptions();

        // The transport limit must leave room above the file cap for the multipart envelope,
        // otherwise a full-size file would be rejected before the validator runs.
        Assert.True(options.MaxRequestBodyBytes > options.MaxFileSizeBytes);
    }
}
