using System.Net;
using Odyssey.ApiClient.Resources;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The profile-picture members of <see cref="ProfileApiClient"/> (issue #94 §7), at the tier
/// <c>CLAUDE.md</c>'s table names for typed clients rather than transitively through the heavier
/// HTTP tier.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IProfileApiClient.ImageUrl"/> is the reason this file exists: it does real URL-building
/// and escaping work, and it is the <b>only</b> place the <c>?v=</c> cache key is composed. That key
/// is what makes the browser re-request after a replace — without it the <c>src</c> string is
/// unchanged, Blazor emits no DOM update, and the read path's <c>no-cache</c>/<c>ETag</c> machinery
/// never comes into play, so a replaced picture simply stays on screen.
/// </para>
/// <para>
/// The two write members are asserted for their <b>route shape</b>: both are self-scoped and must
/// carry no user id in the path. That is the whole IDOR mitigation, and it is structural — a refactor
/// that introduced an id segment would compile and pass every behavioural test.
/// </para>
/// </remarks>
public class ProfileApiClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;

        public string? Body { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(Status);
            if (Body is not null)
            {
                response.Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }

    private static (IProfileApiClient Client, RecordingHandler Handler) Create(string baseAddress = "http://localhost/")
    {
        var handler = new RecordingHandler();
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri(baseAddress) });
        return (new ProfileApiClient(api), handler);
    }

    // ── The write routes are self-scoped (issue #94 §10.1) ────────────────────────────────────────

    [Fact]
    public async Task Uploading_posts_to_the_self_scoped_route_with_no_user_id()
    {
        var (client, handler) = Create();
        handler.Status = HttpStatusCode.OK;
        handler.Body = """{"imageVersion":"7c1f9b30-0000-0000-0000-00000000a20e"}""";

        var bytes = new byte[] { 1, 2, 3 };
        await client.UploadImageAsync(new ApiUpload("picture.jpg", "image/jpeg", bytes.Length, () => new MemoryStream(bytes)));

        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/profile/image", request.RequestUri!.AbsolutePath);

        // No id anywhere in the path: there is nothing to tamper with, by construction.
        Assert.DoesNotContain("users", request.RequestUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/api/profile/image", request.RequestUri.PathAndQuery);
    }

    [Fact]
    public async Task Uploading_returns_the_new_version_token()
    {
        var (client, handler) = Create();
        handler.Status = HttpStatusCode.OK;
        handler.Body = """{"imageVersion":"7c1f9b30-0000-0000-0000-00000000a20e"}""";

        var bytes = new byte[] { 1, 2, 3 };
        var result = await client.UploadImageAsync(
            new ApiUpload("picture.jpg", "image/jpeg", bytes.Length, () => new MemoryStream(bytes)));

        // The caller needs this to re-key the image URL — a 204 would have been unusable.
        Assert.True(result.IsSuccess);
        Assert.Equal(Guid.Parse("7c1f9b30-0000-0000-0000-00000000a20e"), result.Value!.ImageVersion);
    }

    [Fact]
    public async Task Deleting_hits_the_same_self_scoped_route()
    {
        var (client, handler) = Create();

        await client.DeleteImageAsync();

        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/profile/image", request.RequestUri!.PathAndQuery);
    }

    // ── The read URL is keyed, and it is the only place that key is composed ──────────────────────

    [Fact]
    public void The_image_url_carries_the_version_as_the_cache_key()
    {
        var (client, _) = Create();
        var version = Guid.Parse("7c1f9b30-0000-0000-0000-00000000a20e");

        var url = client.ImageUrl("user-123", version);

        Assert.Equal($"http://localhost/api/profile-images/user-123?v={version}", url);
    }

    /// <summary>
    /// A different token must produce a different string. This is the whole mechanism behind a replace
    /// appearing at all, so it is asserted directly rather than inferred from the format above.
    /// </summary>
    [Fact]
    public void A_new_version_produces_a_different_url()
    {
        var (client, _) = Create();

        var first = client.ImageUrl("user-123", Guid.NewGuid());
        var second = client.ImageUrl("user-123", Guid.NewGuid());

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Identity ids are opaque strings, not GUIDs, so one carrying a character with meaning in a URL
    /// must not be able to alter the path or smuggle a second query parameter.
    /// </summary>
    /// <remarks>
    /// Asserted on the <b>parsed</b> URL rather than on the returned string, because
    /// <see cref="Uri.ToString"/> canonicalises — it decodes escapes that are not required in the
    /// position they occupy, so a space comes back as a space. That is display formatting, not a
    /// failure to escape: what matters is that the id stays <i>one path segment</i> and cannot reach
    /// the query, which is what these assertions check.
    /// </remarks>
    [Theory]
    [InlineData("user with spaces")]
    [InlineData("user/../admin")]
    [InlineData("user?v=other")]
    [InlineData("user&x=1")]
    [InlineData("user#frag")]
    [InlineData("üser")]
    public void A_user_id_cannot_escape_its_path_segment(string userId)
    {
        var (client, _) = Create();
        var version = Guid.Parse("7c1f9b30-0000-0000-0000-00000000a20e");

        var uri = new Uri(client.ImageUrl(userId, version));

        // One segment after the resource, and it decodes back to exactly what went in — so a '/' did
        // not split it and a '#' did not truncate it.
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["api", "profile-images", userId], [.. segments.Select(Uri.UnescapeDataString)]);

        // Exactly one query parameter, and it is the version — so a '?' or '&' smuggled nothing in.
        var query = Assert.Single(uri.Query.TrimStart('?').Split('&'));
        Assert.Equal($"v={version}", query);
    }

    /// <summary>
    /// Resolved against the configured base the same way the typed clients form request URLs, so it
    /// works behind the nginx <c>/api/</c> proxy and against an absolute API host alike. A hand-built
    /// URL at a call site would get exactly this wrong.
    /// </summary>
    [Theory]
    [InlineData("http://localhost/", "http://localhost/api/profile-images/user-123")]
    [InlineData("https://api.example.test/", "https://api.example.test/api/profile-images/user-123")]
    [InlineData("https://example.test/gateway/", "https://example.test/gateway/api/profile-images/user-123")]
    public void The_image_url_resolves_against_the_configured_base(string baseAddress, string expectedPrefix)
    {
        var (client, _) = Create(baseAddress);

        var url = client.ImageUrl("user-123", Guid.Parse("7c1f9b30-0000-0000-0000-00000000a20e"));

        Assert.StartsWith(expectedPrefix, url, StringComparison.Ordinal);
    }
}
