using System.Net;
using System.Text;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// The OPML endpoints across the four seeded role users, over real login, cookie and antiforgery (issue #508 AC 7,
/// AC 42). No claim was added: the export follows <c>news-feeds.update</c> and the import <c>news-feeds.create</c>, which
/// Admin and Owner hold and User and Guest do not. Every probe is non-mutating on the shared stack: the GET only
/// reads, the filtered export names a random id, and the import sends no file part, which an authorized caller is
/// answered <c>400 news-opml-file-required</c> before anything is read.
/// </summary>
[Collection(ApiStackCollection.Name)]
public class NewsOpmlPermissionTests(ApiStackFixture fixture)
{
    private static string[] ClaimsForRole(string role) => role switch
    {
        RoleDefinitions.Admin => RolePermissions.AdminClaims,
        RoleDefinitions.Owner => RolePermissions.OwnerClaims,
        RoleDefinitions.User => RolePermissions.UserClaims,
        RoleDefinitions.Guest => RolePermissions.GuestClaims,
        _ => throw new InvalidOperationException($"Unknown demo role '{role}'."),
    };

    [SkippableFact]
    public async Task Each_role_reaches_the_opml_endpoints_exactly_per_its_claims()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        foreach (var user in DemoUsers.All)
        {
            var client = await fixture.CreateAuthenticatedClientAsync(user.Email, user.Password);
            var claims = ClaimsForRole(user.Role);
            var canExport = claims.Contains(PermissionClaims.NewsFeedsUpdate);
            var canImport = claims.Contains(PermissionClaims.NewsFeedsCreate);

            var export = await client.GetAsync("/api/news-feeds/opml");
            Assert.True(export.StatusCode == (canExport ? HttpStatusCode.OK : HttpStatusCode.Forbidden),
                $"{user.Role} GET /api/news-feeds/opml: got {(int)export.StatusCode}.");
            if (canExport)
            {
                Assert.Equal("text/x-opml", export.Content.Headers.ContentType?.MediaType);
                Assert.True(export.Headers.Contains("X-Odyssey-Export-Rows"));
            }

            var filtered = await fixture.PostContentWithAntiforgeryAsync(client, "/api/news-feeds/opml/export",
                new StringContent($"{{\"feedIds\":[\"{Guid.NewGuid()}\"]}}", Encoding.UTF8, "application/json"));
            Assert.True(filtered.StatusCode == (canExport ? HttpStatusCode.OK : HttpStatusCode.Forbidden),
                $"{user.Role} POST /api/news-feeds/opml/export: got {(int)filtered.StatusCode}.");

            var noFile = new MultipartFormDataContent { { new StringContent("x"), "other" } };
            var import = await fixture.PostContentWithAntiforgeryAsync(client, "/api/news-feeds/opml", noFile);
            Assert.True(import.StatusCode == (canImport ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden),
                $"{user.Role} POST /api/news-feeds/opml: got {(int)import.StatusCode}.");
            if (canImport)
            {
                Assert.Contains("news-opml-file-required", await import.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
        }
    }

    /// <summary>The premise the matrix above reads: the role mapping, with no stack needed.</summary>
    [Fact]
    public void Guest_and_User_hold_neither_opml_claim()
    {
        foreach (var role in new[] { RoleDefinitions.User, RoleDefinitions.Guest })
        {
            Assert.DoesNotContain(PermissionClaims.NewsFeedsUpdate, ClaimsForRole(role));
            Assert.DoesNotContain(PermissionClaims.NewsFeedsCreate, ClaimsForRole(role));
        }

        foreach (var role in new[] { RoleDefinitions.Admin, RoleDefinitions.Owner })
        {
            Assert.Contains(PermissionClaims.NewsFeedsUpdate, ClaimsForRole(role));
            Assert.Contains(PermissionClaims.NewsFeedsCreate, ClaimsForRole(role));
        }
    }
}
