using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.ApiClient.Auth;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The two password-reset calls (issue #405). Both endpoints belong to <c>MapIdentityApi</c>, so what
/// this client owns is the mapping from a status class to an outcome the page can branch on — and
/// that mapping is load-bearing: a <c>400</c> for a spent link and a <c>400</c> for a weak password
/// send the user to two entirely different places.
/// </summary>
public class AuthApiClientPasswordResetTests
{
    private const string Email = "user@example.com";

    [Fact]
    public async Task RequestingAReset_PostsTheAddressToForgotPassword()
    {
        var (provider, handler) = Build(HttpStatusCode.OK, body: null);
        var auth = provider.GetRequiredService<AuthApiClient>();

        var outcome = await auth.RequestPasswordResetAsync(Email);

        Assert.Equal(PasswordResetRequestOutcome.Sent, outcome);
        Assert.Equal("/forgotPassword", handler.LastPath);
        Assert.Equal(Email, ReadProperty(handler.LastBody, "email"));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, PasswordResetRequestOutcome.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, PasswordResetRequestOutcome.Failed)]
    [InlineData(HttpStatusCode.BadRequest, PasswordResetRequestOutcome.Failed)]
    public async Task RequestingAReset_MapsTheStatusToAnOutcome(
        HttpStatusCode status, PasswordResetRequestOutcome expected)
    {
        var (provider, _) = Build(status, body: null);
        var auth = provider.GetRequiredService<AuthApiClient>();

        Assert.Equal(expected, await auth.RequestPasswordResetAsync(Email));
    }

    [Fact]
    public async Task RequestingAReset_TreatsATransportFailureAsAFailure()
    {
        var (provider, _) = Build(HttpStatusCode.OK, body: null, throws: true);
        var auth = provider.GetRequiredService<AuthApiClient>();

        Assert.Equal(PasswordResetRequestOutcome.Failed, await auth.RequestPasswordResetAsync(Email));
    }

    [Fact]
    public async Task Resetting_PostsTheEmailCodeAndNewPasswordToResetPassword()
    {
        var (provider, handler) = Build(HttpStatusCode.OK, body: null);
        var auth = provider.GetRequiredService<AuthApiClient>();

        var (outcome, error) = await auth.ResetPasswordAsync(Email, "the-code", "Renewed987!Passphrase");

        Assert.Equal(PasswordResetOutcome.Success, outcome);
        Assert.Null(error);
        Assert.Equal("/resetPassword", handler.LastPath);
        Assert.Equal(Email, ReadProperty(handler.LastBody, "email"));
        Assert.Equal("the-code", ReadProperty(handler.LastBody, "resetCode"));
        Assert.Equal("Renewed987!Passphrase", ReadProperty(handler.LastBody, "newPassword"));
    }

    [Fact]
    public async Task ASpentLink_IsRecognisedByItsErrorCode_NotByAMessage()
    {
        // "InvalidToken" is Identity's IdentityError.Code, pinned server-side by
        // PasswordResetApiTests. Branching on the human-readable description instead would break on
        // any wording or localisation change.
        var (provider, _) = Build(
            HttpStatusCode.BadRequest,
            """{"title":"One or more validation errors occurred.","status":400,"errors":{"InvalidToken":["Invalid token."]}}""");
        var auth = provider.GetRequiredService<AuthApiClient>();

        var (outcome, error) = await auth.ResetPasswordAsync(Email, "spent", "Renewed987!Passphrase");

        Assert.Equal(PasswordResetOutcome.InvalidToken, outcome);
        Assert.Null(error);
    }

    [Fact]
    public async Task APolicyRejection_ComesBackWithTheServersFirstMessage()
    {
        var (provider, _) = Build(
            HttpStatusCode.BadRequest,
            """{"status":400,"errors":{"PasswordTooShort":["Passwords must be at least 16 characters."],"PasswordRequiresDigit":["Passwords must have at least one digit."]}}""");
        var auth = provider.GetRequiredService<AuthApiClient>();

        var (outcome, error) = await auth.ResetPasswordAsync(Email, "the-code", "short");

        Assert.Equal(PasswordResetOutcome.PasswordRejected, outcome);
        Assert.Equal("Passwords must be at least 16 characters.", error);
    }

    [Fact]
    public async Task AThrottledReset_IsItsOwnOutcome()
    {
        var (provider, _) = Build(HttpStatusCode.TooManyRequests, """{"status":429,"detail":"Too many requests."}""");
        var auth = provider.GetRequiredService<AuthApiClient>();

        var (outcome, _) = await auth.ResetPasswordAsync(Email, "the-code", "Renewed987!Passphrase");

        Assert.Equal(PasswordResetOutcome.RateLimited, outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    [InlineData(HttpStatusCode.BadRequest, "not json at all")]
    [InlineData(HttpStatusCode.BadRequest, """{"status":400}""")]
    public async Task AnUnreadableFailure_FallsBackToFailed(HttpStatusCode status, string? body)
    {
        // Including a 400 the client cannot make sense of: guessing "spent link" would strand a user
        // whose password was merely too weak, and guessing "weak password" would loop them forever on
        // a link that will never work again.
        var (provider, _) = Build(status, body);
        var auth = provider.GetRequiredService<AuthApiClient>();

        var (outcome, error) = await auth.ResetPasswordAsync(Email, "the-code", "Renewed987!Passphrase");

        Assert.Equal(PasswordResetOutcome.Failed, outcome);
        Assert.Null(error);
    }

    [Fact]
    public async Task Resetting_TreatsATransportFailureAsAFailure()
    {
        var (provider, _) = Build(HttpStatusCode.OK, body: null, throws: true);
        var auth = provider.GetRequiredService<AuthApiClient>();

        var (outcome, _) = await auth.ResetPasswordAsync(Email, "the-code", "Renewed987!Passphrase");

        Assert.Equal(PasswordResetOutcome.Failed, outcome);
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

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // The antiforgery handler fetches a token ahead of the POST; answer it so the request
            // under test reaches this handler.
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/antiforgery/token", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"token":"token"}""", Encoding.UTF8, "application/json"),
                };
            }

            LastPath = request.RequestUri.AbsolutePath;
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
