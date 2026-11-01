using Odyssey.Worker.Thumbnails;
using Xunit;

namespace Odyssey.Worker.Tests.Thumbnails;

public sealed class MagickDecoderSelfTestTests
{
    /// <summary>The shipped policy passes its own self-test: every negative probe refused, every control decoded.</summary>
    [Fact]
    public void The_shipped_policy_passes()
    {
        var result = new MagickDecoderSelfTest().Run();

        Assert.True(result.Ok, $"Self-test failed at '{result.FailedProbe}'.");
    }
}
