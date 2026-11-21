using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.ApiClient.Auth;
using Odyssey.Testing.Http;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// <c>/resendConfirmationEmail</c> is rate limited (issue #393), so a non-success answer is no longer
/// necessarily a server fault — a <c>429</c> carries the actionable "wait a few minutes" wording. The
/// client has to hand that text back rather than flatten every failure into one generic message.
/// </summary>
public class AuthApiClientResendConfirmationTests
{
    [Fact]
    public async Task ASuccessfulResend_ReportsNoError()
    {
        var (provider, _) = Build(HttpStatusCode.OK, body: null);
        var auth = provider.GetRequiredService<AuthApiClient>();

        var (succeeded, error) = await auth.ResendConfirmationAsync("user@example.com");

        Assert.True(succeeded);
        Assert.Null(error);
    }

    [Fact]
    public async Task AThrottledResend_ReturnsTheProblemDetail()
    {
        const string detail = "Too many requests. Please wait a few minutes before requesting another email.";
        var (provider, _) = Build(HttpStatusCode.TooManyRequests, $$"""{"status":429,"detail":"{{detail}}"}""");
        var auth = provider.GetRequiredService<AuthApiClient>();

        var (succeeded, error) = await auth.ResendConfirmationAsync("user@example.com");

        Assert.False(succeeded);
        Assert.Equal(detail, error);
    }

    [Fact]
    public async Task AFailureWithNoProblemBody_ReportsNoErrorTextForTheCallerToFallBackOn()
    {
        var (provider, _) = Build(HttpStatusCode.InternalServerError, body: null);
        var auth = provider.GetRequiredService<AuthApiClient>();

        var (succeeded, error) = await auth.ResendConfirmationAsync("user@example.com");

        Assert.False(succeeded);
        Assert.Null(error);
    }

    private static (ServiceProvider Provider, RecordingHttpHandler Handler) Build(HttpStatusCode status, string? body)
    {
        var handler = new RecordingHttpHandler(status, body, RecordingHttpHandler.ProblemMediaType) { AnswersAntiforgeryToken = true };
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
}
