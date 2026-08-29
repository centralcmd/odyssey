using System.Text.RegularExpressions;
using Odyssey.Client.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints over the password surfaces (issue #405), in the <see cref="SourceConventionTests"/>
/// idiom: text checks over the checked-in sources, no bUnit and no render harness. They assert that
/// the markup <em>exists</em> — behaviour is the E2E tier's job — which is exactly the guarantee that
/// the defects here need, because every one of them compiles, renders, and looks right to anyone not
/// reading the accessibility tree or comparing two files side by side.
/// </summary>
public class PasswordSurfaceSourceTests
{
    private const string ForgotPassword = "Pages/Auth/ForgotPassword.razor";
    private const string ResetPassword = "Pages/Auth/ResetPassword.razor";
    private const string Login = "Pages/Auth/Login.razor";
    private const string Register = "Pages/Auth/Register.razor";
    private const string AccountPassword = "Pages/AccountPasswordSection.razor";
    private const string RulesComponent = "Components/OdsPasswordRules.razor";

    // Issue #406's shared triad and the forced-reset gate that consumes it.
    private const string ChangeForm = "Components/OdsPasswordChangeForm.razor";
    private const string GatePage = "Pages/ChangePasswordRequired.razor";
    private const string GatePageCode = "Pages/ChangePasswordRequired.razor.cs";

    private static readonly string[] ResetPages = [ForgotPassword, ResetPassword];

    // ── The entry point and the page contract ────────────────────────────────

    [Fact]
    public void TheLoginPage_LinksToTheForgotPasswordPage()
    {
        // Without this the whole feature is unreachable: a user who has forgotten their password has,
        // by definition, no other way in.
        Assert.Contains("Href=\"/forgot-password\"", Read(Login), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ForgotPassword)]
    [InlineData(ResetPassword)]
    public void BothPages_AreAnonymousAndUseTheAuthLayout(string page)
    {
        var text = Read(page);

        Assert.Contains("@attribute [AllowAnonymous]", text, StringComparison.Ordinal);
        Assert.Contains("@layout AuthLayout", text, StringComparison.Ordinal);
    }

    // ── Outcome panels ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(ForgotPassword)]
    [InlineData(ResetPassword)]
    public void OutcomePanels_UseOdsAlert_AndLetItsSeverityDeriveTheLiveRole(string page)
    {
        // OdsAlert already maps severity → role (Error/Warning → alert, otherwise status), and is
        // adopted across the client. Hand-rolling a <div class="alert" role="…"> here would duplicate
        // it — and the apparent precedent for doing so is itself wrong: three existing hand-rolled
        // panels tag an *error* variant role="status", which announces politely or not at all.
        var text = Read(page);

        Assert.Contains("<OdsAlert", text, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"alert", text, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"alert", text, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"status", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResetPagesOutcomeSeverities_MatchTheirMeaning()
    {
        // Done is the only success. Both dead-end panels — a link that never carried a code, and one
        // that is spent — are errors, so OdsAlert gives them role="alert".
        var text = Read(ResetPassword);

        Assert.Contains("Severity.Success", text, StringComparison.Ordinal);
        Assert.Equal(2, Count(text, "Severity.Error,"));
    }

    // ── Focus management ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(ForgotPassword)]
    [InlineData(ResetPassword)]
    public void EachOutcomeHeading_IsAProgrammaticFocusTarget(string page)
    {
        // A phase swap replaces the entire form. The live region announces the message; the focus move
        // is what tells a screen-reader user where they now are — and on /reset-password's first-render
        // InvalidLink phase it is the *only* thing that does, since a region that was present at first
        // paint announces nothing.
        var text = Read(page);

        Assert.Matches(new Regex(@"<h2\s+@ref=""_headingRef""\s+tabindex=""-1""", RegexOptions.None), text);
        Assert.Contains("OnAfterRenderAsync", text, StringComparison.Ordinal);
        Assert.Contains("_headingRef.FocusAsync()", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BothPhasesOfTheForgotPasswordPage_HaveAFocusTarget()
    {
        // "Send again" is the one transition that unmounts the element the user just activated: the
        // Sent panel goes, its button with it, and the Request form comes back. Without a heading to
        // receive focus on that side too, focus falls to <body> and the user resumes from the top of
        // the document — WCAG 2.4.3. Both panels carry one, so the count is two.
        Assert.Equal(2, Count(Read(ForgotPassword), "tabindex=\"-1\""));
    }

    [Theory]
    [InlineData(ForgotPassword)]
    [InlineData(ResetPassword)]
    public void ThePhaseSentinel_IsNullable(string page)
    {
        // `Phase _announcedPhase` would default to the first enum member — on /reset-password that is
        // InvalidLink, precisely the phase that must focus on the very first render. The guard would
        // then compare equal on entry and silently never fire, in the one case with no fallback.
        Assert.Contains("private Phase? _announcedPhase;", Read(page), StringComparison.Ordinal);
    }

    // ── Input purpose and keyboard ───────────────────────────────────────────

    [Fact]
    public void EveryEmailFieldOnTheResetPages_DeclaresItsInputPurpose()
    {
        // WCAG 1.3.5. It matters disproportionately here: a forgotten-password flow is the flow most
        // likely to be driven by a password manager.
        foreach (var page in ResetPages)
        {
            var text = Read(page);
            Assert.Equal(Count(text, "InputType.Email"), Count(text, "autocomplete=\"email\""));
        }
    }

    [Fact]
    public void EveryNewPasswordFieldOnTheResetPage_DeclaresItsInputPurpose()
    {
        var text = Read(ResetPassword);

        Assert.Equal(Count(text, "InputType.Password"), Count(text, "autocomplete=\"new-password\""));
    }

    [Theory]
    [InlineData(ForgotPassword)]
    [InlineData(ResetPassword)]
    public void EveryFieldOnTheResetPages_SubmitsOnEnter(string page)
    {
        // MudTextField gets no native form submit in this codebase's pattern, so Enter has to be wired
        // per field — the convention every other Auth page follows.
        var text = Read(page);

        Assert.Equal(Count(text, "<MudTextField"), Count(text, "OnKeyDown="));
    }

    // ── One rule implementation ──────────────────────────────────────────────

    [Fact]
    public void OnlyOneComponentRendersAPasswordChecklist()
    {
        // The drift this ends was real and shipping: /account advertised "At least 6 characters" —
        // Identity's default — while registration enforced 16, so the page told users something the
        // server would reject.
        var renderers = ClientSource.SourceFiles()
            .Where(file => !IsFile(file, RulesComponent) && !IsFile(file, "Auth/PasswordPolicy.cs"))
            .Where(file => RuleLabels.Any(label => File.ReadAllText(file).Contains(label, StringComparison.Ordinal)))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(renderers.Count == 0,
            "Password rule labels belong to PasswordPolicy alone; these re-declare them:\n"
            + string.Join('\n', renderers));
    }

    [Theory]
    [InlineData(Register)]
    [InlineData(ResetPassword)]
    [InlineData(ChangeForm)]
    public void EverySurfaceThatSetsAPassword_RendersTheSharedChecklist(string page)
    {
        Assert.Contains("<OdsPasswordRules", Read(page), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AccountPassword)]
    [InlineData(GatePage)]
    public void TheTwoChangePasswordSurfaces_RenderTheSharedTriad(string page)
    {
        // The checklist reaches these two through OdsPasswordChangeForm rather than directly (issue
        // #406): they need the whole current → new → confirm triad, not just the rules, and this issue
        // would have created the second hand-rolled copy of it. Asserting the composition — rather than
        // relaxing the check above to "contains a checklist somewhere" — is what keeps a future page
        // from re-hand-rolling the fields while still passing.
        Assert.Contains("<OdsPasswordChangeForm", Read(page), StringComparison.Ordinal);
        Assert.DoesNotContain("<MudTextField", Read(page), StringComparison.Ordinal);
    }

    [Fact]
    public void NoFileOutsidePasswordPolicy_DeclaresAMinimumPasswordLength()
    {
        var declarations = ClientSource.SourceFiles()
            .Where(file => !IsFile(file, "Auth/PasswordPolicy.cs"))
            .Where(file => MinimumLengthDeclaration.IsMatch(File.ReadAllText(file)))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(declarations.Count == 0,
            "A second minimum-length declaration can drift from the server's policy:\n"
            + string.Join('\n', declarations));
    }

    [Fact]
    public void TheAccountPage_NoLongerClaimsSixCharacters()
    {
        // Named explicitly rather than left to the lint above: this exact string was the live defect.
        Assert.DoesNotContain("At least 6 characters", Read(AccountPassword), StringComparison.Ordinal);
    }

    // ── The shared triad and the forced-reset gate (issue #406) ──────────────

    [Fact]
    public void TheSharedTriad_DeclaresEveryFieldsInputPurpose()
    {
        // WCAG 1.3.5, pinned on the component rather than left to each surface. It matters MORE on the
        // gate page than on /account, because the user arrives there by an involuntary redirect, where
        // a password manager's help is more valuable rather than less — and /account carried none of
        // these before the extraction, so this back-fills it at the same time.
        var text = Read(ChangeForm);

        Assert.Equal(3, Count(text, "InputType.Password"));
        Assert.Equal(1, Count(text, "autocomplete=\"current-password\""));
        Assert.Equal(2, Count(text, "autocomplete=\"new-password\""));
    }

    [Theory]
    [InlineData(ChangeForm)]
    [InlineData(AccountPassword)]
    public void TheChangePasswordSurfaces_AnnounceFailuresAssertively(string page)
    {
        // WCAG 4.1.3. OdsAlert derives role from severity (Error/Warning → alert), so an error banner
        // gets role="alert" for free. /account previously hand-rolled `<div class="alert error"
        // role="status">` — an error announced politely — and copying that markup into the gate would
        // have propagated it to a second surface.
        var text = Read(page);

        Assert.DoesNotContain("class=\"alert", text, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"status", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSharedTriad_RefusesANoOpChange()
    {
        // Identity accepts a change to the same password (it just rehashes), so the server enforces
        // this explicitly — and the form has to as well, or the user is left with a button that
        // submits into a 400 it could have predicted. On the gate page it is the difference between
        // a real rotation and clearing the block with the compromised password still live.
        var text = Read(ChangeForm);

        Assert.Contains("SameAsCurrent", text, StringComparison.Ordinal);
        Assert.Contains("!SameAsCurrent", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGatePage_IsRenderedUnderTheNavLessLayout()
    {
        // No rail, no module switcher, no command palette: a forced credential change outranks
        // everything, so there must be no chrome offering a way around it.
        var text = Read(GatePage);

        Assert.Contains("@layout OnboardingLayout", text, StringComparison.Ordinal);
        Assert.Contains("@attribute [Authorize]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGatePage_OffersBothEscapeHatches()
    {
        // A user who does not know their current password cannot complete this form, so the page has
        // to let them leave. Without both of these the gate is a lockout.
        var text = Read(GatePage);

        Assert.Contains("/forgot-password", text, StringComparison.Ordinal);
        Assert.Contains("SignOutAsync", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGateHeading_IsAProgrammaticFocusTarget()
    {
        // WCAG 2.4.3: the user arrives here by an involuntary redirect, so the reason has to be
        // announced rather than left silent.
        var text = Read(GatePage);

        Assert.Matches(new Regex(@"<h1\s+@ref=""_headingRef""\s+tabindex=""-1""", RegexOptions.None), text);
        Assert.Contains("_headingRef.FocusAsync()", Read(GatePageCode), StringComparison.Ordinal);
    }

    [Fact]
    public void TheGatePagesExplanation_IsAssociatedWithAField_NotAWrappingDiv()
    {
        // WCAG 1.3.1. aria-describedby applies only to the element carrying it and does not cascade, so
        // a role-less wrapper holding it associates the explanation with nothing — the div isn't exposed
        // to assistive tech at all. A screen-reader user jumping straight into the form would never hear
        // why they were redirected here. The component takes DescribedBy and puts it on the first field.
        var page = Read(GatePage);

        Assert.Contains("DescribedBy=\"cpr-explanation\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"cpr-explanation\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<div aria-describedby", page, StringComparison.Ordinal);

        // ...and the component actually forwards it onto a field rather than dropping it. It rides the
        // unmatched-attribute path (MudBlazor captures it into UserAttributes and splats it onto the
        // input), NOT an explicit UserAttributes binding — see the next test for why that distinction
        // is load-bearing rather than stylistic.
        Assert.Contains("aria-describedby=\"@DescribedBy\"", Read(ChangeForm), StringComparison.Ordinal);
    }

    /// <summary>
    /// The shared form must never assign <c>UserAttributes</c> on a field that also carries a loose
    /// attribute.
    /// </summary>
    /// <remarks>
    /// Blazor throws at render time when a <c>CaptureUnmatchedValues</c> parameter is supplied
    /// explicitly and unmatched attributes exist — "cannot be set explicitly when also used to capture
    /// unmatched values" — and supplying null still counts as supplying it. The first field once had
    /// both <c>autocomplete</c> and <c>UserAttributes</c>, which crashed the gate page and the account
    /// security page on render. Nothing caught it: every test over this component reads the source as
    /// text, and this project has no renderer, so a component that cannot render at all still passed.
    /// This lint is the cheap stand-in for that missing coverage.
    /// </remarks>
    [Fact]
    public void TheSharedTriad_NeverBindsUserAttributesAlongsideLooseAttributes()
    {
        var text = Read(ChangeForm);

        Assert.Equal(3, Count(text, "InputType.Password"));
        Assert.DoesNotContain("UserAttributes=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGatePagesExplanation_ClaimsNoCauseThatIsOnlyTrueOfAnAdminReset()
    {
        // Two things reach this page: an admin-initiated reset (#406) and the seeded bootstrap
        // administrator, whose one-time password nobody asked them to change (#290). The flag carries no
        // reason, so the sentence has to be true of both — the copy is the only place that can get this
        // wrong, and getting it wrong tells a brand-new operator something that did not happen.
        var explanation = Regex.Match(
            Read(GatePage), @"id=""cpr-explanation""\s*>(?<text>.*?)</p>", RegexOptions.Singleline);

        Assert.True(explanation.Success, "The gate page's explanatory paragraph could not be located.");
        Assert.DoesNotContain("administrator", explanation.Groups["text"].Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheGatePagesFocusOnceSentinel_IsNullable()
    {
        // `bool _headingFocused` would default to false — indistinguishable from "not yet handled" —
        // and the identical guard bug was found live in #405's draft. A nullable sentinel cannot be
        // confused with its own unset state.
        Assert.Contains("private bool? _headingFocused;", Read(GatePageCode), StringComparison.Ordinal);
    }

    // ── The client policy tracks the server's ────────────────────────────────

    [Fact]
    public void TheClientMinimumLength_EqualsTheServersRequiredLength()
    {
        // The only lint here that reads outside the client tree, and deliberately so: the value it
        // guards is a mirror of a server setting, so checking the client half alone would let a
        // future server-side change desynchronise the displayed rules with the test still green.
        // The server half lives in Odyssey.Context, shared by the API and the migrations
        // job's bootstrap-admin seeder (issue #290) — it is no longer inline in Odyssey.Api/Program.cs.
        var policy = File.ReadAllText(
            ClientSource.Sibling(Path.Combine("Odyssey.Context", "PasswordPolicy.cs")));
        var configured = Regex.Match(policy, @"RequiredLength\s*=\s*(\d+)\s*;");

        Assert.True(configured.Success,
            "Could not find RequiredLength in Odyssey.Context/PasswordPolicy.cs — "
            + "the client's PasswordPolicy.MinLength has nothing left to track.");
        Assert.Equal(PasswordPolicy.MinLength, int.Parse(configured.Groups[1].Value));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The rule labels as <see cref="PasswordPolicy"/> declares them, matched case-sensitively so
    /// ordinary prose about passwords ("…must include an uppercase letter…") isn't mistaken for a
    /// second declaration of the rule set.
    /// </summary>
    private static readonly string[] RuleLabels =
    [
        "An uppercase letter", "A lowercase letter", "A symbol (!@#$",
    ];

    private static readonly Regex MinimumLengthDeclaration =
        new(@"[Mm]in(imum)?[_]?([Pp]assword)?[Ll]ength\s*=\s*\d+", RegexOptions.Compiled);

    private static string Read(string clientRelativePath) =>
        File.ReadAllText(Path.Combine(ClientSource.Root, clientRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static bool IsFile(string absolutePath, string clientRelativePath) =>
        string.Equals(
            ClientSource.Relative(absolutePath),
            clientRelativePath.Replace('/', Path.DirectorySeparatorChar),
            StringComparison.Ordinal);

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
