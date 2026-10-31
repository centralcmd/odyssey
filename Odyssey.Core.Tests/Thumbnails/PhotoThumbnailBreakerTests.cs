using Odyssey.Core.Journal.Thumbnails;
using Xunit;

namespace Odyssey.Core.Tests.Thumbnails;

/// <summary>The breaker rule shared by the job and the stats (§3.6): 3 incidents within an hour stop claiming until an
/// hour after the latest of them.</summary>
public class PhotoThumbnailBreakerTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Two_incidents_never_trip_it() =>
        Assert.Null(PhotoThumbnailBreaker.ResumesAt([Now.AddMinutes(-5), Now.AddMinutes(-1)], Now));

    [Fact]
    public void Three_within_an_hour_trip_it_until_an_hour_after_the_latest()
    {
        var resumes = PhotoThumbnailBreaker.ResumesAt([Now.AddMinutes(-50), Now.AddMinutes(-20), Now.AddMinutes(-10)], Now);

        Assert.Equal(Now.AddMinutes(50), resumes);
    }

    [Fact]
    public void Three_spread_over_more_than_an_hour_do_not_trip_it() =>
        Assert.Null(PhotoThumbnailBreaker.ResumesAt([Now.AddMinutes(-90), Now.AddMinutes(-20), Now.AddMinutes(-10)], Now));

    [Fact]
    public void A_cluster_older_than_an_hour_after_its_latest_has_expired() =>
        Assert.Null(PhotoThumbnailBreaker.ResumesAt([Now.AddMinutes(-100), Now.AddMinutes(-80), Now.AddMinutes(-61)], Now));

    [Fact]
    public void The_latest_qualifying_cluster_decides_the_resume_time()
    {
        var resumes = PhotoThumbnailBreaker.ResumesAt(
            [Now.AddMinutes(-55), Now.AddMinutes(-40), Now.AddMinutes(-30), Now.AddMinutes(-5)], Now);

        Assert.Equal(Now.AddMinutes(55), resumes);
    }
}
