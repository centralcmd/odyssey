using System.Security.Claims;
using Odyssey.Client.Authorization;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="AuthorizationExtensions.UserId"/> — extracted (issue #406) from a line copied between
/// <c>/account</c> and <c>/users</c>.
/// </summary>
/// <remarks>
/// Worth pinning despite being three lines: pages use it to tell "me" from "someone else" — <c>/users</c>
/// warns an admin who is about to reset their <em>own</em> password — so a version that checked only one
/// claim spelling would silently answer "not me" for every user, and the warning would simply never
/// appear. That is a defect nothing else in the suite would catch.
/// </remarks>
public class ClaimsPrincipalUserIdTests
{
    [Fact]
    public void TheCookiePipelinesClaimSpelling_isRead() =>
        Assert.Equal("user-1", Principal(new Claim(ClaimTypes.NameIdentifier, "user-1")).UserId());

    [Fact]
    public void AJwtShapedPrincipalsShortSpelling_isReadToo() =>
        Assert.Equal("user-2", Principal(new Claim("sub", "user-2")).UserId());

    [Fact]
    public void WhenBothArePresent_theLongSpellingWins() =>
        Assert.Equal(
            "user-3",
            Principal(new Claim(ClaimTypes.NameIdentifier, "user-3"), new Claim("sub", "user-4")).UserId());

    [Fact]
    public void APrincipalCarryingNeither_isEmpty_notNull() =>
        // Callers compare it against a row id with Ordinal equality; null would NRE at those call sites,
        // and empty compares false against every real id — which is the safe answer for "who am I?".
        Assert.Equal(string.Empty, Principal(new Claim(ClaimTypes.Name, "someone")).UserId());

    [Fact]
    public void AnAnonymousPrincipal_isEmpty() =>
        Assert.Equal(string.Empty, new ClaimsPrincipal(new ClaimsIdentity()).UserId());

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "TestAuth"));
}
