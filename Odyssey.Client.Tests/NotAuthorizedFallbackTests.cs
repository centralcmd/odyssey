using System.Net;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.ApiClient.Auth;
using Odyssey.Client.Auth;
using Odyssey.Client.Pages.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// What the router renders for a page the caller is not authorized for (issue #278). A session probe with
/// no definitive answer must show the retry panel — a signed-in user's cookie is still valid, and sending
/// them to <c>/login</c> during an outage (or a <c>429</c>) is the bug. A genuine <c>401</c> still redirects.
/// </summary>
public class NotAuthorizedFallbackTests
{
    private const string Route = "/accounts";

    [Fact]
    public async Task AnUnavailableSession_ShowsTheRetryPanel_AndDoesNotNavigate()
    {
        await using var ctx = NewContext(new SessionApi { Info = HttpStatusCode.ServiceUnavailable });
        var before = Navigation(ctx).History.Count;

        var cut = ctx.Render<NotAuthorizedFallback>(p => p.Add(c => c.User, SessionUnavailable.Principal));

        Assert.Contains("Unable to reach Odyssey", cut.Markup);
        Assert.NotNull(cut.Find("[role=alert]"));
        var retry = cut.FindAll("button").Single(b => b.TextContent.Contains("Retry"));
        Assert.False(retry.HasAttribute("disabled"));
        Assert.Equal(before, Navigation(ctx).History.Count);
    }

    [Fact]
    public async Task AGenuineSignOut_StillRedirectsToSignIn()
    {
        await using var ctx = NewContext(new SessionApi { Info = HttpStatusCode.Unauthorized });
        var anonymous = (await ctx.Services.GetRequiredService<AuthenticationStateProvider>()
            .GetAuthenticationStateAsync()).User;

        var cut = ctx.Render<NotAuthorizedFallback>(p => p.Add(c => c.User, anonymous));

        Assert.DoesNotContain("Unable to reach Odyssey", cut.Markup);
        cut.WaitForAssertion(() => Assert.Equal(
            Login.SignInUrlFor(Route.TrimStart('/')), Navigation(ctx).History.First().Uri));
    }

    [Fact]
    public async Task Retry_WhenTheServerAnswers_AnnouncesTheSession()
    {
        var api = new SessionApi { Info = HttpStatusCode.ServiceUnavailable };
        await using var ctx = NewContext(api);
        var provider = ctx.Services.GetRequiredService<AuthenticationStateProvider>();
        var announced = new TaskCompletionSource<AuthenticationState>();
        provider.AuthenticationStateChanged += async task => announced.TrySetResult(await task);
        var cut = ctx.Render<NotAuthorizedFallback>(p => p.Add(c => c.User, SessionUnavailable.Principal));

        api.Info = HttpStatusCode.OK;
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Retry")).ClickAsync(new());

        var state = await announced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Retry_WhileStillDown_SaysSo_AndStaysPut()
    {
        await using var ctx = NewContext(new SessionApi { Info = HttpStatusCode.ServiceUnavailable });
        var before = Navigation(ctx).History.Count;
        var cut = ctx.Render<NotAuthorizedFallback>(p => p.Add(c => c.User, SessionUnavailable.Principal));

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Retry")).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("Still no answer from the server", cut.Markup));
        Assert.Equal(before, Navigation(ctx).History.Count);
    }

    private static BunitNavigationManager Navigation(BunitContext ctx) =>
        (BunitNavigationManager)ctx.Services.GetRequiredService<NavigationManager>();

    private static BunitContext NewContext(SessionApi api)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<AuthenticationStateProvider>(sp => new CookieAuthenticationStateProvider(
            new AuthApiClient(
                new HttpClient(api) { BaseAddress = new Uri("https://api.odyssey.test/") },
                new AntiforgeryTokenStore(sp)),
            [TimeSpan.Zero],
            _ => new TaskCompletionSource().Task));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(Route);
        return ctx;
    }

    private sealed class SessionApi : HttpMessageHandler
    {
        public HttpStatusCode Info { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            return request.RequestUri!.AbsolutePath switch
            {
                "/manage/info" => new HttpResponseMessage(Info),
                "/auth/claims" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """[{"type":"permission","value":"accounts.read"}]""", Encoding.UTF8, "application/json"),
                },
                _ => throw new InvalidOperationException(request.RequestUri.ToString()),
            };
        }
    }
}
