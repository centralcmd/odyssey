using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient.Auth;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Auth;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #256: the page-level structure a screen reader navigates by — one <c>&lt;h1&gt;</c> per page
/// that <c>FocusOnNavigate</c> (App.razor, <c>Selector="h1"</c>) can actually focus, a
/// <c>&lt;title&gt;</c> on every route, autocomplete tokens on the credential fields, and an accessible
/// name on a header-less modal.
/// </summary>
public class PageStructureAccessibilityTests
{
    static PageStructureAccessibilityTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    // ── §1 — the page heading ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PageHeader_TitleIsTheOneH1_AndAProgrammaticFocusTarget()
    {
        await using var ctx = NewContext();

        var header = ctx.Render<PageHeader>(p => p.Add(h => h.Title, "Accounts"));

        var h1 = Assert.Single(header.FindAll("h1"));
        Assert.Equal("Accounts", h1.TextContent.Trim());
        // FocusOnNavigate calls element.focus(), which a heading only accepts with a tabindex; -1 keeps
        // it out of the tab order.
        Assert.Equal("-1", h1.GetAttribute("tabindex"));
        // The look is unchanged: still the theme's h2 typography, only the element moved up a level.
        Assert.Contains("mud-typography-h2", h1.ClassList);
    }

    [Fact]
    public void PageHeader_TitleHasNoFocusRing_OnProgrammaticFocus()
    {
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "PageHeader.razor.css"));

        Assert.Contains(".ph-titles ::deep .ph-title:focus { outline: none; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthLayout_WrapsTheBodyInTheMainLandmark()
    {
        var layout = File.ReadAllText(Path.Combine(ClientSource.Root, "Layout", "AuthLayout.razor"));

        Assert.Matches(new Regex(@"<main\s+id=""main-content""[^>]*>\s*@Body\s*</main>"), layout);
    }

    /// <summary>
    /// Every routable page renders a focusable <c>&lt;h1&gt;</c>: either through <c>PageHeader</c> (directly
    /// or via <c>OdsTagAdmin</c>, which composes one) or as its own element carrying
    /// <c>tabindex="-1"</c>. A page with none leaves FocusOnNavigate a silent no-op on arrival.
    /// </summary>
    [Fact]
    public void EveryRoutablePage_HasAFocusableH1()
    {
        var offenders = RoutablePages()
            .Where(page =>
            {
                var text = File.ReadAllText(page);
                var viaHeader = text.Contains("<PageHeader", StringComparison.Ordinal)
                                || text.Contains("<OdsTagAdmin", StringComparison.Ordinal);
                var ownH1 = Regex.IsMatch(text, @"<h1\b[^>]*tabindex=""-1""")
                            || Regex.IsMatch(text, @"HtmlTag=""h1""[^>]*tabindex=""-1""");
                return !viaHeader && !ownH1;
            })
            .Select(Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>A page that brings a PageHeader must not ALSO render its own h1 — that would be two.</summary>
    [Fact]
    public void NoPageWithAPageHeader_AlsoRendersItsOwnH1()
    {
        var offenders = RoutablePages()
            .Where(page =>
            {
                var text = File.ReadAllText(page);
                return (text.Contains("<PageHeader", StringComparison.Ordinal)
                        || text.Contains("<OdsTagAdmin", StringComparison.Ordinal))
                       && Regex.IsMatch(text, @"<h1\b|HtmlTag=""h1""|Typo\.h1\b");
            })
            .Select(Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    // ── §2 — autocomplete ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_CredentialInputs_CarryTheirAutocompleteTokens()
    {
        await using var ctx = NewContext();
        AddAuthClient(ctx, new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));

        var login = ctx.Render<Login>();

        var inputs = login.FindAll("input");
        // The unmatched attribute has to reach the real <input>, not stop at MudTextField's wrapper.
        Assert.Contains(inputs, i => i.GetAttribute("autocomplete") == "username");
        Assert.Contains(inputs, i => i.GetAttribute("autocomplete") == "current-password"
                                     && i.GetAttribute("type") == "password");
        var h1 = Assert.Single(login.FindAll("h1"));
        Assert.Equal("-1", h1.GetAttribute("tabindex"));
        // Arrival focus belongs to FocusOnNavigate; the page itself must not take it on first render.
        Assert.Empty(FocusCalls(ctx));
    }

    [Fact]
    public async Task Login_TwoStepPhase_FocusesItsHeading_AndOffersTheOneTimeCodeToken()
    {
        await using var ctx = NewContext();
        AddAuthClient(ctx, new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"detail":"RequiresTwoFactor"}""", Encoding.UTF8, "application/problem+json"),
        })));

        var login = ctx.Render<Login>();
        login.Find("button.mud-button-filled").Click();

        login.WaitForAssertion(() => Assert.Equal("Two-step verification", login.Find("h1").TextContent.Trim()));
        var h1 = login.Find("h1");
        Assert.Equal("-1", h1.GetAttribute("tabindex"));
        Assert.Equal("one-time-code", login.Find("input").GetAttribute("autocomplete"));
        // The swap removes the button the user just pressed; focus must land on the new heading, not <body>.
        login.WaitForAssertion(() =>
            Assert.Equal(h1.GetAttribute("blazor:elementReference"), Assert.Single(FocusCalls(ctx))));
    }

    [Fact]
    public async Task Register_EmailField_CarriesTheEmailToken()
    {
        await using var ctx = NewContext();
        AddAuthClient(ctx, new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        ctx.Services.AddSingleton(new Mock<ILegalApiClient>().Object);

        var register = ctx.Render<Register>();

        var tokens = register.FindAll("input:not([type=checkbox])").Select(i => i.GetAttribute("autocomplete")).ToList();
        Assert.Equal(["email", "new-password", "new-password"], tokens);
        Assert.Single(register.FindAll("h1"));
    }

    [Fact]
    public async Task ConfirmEmail_EveryPhaseCarriesAFocusableH1_AndTheOutcomeTakesFocus()
    {
        await using var ctx = NewContext();
        var release = new TaskCompletionSource<HttpResponseMessage>();
        AddAuthClient(ctx, new StubHandler(_ => release.Task));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/confirm-email?userId=u1&code=c1");

        var page = ctx.Render<ConfirmEmail>();

        // Verifying: FocusOnNavigate needs an h1 to land on while the request is in flight.
        var verifying = Assert.Single(page.FindAll("h1"));
        Assert.Equal("-1", verifying.GetAttribute("tabindex"));
        Assert.Empty(FocusCalls(ctx));

        release.SetResult(new HttpResponseMessage(HttpStatusCode.OK));

        page.WaitForAssertion(() => Assert.Equal("Email confirmed", page.Find("h1").TextContent.Trim()));
        var outcome = page.Find("h1");
        Assert.Equal("-1", outcome.GetAttribute("tabindex"));
        page.WaitForAssertion(() =>
            Assert.Equal(outcome.GetAttribute("blazor:elementReference"), Assert.Single(FocusCalls(ctx))));
    }

    [Fact]
    public async Task ConfirmEmail_AnIncompleteLinkOpensOnFailed_WithoutTakingArrivalFocus()
    {
        await using var ctx = NewContext();
        AddAuthClient(ctx, new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        var page = ctx.Render<ConfirmEmail>();

        var h1 = Assert.Single(page.FindAll("h1"));
        Assert.Equal("Confirmation failed", h1.TextContent.Trim());
        Assert.Equal("-1", h1.GetAttribute("tabindex"));
        Assert.Equal("email", page.Find("input").GetAttribute("autocomplete"));
        Assert.Empty(FocusCalls(ctx));
    }

    private static void AddAuthClient(BunitContext ctx, StubHandler handler)
    {
        ctx.Services.AddSingleton(sp => new AuthApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, new AntiforgeryTokenStore(sp)));
        ctx.Services.AddSingleton<CookieAuthenticationStateProvider>();
    }

    /// <summary>Ids of the elements <c>ElementReference.FocusAsync</c> was called on, in order.</summary>
    private static List<string> FocusCalls(BunitContext ctx) =>
        ctx.JSInterop.Invocations
            .Where(i => i.Identifier.EndsWith("domWrapper.focus", StringComparison.Ordinal))
            .Select(i => ((ElementReference)i.Arguments[0]!).Id)
            .ToList();

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    // ── §3 — modal name ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OdsModal_WithHiddenHeader_IsNamedByItsAriaLabel()
    {
        await using var ctx = NewContext();

        var cut = ctx.Render<ModalHost>(p => p
            .Add(h => h.HideHeader, true)
            .Add(h => h.AriaLabel, "statement.pdf"));

        var dialog = cut.Find("[role=dialog]");
        Assert.Equal("statement.pdf", dialog.GetAttribute("aria-label"));
        // No aria-labelledby left pointing at an empty title element, which would win over aria-label.
        Assert.Null(dialog.GetAttribute("aria-labelledby"));
        Assert.Empty(cut.FindAll(".mud-dialog-title"));
    }

    [Fact]
    public async Task OdsModal_WithAVisibleTitle_KeepsTheTitleAsItsName()
    {
        await using var ctx = NewContext();

        var cut = ctx.Render<ModalHost>(p => p
            .Add(h => h.Title, "Analyze statement")
            .Add(h => h.AriaLabel, "Fallback"));

        var dialog = cut.Find("[role=dialog]");
        Assert.Null(dialog.GetAttribute("aria-label"));
        var labelledBy = dialog.GetAttribute("aria-labelledby");
        Assert.False(string.IsNullOrEmpty(labelledBy));
        Assert.Contains("Analyze statement", cut.Find($"#{labelledBy}").TextContent);
    }

    [Fact]
    public void FilePreviewDialog_HidesEveryLigatureFromTheAccessibilityTree()
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "FilePreviewDialog.razor"));

        var unhidden = Regex.Matches(text, @"<span class=""material-icons""[^>]*>")
            .Select(m => m.Value)
            .Where(tag => !tag.Contains("aria-hidden=\"true\"", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(unhidden);
    }

    // ── §4 — page titles ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryRoutablePage_SetsAPageTitle()
    {
        var offenders = RoutablePages()
            .Where(page => !File.ReadAllText(page).Contains("<PageTitle>", StringComparison.Ordinal))
            .Select(Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheFallbackDocumentTitle_IsTheProductName()
    {
        var html = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "index.html"));

        Assert.Contains("<title>Odyssey</title>", html, StringComparison.Ordinal);
    }

    private static IEnumerable<string> RoutablePages() =>
        ClientSource.RazorFiles()
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"^\s*@page\s", RegexOptions.Multiline));

    private static string Relative(string path) => Path.GetRelativePath(ClientSource.Root, path);

    /// <summary>An open modal beside the provider its MudDialog teleports into.</summary>
    public sealed class ModalHost : ComponentBase
    {
        [Parameter] public bool HideHeader { get; set; }

        [Parameter] public string? AriaLabel { get; set; }

        [Parameter] public string? Title { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<OdsModal>(2);
            builder.AddComponentParameter(3, nameof(OdsModal.Open), true);
            builder.AddComponentParameter(4, nameof(OdsModal.HideHeader), HideHeader);
            builder.AddComponentParameter(5, nameof(OdsModal.AriaLabel), AriaLabel);
            if (Title is { } title)
                builder.AddComponentParameter(6, nameof(OdsModal.Title), (RenderFragment)(b => b.AddContent(0, title)));
            builder.AddComponentParameter(7, nameof(OdsModal.ChildContent), (RenderFragment)(b => b.AddContent(0, "Body")));
            builder.CloseComponent();
        }
    }
}
