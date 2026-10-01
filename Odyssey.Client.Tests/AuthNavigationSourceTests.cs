using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for the two halves of issue #250 that a component test cannot reach: the sign-in
/// navigation (it follows a real <c>POST /login</c>) and the <c>HttpClient</c> pipeline assembled in
/// <c>Program.cs</c>.
/// </summary>
public class AuthNavigationSourceTests
{
    /// <summary>
    /// A client-side route change after sign-in keeps every app-lifetime cache — reference data,
    /// preferences, the limit caches, the auth state — so a second user on the same tab would inherit
    /// the first user's. Only a full reload discards them.
    /// </summary>
    [Fact]
    public void SignIn_NavigatesWithAFullReload()
    {
        var source = Read("Pages/Auth/Login.razor.cs");

        var successBranch = Regex.Match(source, @"case LoginOutcome\.Success:(?<body>.*?)break;", RegexOptions.Singleline);
        Assert.True(successBranch.Success, "LoginOutcome.Success branch not found in Login.razor.cs");
        Assert.Contains("NavigateTo(Destination(ReturnUrl), forceLoad: true)", successBranch.Groups["body"].Value);
    }

    /// <summary>
    /// <c>UnauthorizedHandler</c> only signals when it is actually in the chain every typed client
    /// shares. Registering it in DI without linking it is silent: nothing fails, nothing redirects.
    /// </summary>
    [Fact]
    public void TheUnauthorizedHandler_IsLinkedIntoTheSharedPipeline()
    {
        var source = Read("Program.cs");

        Assert.Contains("passwordChange.InnerHandler = unauthorized;", source);
        Assert.Contains("unauthorized.InnerHandler = browserCredentials;", source);
        Assert.Contains("builder.Services.AddSingleton<SessionExpiredNotifier>();", source);
    }

    /// <summary>The handler only raises the signal; something has to act on it, on every layout.</summary>
    [Fact]
    public void TheSessionExpiryPrompt_IsMountedAboveTheRouter()
    {
        var app = Read("App.razor");

        var prompt = app.IndexOf("<SessionExpiryRedirect", StringComparison.Ordinal);
        Assert.True(prompt >= 0, "SessionExpiryRedirect is not mounted in App.razor");
        Assert.True(prompt < app.IndexOf("<Router", StringComparison.Ordinal),
            "SessionExpiryRedirect must sit above the Router so it covers every layout");
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(ClientSource.Root, relativePath));
}
