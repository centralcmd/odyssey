using System.Net;
using System.Text;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// Unit coverage for <see cref="ApiProblem"/> / <see cref="ApiProblemExtensions.ReadProblemAsync"/> —
/// the shared RFC 7807 parsing + fallback logic that sits on every client error-display path.
/// </summary>
public class ApiProblemTests
{
    [Fact]
    public void Message_prefers_detail_then_reason_then_status_then_default()
    {
        Assert.Equal("boom", new ApiProblem { Detail = "boom", ReasonFallback = "Bad Request", Status = 400 }.Message);
        Assert.Equal("Bad Request", new ApiProblem { ReasonFallback = "Bad Request", Status = 400 }.Message);
        Assert.Equal("HTTP 400", new ApiProblem { Status = 400 }.Message);
        Assert.Equal("Request failed.", new ApiProblem().Message);
    }
    [Fact]
    public void Message_treats_whitespace_detail_and_reason_as_absent()
    {
        Assert.Equal("Bad Request", new ApiProblem { Detail = "   ", ReasonFallback = "Bad Request", Status = 400 }.Message);
        Assert.Equal("HTTP 400", new ApiProblem { Detail = " ", ReasonFallback = " ", Status = 400 }.Message);
    }
    [Fact]
    public async Task ReadProblemAsync_parses_problem_json_and_defaults_status_and_reason()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new StringContent("""{"detail":"nope","code":"feature_disabled"}""",
                                        Encoding.UTF8, "application/problem+json"),
        };
        var p = await resp.ReadProblemAsync();
        Assert.Equal("nope", p.Detail);
        Assert.Equal("feature_disabled", p.Code);
        Assert.Equal(400, p.Status);                 // defaulted from the response
        Assert.Equal("Bad Request", p.ReasonFallback);
    }
    [Fact]
    public async Task ReadProblemAsync_keeps_explicit_status_from_body()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new StringContent("""{"status":404,"detail":"gone"}""",
                                        Encoding.UTF8, "application/problem+json"),
        };
        var p = await resp.ReadProblemAsync();
        Assert.Equal(404, p.Status);
        Assert.Equal("gone", p.Message);
    }
    [Theory]
    [InlineData("")]            // empty body
    [InlineData("not json")]    // non-JSON
    [InlineData("null")]        // literal null → ReadFromJsonAsync returns null
    public async Task ReadProblemAsync_falls_back_to_status_only_on_unparseable_body(string body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            ReasonPhrase = "Internal Server Error",
            Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
        };
        var p = await resp.ReadProblemAsync();
        Assert.Equal(500, p.Status);
        Assert.Equal("Internal Server Error", p.Message);   // reason fallback, never raw body
    }
}
