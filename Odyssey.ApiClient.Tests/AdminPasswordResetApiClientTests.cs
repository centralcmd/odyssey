using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.ApiClient.Auth;
using Odyssey.ApiClient.Resources;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The two calls issue #406 adds to the library: the admin's "send password reset", and the
/// change-password call repointed off Identity's <c>manage/info</c> onto Odyssey's first-party endpoint.
/// What both own is the mapping from a wire response to something a page can branch on — and here that
/// mapping carries real weight, because "sent" and "applied but not delivered" arrive as the same
/// <c>200</c> and mean different things to the admin.
/// </summary>
public class AdminPasswordResetApiClientTests
{
    private const string UserId = "user-1";

    [Fact]
    public async Task SendingAReset_PostsAnEmptyBodyToThePasswordResetRoute()
    {
        var (provider, handler) = Build(HttpStatusCode.OK, """{"emailDelivered":true}""");
        var users = provider.GetRequiredService<IUsersApiClient>();

        var result = await users.SendPasswordResetAsync(UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal("/api/users/user-1/password-reset", handler.LastPath);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Null(handler.LastBody);
    }

    [Fact]
    public async Task SendingAReset_EscapesTheIdIntoTheRoute()
    {
        // Identity ids are GUIDs today, but the route parameter is a free string server-side, so an id
        // carrying a slash would otherwise silently address a different endpoint.
        var (provider, handler) = Build(HttpStatusCode.OK, """{"emailDelivered":true}""");
        var users = provider.GetRequiredService<IUsersApiClient>();

        await users.SendPasswordResetAsync("odd/id?x=1");

        Assert.Equal("/api/users/odd%2Fid%3Fx%3D1/password-reset", handler.LastPath);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task ADeliveredFlag_SurfacesAsATypedValueTheCallerCanBranchOn(string json, bool expected)
    {
        // false means the reset WAS applied and the mail was not — a warning the admin must act on, not
        // an error to retry. Collapsing the two would be the exact false success the endpoint's body
        // exists to prevent.
        var (provider, _) = Build(HttpStatusCode.OK, $$"""{"emailDelivered":{{json}}}""");
        var users = provider.GetRequiredService<IUsersApiClient>();

        var result = await users.SendPasswordResetAsync(UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value!.EmailDelivered);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "This account has no confirmed email address, so a reset link cannot be sent.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Too many reset emails have been sent to this address recently.")]
    [InlineData(HttpStatusCode.NotFound, "User ID user-1 was not found.")]
    public async Task AFailure_KeepsBothTheStatusAndTheServersWording(HttpStatusCode status, string detail)
    {
        // The page branches on the status for tone and shows the detail as the message: 422 and 429 mean
        // nothing was mutated, and the 429 wording is what tells "too many to this address" apart from
        // "too many from this account".
        var (provider, _) = Build(status, $$"""{"status":{{(int)status}},"detail":"{{detail}}"}""");
        var users = provider.GetRequiredService<IUsersApiClient>();

        var result = await users.SendPasswordResetAsync(UserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(status, result.Status);
        Assert.Equal(detail, result.Error);
    }

    [Fact]
    public async Task ChangingAPassword_PostsToTheFirstPartyEndpoint_NotManageInfo()
    {
        // The repoint is the point (issue #406 §5.7): manage/info changes the password AND the email, so
        // it could not be the endpoint left reachable by a session blocked pending a forced change.
        var (provider, handler) = Build(HttpStatusCode.NoContent, body: null);
        var auth = provider.GetRequiredService<AuthApiClient>();

        var result = await auth.ChangePasswordAsync("Current123!Passphrase", "Renewed987!Passphrase");

        Assert.True(result.IsSuccess);
        Assert.Equal("/api/account/password", handler.LastPath);
        Assert.Equal("Current123!Passphrase", ReadProperty(handler.LastBody, "currentPassword"));
        Assert.Equal("Renewed987!Passphrase", ReadProperty(handler.LastBody, "newPassword"));
    }

    [Fact]
    public async Task AFailedChange_ExposesTheProblemDetail_NotTheRawJsonBody()
    {
        // The old method returned manage/info's plain-string body verbatim. The new endpoint answers
        // RFC 7807, so a caller that rendered the old Error would now be putting a JSON document on the
        // page — which is why the method returns ApiResult rather than (bool, string?).
        var (provider, _) = Build(
            HttpStatusCode.BadRequest,
            """{"status":400,"title":"Bad Request","detail":"The current password is incorrect."}""");
        var auth = provider.GetRequiredService<AuthApiClient>();

        var result = await auth.ChangePasswordAsync("Wrong123!Passphrase", "Renewed987!Passphrase");

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("The current password is incorrect.", result.Error);
        Assert.DoesNotContain("{", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALockedOutChange_KeepsIts423()
    {
        var (provider, _) = Build(
            HttpStatusCode.Locked,
            """{"status":423,"detail":"This account is temporarily locked after too many failed attempts."}""");
        var auth = provider.GetRequiredService<AuthApiClient>();

        var result = await auth.ChangePasswordAsync("Wrong123!Passphrase", "Renewed987!Passphrase");

        Assert.Equal(HttpStatusCode.Locked, result.Status);
        Assert.Contains("temporarily locked", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATransportFailure_IsAFailureRatherThanAnException()
    {
        var (provider, _) = Build(HttpStatusCode.NoContent, body: null, throws: true);
        var auth = provider.GetRequiredService<AuthApiClient>();

        var result = await auth.ChangePasswordAsync("Current123!Passphrase", "Renewed987!Passphrase");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    private static string? ReadProperty(string? json, string property)
    {
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json!);
        return document.RootElement.GetProperty(property).GetString();
    }

    private static (ServiceProvider Provider, StubHandler Handler) Build(
        HttpStatusCode status, string? body, bool throws = false)
    {
        var handler = new StubHandler(status, body, throws);
        var services = new ServiceCollection();
        services.AddOdysseyApiClient();
        services.AddScoped(sp =>
        {
            var antiforgery = sp.GetRequiredService<AntiforgeryHandler>();
            antiforgery.InnerHandler = handler;
            return new HttpClient(antiforgery) { BaseAddress = new Uri("http://localhost/") };
        });
        return (services.BuildServiceProvider(), handler);
    }

    private sealed class StubHandler(HttpStatusCode status, string? body, bool throws) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // The antiforgery handler fetches a token ahead of the POST; answer it so the request under
            // test reaches this handler.
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/antiforgery/token", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"token":"token"}""", Encoding.UTF8, "application/json"),
                };
            }

            LastPath = request.RequestUri.AbsolutePath;
            LastMethod = request.Method;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            if (throws)
            {
                throw new HttpRequestException("the API is unreachable");
            }

            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/problem+json");
            }

            return response;
        }
    }
}
