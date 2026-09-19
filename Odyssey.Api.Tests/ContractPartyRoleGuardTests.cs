using Xunit;
using ContextRole = Odyssey.Context.ContractPartyRole;
using DtoRole = Odyssey.Dtos.Finance.ContractPartyRole;

namespace Odyssey.Api.Tests;

/// <summary>
/// The build-time half of issue #121 §4: <c>ContractPartyRole</c> is declared twice — once as the
/// persisted entity enum and once as the wire vocabulary — and the two must stay aligned
/// member-for-member. Mapster maps between them by ordinal, so a member added to one declaration and
/// not the other is silently wrong at runtime rather than a compile error (AC 11).
/// </summary>
public class ContractPartyRoleGuardTests
{
    /// <summary>AC 11 — identical member NAMES and identical ORDINALS, in both directions.</summary>
    [Fact]
    public void BothDeclarations_HaveIdenticalMembersAndOrdinals()
    {
        var context = Enum.GetValues<ContextRole>()
            .ToDictionary(role => role.ToString(), role => (int)role, StringComparer.Ordinal);
        var dto = Enum.GetValues<DtoRole>()
            .ToDictionary(role => role.ToString(), role => (int)role, StringComparer.Ordinal);

        Assert.Equal(context.Keys.Order(StringComparer.Ordinal), dto.Keys.Order(StringComparer.Ordinal));
        Assert.All(context, pair => Assert.Equal(pair.Value, dto[pair.Key]));
    }

    /// <summary>
    /// The v1 ordinals, pinned by LITERAL. They are a wire <em>and</em> persistence contract — stored
    /// as <c>int</c> and serialized as <c>int</c> — so a later member appends and none is renumbered.
    /// Pinning both declarations against literals rather than against each other is what makes a
    /// symmetric "tidy-up" renumbering fail the build.
    /// </summary>
    [Fact]
    public void V1Ordinals_ArePinned()
    {
        Assert.Equal(0, (int)DtoRole.Unspecified);
        Assert.Equal(1, (int)DtoRole.Employee);
        Assert.Equal(2, (int)DtoRole.Employer);
        Assert.Equal(3, (int)DtoRole.Buyer);
        Assert.Equal(4, (int)DtoRole.Seller);
        Assert.Equal(5, (int)DtoRole.ServiceProvider);
        Assert.Equal(6, (int)DtoRole.Other);

        Assert.Equal(0, (int)ContextRole.Unspecified);
        Assert.Equal(6, (int)ContextRole.Other);
    }

    /// <summary>
    /// <c>Unspecified</c> and <c>Other</c> are deliberately BOTH present and mean different things —
    /// "nobody has said" versus "somebody looked and none of these fit". Collapsing one into the other
    /// is the change this pins against: it would lose the distinction that makes a future
    /// role-completeness prompt possible, and would make the client's unknown-ordinal fallback render
    /// an unrecognised role as the deliberate <c>Other</c>.
    /// </summary>
    [Fact]
    public void UnspecifiedAndOther_AreDistinctMembers()
    {
        Assert.NotEqual((int)DtoRole.Unspecified, (int)DtoRole.Other);
        Assert.Equal(0, (int)DtoRole.Unspecified);
    }
}
