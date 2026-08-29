using System.Net;
using System.Net.Http.Json;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// Contract-level checks against the real running API: authentication failure modes, status codes
/// for missing resources, and that seeded data is actually served. Read-only, so safe against the
/// shared seeded database.
/// </summary>
[Collection(ApiStackCollection.Name)]
public class ApiContractTests(ApiStackFixture fixture)
{
    private static readonly DemoUser Admin = DemoUsers.All.First(user => user.Role == "Admin");

    [SkippableFact]
    public async Task Login_with_wrong_password_is_rejected()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        using var client = fixture.CreateAnonymousClient();
        // Carry a valid antiforgery token so the request reaches credential validation (a tokenless
        // POST would be rejected at the antiforgery gate with 400 instead of the 401 under test).
        var response = await fixture.PostWithAntiforgeryAsync(
            client, "/login?useCookies=true", new { email = Admin.Email, password = "not-the-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Tokenless_identity_write_is_rejected_by_antiforgery()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        // Deliberately the tokenless client: CreateAnonymousClient carries the antiforgery pipeline and
        // would clear the gate under test.
        using var client = fixture.CreateTokenlessClient();
        // The Identity write endpoints are antiforgery-guarded like the controllers, so a tokenless
        // POST is rejected at the antiforgery gate (400) before credential validation runs. Valid
        // credentials are used deliberately: the 400 proves the gate fired (a 401/200 would mean the
        // request reached authentication, i.e. the endpoint was left unguarded). This is the
        // regression guard for tagging MapIdentityApi with RequireAntiforgeryToken.
        var response = await client.PostAsJsonAsync(
            "/login?useCookies=true", new { email = Admin.Email, password = Admin.Password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Unknown_resource_returns_404()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var client = await fixture.CreateAuthenticatedClientAsync(Admin.Email, Admin.Password);
        var response = await client.GetAsync("/api/users/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Seeded_users_are_served_as_json()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var client = await fixture.CreateAuthenticatedClientAsync(Admin.Email, Admin.Password);
        var response = await client.GetAsync("/api/users?offset=0&limit=100");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        // The four seeded demo users must be present (loose parse to avoid coupling to the DTO).
        using var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        var items = json!.RootElement.GetProperty("items");
        Assert.True(
            items.GetArrayLength() >= DemoUsers.All.Count,
            $"expected at least {DemoUsers.All.Count} seeded users, got {items.GetArrayLength()}.");
    }

    [SkippableFact]
    public async Task Seeded_photos_and_albums_are_served_as_json()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var client = await fixture.CreateAuthenticatedClientAsync(Admin.Email, Admin.Password);

        // The Photos module (issue #321) seeds a library of photos + albums; both list endpoints serve them.
        var photos = await client.GetAsync("/api/photos?offset=0&limit=100");
        photos.EnsureSuccessStatusCode();
        using var photosJson = await photos.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        Assert.True(
            photosJson!.RootElement.GetProperty("items").GetArrayLength() > 0,
            "expected seeded photos to be served.");

        var albums = await client.GetAsync("/api/albums?offset=0&limit=100");
        albums.EnsureSuccessStatusCode();
        using var albumsJson = await albums.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        Assert.True(
            albumsJson!.RootElement.GetProperty("items").GetArrayLength() > 0,
            "expected seeded albums to be served.");
    }
}
