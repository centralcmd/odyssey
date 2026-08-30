using System.Security.Claims;
using Odyssey.Client.Auth;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The first-run gate chain <c>MainLayout</c> runs before the app body renders: password change, then
/// legal acceptance, then profile onboarding.
/// </summary>
/// <remarks>
/// <para>
/// The order matters because the API enforces the same one. <c>PasswordChangeRequiredMiddleware</c>
/// refuses every authenticated endpoint but five, and <c>GET /api/legal/status</c> is deliberately not
/// among them — so a client that routes a flagged user to <c>/accept-terms</c> first lands them on an
/// interstitial the server will not serve.
/// </para>
/// <para>
/// The regression these pin: the chain used to be three predicates each returning "did I navigate?",
/// and <c>RedirectToGate</c> returned <see langword="false"/> when the browser was already on the gate —
/// indistinguishable from "this gate does not apply". A freshly bootstrapped administrator (issue #290)
/// owes all three at once, so once it was parked on <c>/change-password-required</c> the chain fell
/// through and pulled it onto <c>/accept-terms</c>, which then dead-ended on a 403. It was not a race:
/// <c>AuthorizeRouteView</c> renders <c>DefaultLayout</c> (<c>MainLayout</c>) for the whole async
/// authorizing phase, so the layout initialises on a cold load of every <c>[Authorize]</c> gate page,
/// and a direct visit to the password form bounced off it every time.
/// </para>
/// <para>
/// The property that closes it is that <see cref="FirstRunGateChain.Owed"/> takes no current route:
/// where the browser is cannot change which gate is owed.
/// </para>
/// </remarks>
public class FirstRunGateChainTests
{
    private const string PasswordGate = "/change-password-required";
    private const string LegalGate = "/accept-terms";
    private const string OnboardingGate = "/onboarding";

    /// <summary>The shape <c>BootstrapAdminSeeder</c> produces: a one-time password and no profile.</summary>
    private static ProfileDto FirstRunAdmin() => new() { MustChangePassword = true, IsComplete = false };

    private static ClaimsPrincipal User(bool owesLegalAcceptance)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "u-1") };
        if (owesLegalAcceptance)
        {
            claims.Add(new Claim(LegalClaims.PendingAcceptanceType, nameof(LegalDocumentType.License)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    /// <summary>
    /// A bootstrapped administrator owes all three at once. The password gate wins, and it must keep
    /// winning for as long as the flag is set — that is the only screen that can clear it.
    /// </summary>
    [Fact]
    public void AFirstRunAdmin_OwesThePasswordGate_NotTheLegalOne()
    {
        Assert.Equal(PasswordGate, FirstRunGateChain.Owed(FirstRunAdmin(), User(owesLegalAcceptance: true)));
    }

    /// <summary>
    /// The regression, stated as the property that prevents it: the answer is a function of the account
    /// alone. Re-asking from the gate's own route — which <c>MainLayout</c> does on every cold load of
    /// it — must not produce a different gate.
    /// </summary>
    [Fact]
    public void TheAnswer_DoesNotDependOnWhereTheBrowserIs()
    {
        var profile = FirstRunAdmin();
        var user = User(owesLegalAcceptance: true);

        // There is no route parameter to vary: asking twice is asking the same question. Pinned as a
        // test so that reintroducing one is a failure rather than a refactor.
        Assert.Equal(FirstRunGateChain.Owed(profile, user), FirstRunGateChain.Owed(profile, user));
        Assert.Equal(PasswordGate, FirstRunGateChain.Owed(profile, user));
    }

    [Fact]
    public void WithThePasswordFlagClear_TheLegalGateIsNext()
    {
        var profile = new ProfileDto { MustChangePassword = false, IsComplete = false };

        Assert.Equal(LegalGate, FirstRunGateChain.Owed(profile, User(owesLegalAcceptance: true)));
    }

    [Fact]
    public void WithBothCleared_AnIncompleteProfile_ReachesTheOnboardingGate()
    {
        var profile = new ProfileDto { MustChangePassword = false, IsComplete = false };

        Assert.Equal(OnboardingGate, FirstRunGateChain.Owed(profile, User(owesLegalAcceptance: false)));
    }

    [Fact]
    public void AFullyOnboardedUser_OwesNothing()
    {
        var profile = new ProfileDto { MustChangePassword = false, IsComplete = true };

        Assert.Null(FirstRunGateChain.Owed(profile, User(owesLegalAcceptance: false)));
    }

    /// <summary>
    /// A failed profile read fails <b>open</b> for the two profile-derived gates — they are presentation,
    /// and the API refuses independently — but must not swallow the legal gate, whose input is the
    /// principal rather than the profile.
    /// </summary>
    [Fact]
    public void AFailedProfileRead_FailsOpen_WithoutSwallowingTheLegalGate()
    {
        Assert.Null(FirstRunGateChain.Owed(profile: null, User(owesLegalAcceptance: false)));
        Assert.Equal(LegalGate, FirstRunGateChain.Owed(profile: null, User(owesLegalAcceptance: true)));
    }

    /// <summary>An anonymous principal carries no pending-acceptance claim and owes no legal gate.</summary>
    [Fact]
    public void ANullPrincipal_OwesNoLegalGate()
    {
        var profile = new ProfileDto { MustChangePassword = false, IsComplete = true };

        Assert.Null(FirstRunGateChain.Owed(profile, user: null));
    }
}
