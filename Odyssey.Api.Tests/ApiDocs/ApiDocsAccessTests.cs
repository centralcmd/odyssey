using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Api.ApiDocs;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Testing.Logging;
using Xunit;

namespace Odyssey.Api.Tests.ApiDocs;

/// <summary>
/// Who may read the API documentation, and what the page is served with (issue #569 §7.1, §7.3).
/// <c>ApiDocs:RequireLogin</c> defaults to a login in every environment; when off, the routes are anonymous. Either
/// way the global fallback policy is untouched, and the Scalar page carries its own strict CSP.
/// </summary>
public partial class ApiDocsAccessTests
{
    private static readonly string[] DocsPaths = ["/openapi/v1.json", "/scalar/v1"];

    public static TheoryData<string?> LoginRequiredValues => new() { null, "true", "TRUE", "", "   " };

    [Theory]
    [MemberData(nameof(LoginRequiredValues))]
    public async Task LoginRequired_AnonymousCallerIsRefused(string? requireLogin)
    {
        await using var factory = DocsFactory(requireLogin);
        using var client = Anonymous(factory);

        foreach (var path in DocsPaths)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        }
    }

    public static TheoryData<string> Roles => new() { "Admin", "Owner", "User", "Guest" };

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task LoginRequired_ASignedInUserOfEveryRoleReadsTheDocs(string role)
    {
        var claims = role switch
        {
            "Admin" => RolePermissions.AdminClaims,
            "Owner" => RolePermissions.OwnerClaims,
            "User" => RolePermissions.UserClaims,
            _ => RolePermissions.GuestClaims,
        };
        await using var factory = new OdysseyApiFactory(
            permissions: claims, configuration: new Dictionary<string, string?> { [ApiDocsConfiguration.EnabledKey] = "true" });
        using var client = factory.CreateClient();

        foreach (var path in DocsPaths)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task LoginNotRequired_AnonymousCallerReadsTheDocs()
    {
        await using var factory = DocsFactory("false");
        using var client = Anonymous(factory);

        foreach (var path in DocsPaths)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task EitherWay_TheFallbackPolicyStillRefusesAnAnonymousCaller(string? requireLogin)
    {
        // The docs' access rule is stated on their own endpoints; it must not have loosened the global one.
        await using var factory = DocsFactory(requireLogin);
        using var client = Anonymous(factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/accounts")).StatusCode);
    }

    [Fact]
    public async Task ThePage_LoadsItsScriptsAndItsDocument()
    {
        await using var factory = DocsFactory("false");
        using var client = Anonymous(factory);

        var page = await client.GetAsync("/scalar/v1");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var pageUri = new Uri(client.BaseAddress!, "/scalar/v1");

        var scripts = ScriptSource().Matches(html).Select(match => match.Groups["src"].Value).ToList();
        Assert.NotEmpty(scripts);
        foreach (var script in scripts)
        {
            Assert.False(Uri.TryCreate(script, UriKind.Absolute, out _), $"The page loads {script} by absolute URL.");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri(pageUri, script))).StatusCode);
        }

        // The document URL as the page's own loader resolves it: the source URL against the page path with the
        // "/scalar/v1" suffix removed. Relative, so behind nginx's /api/ prefix it stays under /api/ (AC 13).
        var source = Assert.Single(DocumentSource().Matches(html)).Groups["url"].Value;
        Assert.Equal("openapi/v1.json", source);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/" + source)).StatusCode);
    }

    [Theory]
    [InlineData("/scalar/v1/", "../v1")]
    [InlineData("/scalar/v1/?x=1", "../v1?x=1")]
    public async Task TheTrailingSlashPage_RedirectsRelatively(string path, string location)
    {
        // Scalar's script tags are relative, so /scalar/v1/ would load them from /scalar/v1/ and never render. The
        // redirect is relative too, so it keeps a proxy prefix the API never sees (/api/scalar/v1/ → /api/scalar/v1).
        await using var factory = DocsFactory("false");
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsOverrideHeader, TestAuthHandler.AnonymousValue);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task LoginRequired_TheBundledAssetsAreRefusedToo()
    {
        await using var factory = DocsFactory(null);
        using var client = Anonymous(factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/scalar/scalar.js")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/scalar/scalar.aspnetcore.js")).StatusCode);
    }

    [Fact]
    public async Task ThePage_CarriesTheStrictHeadersWithAFreshNonce()
    {
        await using var factory = DocsFactory("false");
        using var client = Anonymous(factory);

        var nonces = new List<string>();
        for (var request = 0; request < 2; request++)
        {
            var response = await client.GetAsync("/scalar/v1");
            var html = await response.Content.ReadAsStringAsync();

            var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
            var nonce = Assert.Single(CspNonce().Matches(csp)).Groups["nonce"].Value;
            Assert.Equal(
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    ApiDocsHeaders.ContentSecurityPolicyFormat, $"'self' 'nonce-{nonce}'"),
                csp);
            Assert.All(ScriptTag().Matches(html), tag => Assert.Contains($"nonce=\"{nonce}\"", tag.Value, StringComparison.Ordinal));
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            nonces.Add(nonce);
        }

        Assert.NotEqual(nonces[0], nonces[1]);
    }

    [Fact]
    public async Task TheDocumentAndTheAssets_GetNosniffButNoPagePolicy()
    {
        await using var factory = DocsFactory("false");
        using var client = Anonymous(factory);

        var document = await client.GetAsync("/openapi/v1.json");

        Assert.Equal("nosniff", Assert.Single(document.Headers.GetValues("X-Content-Type-Options")));
        Assert.False(document.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public void AMissingNonce_StillSendsAPolicy_WithNoInlineScriptAllowed()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Response.ContentType = "text/html; charset=utf-8";
        var logger = new RecordingLogger();

        ApiDocsHeaders.Apply(context, logger);

        var csp = context.Response.Headers.ContentSecurityPolicy.ToString();
        Assert.Contains("script-src 'self';", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("nonce-", csp, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task LoginRequired_ACallerHeldByThePasswordGate_IsHeldHereToo()
    {
        await using var factory = DocsFactory(null, RolePermissions.AdminClaims);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            db.Users.Add(new ApplicationUser
            {
                Id = TestAuthHandler.DefaultActorUserId,
                UserName = "gated@example.com",
                Email = "gated@example.com",
                MustChangePassword = true,
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        // The reference: an Admin holds accounts.read, so this 403 is the gate's, not a missing claim.
        var reference = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, reference.StatusCode);
        var referenceBody = await reference.Content.ReadAsStringAsync();

        foreach (var path in DocsPaths)
        {
            var response = await client.GetAsync(path);
            Assert.Equal(reference.StatusCode, response.StatusCode);
            Assert.Equal(referenceBody, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task TheRetiredKey_IsReportedOnce_AndNotHonoured()
    {
        var logs = new RecordingLoggerProvider();
        await using var factory = new OdysseyApiFactory(
            configuration: new Dictionary<string, string?> { [ApiDocsConfiguration.RetiredEnabledKey] = "true" },
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();

        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        var warning = Assert.Single(logs.ForCategory(nameof(ApiDocsConfiguration)), entry => entry.Level == LogLevel.Warning);
        Assert.Contains(ApiDocsConfiguration.EnabledKey, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedRequireLogin_StopsTheHost_WithoutEchoingTheValue()
    {
        using var factory = DocsFactory("banana");

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(ApiDocsConfiguration.RequireLoginKey, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("banana", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("true", true)]
    [InlineData(" False ", false)]
    [InlineData("false", false)]
    public void ReadRequireLogin_ResolvesEveryAcceptedSpelling(string? value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ApiDocsConfiguration.RequireLoginKey] = value })
            .Build();

        Assert.Equal(expected, ApiDocsConfiguration.ReadRequireLogin(configuration));
    }

    /// <summary>
    /// A host with the docs on. Its default caller is anonymous unless <paramref name="permissions"/> is given (the
    /// shared factory's convention); tests that need a signed-in caller pass a claim set.
    /// </summary>
    private static OdysseyApiFactory DocsFactory(string? requireLogin, IReadOnlyCollection<string>? permissions = null)
    {
        var settings = new Dictionary<string, string?> { [ApiDocsConfiguration.EnabledKey] = "true" };
        if (requireLogin is not null)
        {
            settings[ApiDocsConfiguration.RequireLoginKey] = requireLogin;
        }

        return new OdysseyApiFactory(permissions: permissions, configuration: settings);
    }

    private static HttpClient Anonymous(OdysseyApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsOverrideHeader, TestAuthHandler.AnonymousValue);
        return client;
    }

    [GeneratedRegex("<script[^>]*\\bsrc=\"(?<src>[^\"]+)\"")]
    private static partial Regex ScriptSource();

    [GeneratedRegex("\"sources\":\\[\\{\"title\":\"v1\",\"url\":\"(?<url>[^\"]+)\"")]
    private static partial Regex DocumentSource();

    [GeneratedRegex("<script\\b[^>]*>")]
    private static partial Regex ScriptTag();

    [GeneratedRegex("'nonce-(?<nonce>[^']+)'")]
    private static partial Regex CspNonce();
}
