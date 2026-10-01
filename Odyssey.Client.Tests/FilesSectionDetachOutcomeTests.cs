using System.Net;
using Odyssey.ApiClient;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #287 H3 made detaching an unattached file a <c>404</c> on the account and transaction surfaces
/// (it was a silent <c>204</c>). The shared files section must read that as "already gone" and drop the
/// stale row, not leave it on screen beside a "Delete failed" toast.
/// </summary>
public class FilesSectionDetachOutcomeTests
{
    [Fact]
    public void A404_IsAlreadyDetached()
    {
        var result = ApiResult.Failure(HttpStatusCode.NotFound, new ApiProblem { Detail = "not attached" });

        Assert.True(FilesSectionBase<ExistingAccountFile>.IsAlreadyDetached(result));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Conflict)]
    public void AnyOtherFailure_IsNot(HttpStatusCode status)
    {
        var result = ApiResult.Failure(status, new ApiProblem { Detail = "nope" });

        Assert.False(FilesSectionBase<ExistingAccountFile>.IsAlreadyDetached(result));
    }

    [Fact]
    public void ASuccess_IsNot_SoItStillGetsTheDetachedToast()
    {
        Assert.False(FilesSectionBase<ExistingAccountFile>.IsAlreadyDetached(ApiResult.Success(HttpStatusCode.NoContent)));
    }

    [Fact]
    public void ATransportFailure_IsNot()
    {
        Assert.False(FilesSectionBase<ExistingAccountFile>.IsAlreadyDetached(ApiResult.Failure(new HttpRequestException("offline"))));
    }
}
