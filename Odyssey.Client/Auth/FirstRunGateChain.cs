using System.Security.Claims;
using Odyssey.Client.Authorization;
using Odyssey.Client.Pages;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Auth;

/// <summary>
/// Which blocking gate, if any, a session owes right now — the client half of the first-run chain
/// <c>MainLayout</c> runs before the app body renders.
/// </summary>
/// <remarks>
/// <para>
/// The order is the order the <b>server</b> enforces, and it is not a preference:
/// </para>
/// <list type="number">
/// <item><description>
/// A forced password change (issue #406) outranks everything. <c>PasswordChangeRequiredMiddleware</c>
/// refuses every authenticated endpoint but five, and neither <c>GET /api/legal/status</c> nor
/// <c>POST /api/legal/respond</c> is among them — so sending a flagged user to <c>/accept-terms</c>
/// first strands them on an interstitial the API will not serve.
/// </description></item>
/// <item><description>
/// Legal acceptance (issue #354 §5) outranks onboarding: a user who owes a response shouldn't be asked
/// to complete a profile for an account they may be about to decline terms for.
/// </description></item>
/// <item><description>
/// Profile completeness (issue #316 §5).
/// </description></item>
/// </list>
/// <para>
/// <b>The answer deliberately does not depend on where the browser currently is.</b> That is the whole
/// point of resolving it in one place. The chain used to be three predicates each returning "did I
/// navigate?", and a gate that was already on screen reported <see langword="false"/> — indistinguishable
/// from "does not apply" — so the chain fell through to the next gate. This is reachable in ordinary
/// operation, because <c>AuthorizeRouteView</c> renders <c>DefaultLayout</c> (<c>MainLayout</c>) for the
/// whole async authorizing phase, so the layout initialises on a cold load of every <c>[Authorize]</c>
/// gate page whatever that page's own <c>@layout</c> says. A freshly bootstrapped administrator
/// (issue #290) owes all three at once, so it was pulled off <c>/change-password-required</c> onto
/// <c>/accept-terms</c>, whose status call the API then refused with a 403 — a blank interstitial and no
/// reachable route back to the only form that could clear the flag.
/// </para>
/// <para>
/// None of this is a security boundary; the API's two middlewares are. A wrong answer here costs a user
/// a confusing screen, not access.
/// </para>
/// </remarks>
public static class FirstRunGateChain
{
    /// <summary>
    /// The route of the gate that applies, or <see langword="null"/> when the app body may render.
    /// </summary>
    /// <param name="profile">
    /// The caller's profile, or <see langword="null"/> when the read failed. A failed read fails
    /// <b>open</b> for both profile-derived gates: they are presentation, and the server refuses
    /// independently.
    /// </param>
    /// <param name="user">The signed-in principal, whose pending-acceptance claims drive the legal gate.</param>
    public static string? Owed(ProfileDto? profile, ClaimsPrincipal? user)
    {
        if (profile is { MustChangePassword: true })
        {
            return PasswordChangeRequiredHandler.GatePath;
        }

        if (user?.HasPendingLegalAcceptance() == true)
        {
            return LegalComplianceHandler.InterstitialPath;
        }

        if (profile is { IsComplete: false })
        {
            return Onboarding.OnboardingPath;
        }

        return null;
    }
}
