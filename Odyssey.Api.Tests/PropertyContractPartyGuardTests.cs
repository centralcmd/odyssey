using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>Wire-shape guards for the property party kind (issue #208 AC 14, AC 15).</summary>
public class PropertyContractPartyGuardTests
{
    /// <summary>
    /// <see cref="PropertyContractLink"/> is a separate record from <see cref="AccountContractLink"/>
    /// only because DTOs are sealed; the two render in one tile, so their shapes must not drift.
    /// </summary>
    [Fact]
    public void PropertyContractLink_has_exactly_the_shape_of_AccountContractLink()
    {
        static IEnumerable<(string, Type)> Shape(Type type) =>
            type.GetProperties().Select(p => (p.Name, p.PropertyType)).OrderBy(p => p.Name, StringComparer.Ordinal);

        Assert.Equal(Shape(typeof(AccountContractLink)), Shape(typeof(PropertyContractLink)));
    }

    /// <summary>
    /// Ordinal 2 was <c>InsurancePolicy</c> and stays a permanent hole: a stale payload reading 2 must
    /// never come to mean Property.
    /// </summary>
    [Fact]
    public void ContractPartyKind_keeps_ordinal_2_retired_and_Property_is_3()
    {
        Assert.DoesNotContain(2, Enum.GetValues<ContractPartyKind>().Select(k => (int)k));
        Assert.Equal(3, (int)ContractPartyKind.Property);
        Assert.Equal(0, (int)ContractPartyKind.Account);
        Assert.Equal(1, (int)ContractPartyKind.Institution);
    }
}
