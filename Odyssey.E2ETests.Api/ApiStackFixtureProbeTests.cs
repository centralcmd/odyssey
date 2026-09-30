using System.Net;
using System.Text;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// How <see cref="ApiStackFixture"/> reads <c>/healthz</c> (issue #257). Plain unit tests outside
/// <see cref="ApiStackCollection"/>, so they run with no stack.
/// </summary>
public sealed class ApiStackFixtureProbeTests
{
    [Fact]
    public async Task Json_2xx_is_ready()
    {
        var (step, _) = await ApiStackFixture.ClassifyHealth(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json"),
            });

        Assert.Equal(ProbeStep.Ready, step);
    }

    [Fact]
    public async Task A_starting_api_is_retried()
    {
        var (step, _) = await ApiStackFixture.ClassifyHealth(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.Equal(ProbeStep.Retry, step);
    }

    [Fact]
    public async Task Html_means_the_base_url_names_the_client_and_fails_at_once()
    {
        var (step, detail) = await ApiStackFixture.ClassifyHealth(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<!DOCTYPE html>", Encoding.UTF8, "text/html"),
            });

        Assert.Equal(ProbeStep.Fatal, step);
        Assert.Contains("E2E_API_BASE_URL", detail);
    }

    [Fact]
    public void The_api_tier_name_is_recognised()
    {
        Assert.True(TestTierGate.IsRequired(TestTierGate.E2EApi, "e2e,e2e-api"));
        Assert.False(TestTierGate.IsRequired(TestTierGate.E2EApi, "e2e"));
    }
}
