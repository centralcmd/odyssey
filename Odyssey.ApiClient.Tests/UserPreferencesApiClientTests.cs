using System.Net;
using Odyssey.ApiClient.Resources;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The preference store's <c>404</c> means "nothing saved for this key yet", which is normal for a
/// fresh user. Both callers depend on telling that apart from a real failure: confusing them would
/// cache defaults over a user's real saved theme and currency.
/// </summary>
public class UserPreferencesApiClientTests
{
    private sealed record Payload(string PreferencesJson);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.NoContent);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private static (UserPreferencesApiClient Client, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        return (new UserPreferencesApiClient(new OdysseyApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") })), handler);
    }

    [Fact]
    public async Task An_unset_key_reports_NotFound_rather_than_a_generic_failure()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.NotFound);

        var result = await client.GetAsync<Payload>("accounts-page");

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.NotFound, result.Status);
    }

    /// <summary>A real failure must stay distinguishable from the unset case.</summary>
    [Fact]
    public async Task A_server_error_is_not_reported_as_NotFound()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await client.GetAsync<Payload>("accounts-page");

        Assert.False(result.IsSuccess);
        Assert.NotEqual(HttpStatusCode.NotFound, result.Status);
    }

    [Fact]
    public async Task GetAsync_deserializes_the_stored_payload()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"preferencesJson":"{\"darkModeEnabled\":true}"}""",
                                        System.Text.Encoding.UTF8, "application/json"),
        };

        var result = await client.GetAsync<Payload>("theme");

        Assert.True(result.IsSuccess);
        Assert.Contains("darkModeEnabled", result.Value!.PreferencesJson);
    }

    // Page-state keys come from route names, so a key with a slash or space must not break out of
    // the resource path.
    [Theory]
    [InlineData("accounts-page", "/api/user-preferences/accounts-page")]
    [InlineData("odd key", "/api/user-preferences/odd%20key")]
    [InlineData("a/b", "/api/user-preferences/a%2Fb")]
    public async Task Keys_are_escaped_into_a_single_path_segment(string key, string expectedPath)
    {
        var (client, handler) = Create();

        await client.PutAsync(key, new Payload("{}"));

        Assert.Equal(expectedPath, handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);
    }
}
