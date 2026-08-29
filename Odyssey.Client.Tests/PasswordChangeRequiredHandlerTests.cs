using System.Net;
using System.Text;
using Odyssey.ApiClient;
using Odyssey.Client.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="PasswordChangeRequiredHandler"/> is what surfaces a <em>mid-session</em> forced reset
/// (issue #406 §7): <c>MainLayout</c>'s gate reads the profile once per full page load, so an admin who
/// triggers a reset while the user is working would otherwise leave them watching calls fail with no
/// explanation.
/// <para>
/// The body-buffering test below is the one that matters most. This handler has to read the body of
/// every <c>403</c> to tell "you owe a password change" from an ordinary permission denial — and
/// <c>OdysseyApi</c> reads that same body afterwards. Getting it wrong doesn't break this feature; it
/// silently degrades every <c>403</c> in the app to a reason-phrase fallback.
/// </para>
/// </summary>
public class PasswordChangeRequiredHandlerTests
{
    private const string Base = "https://api.odyssey.test/";

    private const string GateProblem = """
        {"type":"https://odyssey.local/errors/password-change-required","title":"Forbidden","status":403,
         "detail":"A password change is required before this account can be used.",
         "code":"password_change_required"}
        """;

    private const string PermissionProblem = """
        {"title":"Forbidden","status":403,"detail":"You do not have permission to read accounts."}
        """;

    [Fact]
    public async Task TheGate403_raisesTheNotifier()
    {
        var notifier = new PasswordChangeRequiredNotifier();
        var raised = 0;
        notifier.PasswordChangeRequired += () => raised++;
        using var client = ClientFor(notifier, HttpStatusCode.Forbidden, GateProblem);

        await client.GetAsync("api/accounts");

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// It has to fire for any client, not just whatever the page happened to call — a background writer
    /// like PageStateService's debounced save is as likely as anything to meet the status first, and it
    /// has no UI of its own to react.
    /// </summary>
    [Fact]
    public async Task TheGate403_fromAnyRequest_raisesTheNotifier()
    {
        var notifier = new PasswordChangeRequiredNotifier();
        var raised = false;
        notifier.PasswordChangeRequired += () => raised = true;
        using var client = ClientFor(notifier, HttpStatusCode.Forbidden, GateProblem);

        await client.PutAsync("api/user-preferences/accounts-page", content: null);

        Assert.True(raised);
    }

    [Fact]
    public async Task AnOrdinaryPermissionDenied403_doesNotRaiseIt()
    {
        // Common in this app — a Guest reaching an endpoint the client didn't hide — so keying off the
        // status alone would bounce them to a password form they don't need.
        var notifier = new PasswordChangeRequiredNotifier();
        var raised = false;
        notifier.PasswordChangeRequired += () => raised = true;
        using var client = ClientFor(notifier, HttpStatusCode.Forbidden, PermissionProblem);

        await client.GetAsync("api/accounts");

        Assert.False(raised);
    }

    /// <summary>
    /// The non-regression this handler's whole design is arranged around: the caller reads the same body
    /// afterwards, so a naive read here would exhaust the stream and drop every 403's real
    /// <c>detail</c> app-wide to the reason-phrase fallback.
    /// </summary>
    [Fact]
    public async Task AnUnrelated403_stillCarriesItsOriginalDetail_toTheCaller()
    {
        var notifier = new PasswordChangeRequiredNotifier();
        using var client = ClientFor(notifier, HttpStatusCode.Forbidden, PermissionProblem);

        var response = await client.GetAsync("api/accounts");
        var problem = await response.ReadProblemAsync();

        Assert.Equal("You do not have permission to read accounts.", problem.Detail);
        Assert.Equal("You do not have permission to read accounts.", problem.Message);
    }

    [Fact]
    public async Task TheGate403_alsoStillCarriesItsBody_toTheCaller()
    {
        var notifier = new PasswordChangeRequiredNotifier();
        using var client = ClientFor(notifier, HttpStatusCode.Forbidden, GateProblem);

        var response = await client.GetAsync("api/accounts");
        var problem = await response.ReadProblemAsync();

        Assert.Equal(PasswordChangeRequiredHandler.ProblemCode, problem.Code);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.UnavailableForLegalReasons)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnyOtherStatus_isNeverRead(HttpStatusCode status)
    {
        // Success responses include binary file and photo downloads, which are streamed with
        // ResponseHeadersRead precisely so they are never fully materialised. Speculatively buffering
        // those would pull whole files into memory for nothing, so the status check comes first — and
        // reading a stream nobody re-buffers would break the download outright.
        var notifier = new PasswordChangeRequiredNotifier();
        var raised = false;
        notifier.PasswordChangeRequired += () => raised = true;
        using var client = ClientFor(notifier, status, GateProblem);

        var response = await client.GetAsync("api/accounts", HttpCompletionOption.ResponseHeadersRead);

        Assert.False(raised);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(GateProblem, await response.Content.ReadAsStringAsync());
    }

    private static HttpClient ClientFor(
        PasswordChangeRequiredNotifier notifier, HttpStatusCode status, string body) =>
        new(new PasswordChangeRequiredHandler(notifier) { InnerHandler = new StubHandler(status, body) })
        {
            BaseAddress = new Uri(Base),
        };

    /// <summary>
    /// Answers over a <b>read-once</b> stream, like a real network response and unlike the buffered
    /// content a naive stub would hand back. That is what makes the buffering assertions mean something:
    /// without <c>LoadIntoBufferAsync</c> the handler's own parse consumes the stream and the caller's
    /// read comes back empty.
    /// </summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/problem+json");
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }
}
