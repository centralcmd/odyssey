using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints over <c>/account</c>'s email section (issue #246), in the
/// <see cref="PasswordSurfaceSourceTests"/> idiom. The section used to be a design preview that reported
/// success without a request; these pin that it now goes through the first-party endpoint and keeps the
/// accessibility properties its review asked for.
/// </summary>
public class AccountEmailSectionSourceTests
{
    private const string Section = "Pages/AccountEmailSection.razor";

    [Fact]
    public void TheChange_GoesThroughTheFirstPartyEndpoint()
    {
        var text = Read();

        Assert.Contains("AuthApiClient.ChangeEmailAsync(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("manage/info", Code(text), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePassword_IsClearedOnEveryOutcome_BeforeBranching()
    {
        // Cleared once, between the response and the success branch, so neither path can keep it.
        var code = Code(Read());
        var call = code.IndexOf("ChangeEmailAsync(", StringComparison.Ordinal);
        var clear = code.IndexOf("_password = string.Empty;", call, StringComparison.Ordinal);
        var branch = code.IndexOf("if (result.IsSuccess)", call, StringComparison.Ordinal);

        Assert.True(call >= 0 && clear > call && clear < branch);
    }

    [Fact]
    public void Outcomes_UseOdsAlert_WithNoHandRolledBannerOrSecondAnnouncement()
    {
        // WCAG 1.1.1/4.1.3: the hand-rolled banner exposed its ligature icon as text, and a snackbar
        // beside the role="alert" banner announced every failure twice.
        var text = Read();

        Assert.Contains("<OdsAlert Severity=\"Severity.Success\"", text, StringComparison.Ordinal);
        Assert.Contains("<OdsAlert Severity=\"Severity.Error\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"alert", text, StringComparison.Ordinal);
        Assert.DoesNotContain("material-icons", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Snackbar", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FocusMovesToTheOutcome_SinceTheSubmitStaysDisabled()
    {
        // WCAG 2.4.3: the button disables while saving and the cleared password keeps it disabled, so
        // without this focus falls to <body>.
        var text = Read();

        Assert.Contains("@ref=\"_outcomeRef\" tabindex=\"-1\"", text, StringComparison.Ordinal);
        Assert.Contains("_outcomeRef.FocusAsync()", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFieldsAreAForm_AndDeclareTheirInputPurpose()
    {
        var text = Read();

        Assert.Contains("<form", text, StringComparison.Ordinal);
        Assert.Contains("ButtonType=\"ButtonType.Submit\"", text, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"email\"", text, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"current-password\"", text, StringComparison.Ordinal);
    }

    private static string Read() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, Section.Replace('/', Path.DirectorySeparatorChar)));

    private static string Code(string text) => text[text.IndexOf("@code {", StringComparison.Ordinal)..];
}
