using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Real estate section's homeowner-association tile (issue #217; design system · Properties.jsx):
/// its presence alone opens the tile grid, and an archived contact is stated in the foot, never by
/// tone alone.
/// </summary>
public class PropertyAssociationTileTests
{
    private static ExistingProperty Property(PropertyHomeownerAssociation? association) => new()
    {
        PropertyId = Guid.NewGuid(),
        Name = "Storgata 12",
        Description = "Flat",
        CurrencyCode = "NOK",
        Type = PropertyType.RealEstate,
        RealEstateDetails = new RealEstateDetailsDto { Kind = RealEstateKind.Apartment },
        HomeownerAssociation = association,
    };

    [Fact]
    public void An_association_alone_is_enough_to_show_the_tiles()
    {
        var withLink = Property(new PropertyHomeownerAssociation { ContactId = Guid.NewGuid(), Name = "Storgata Borettslag" });
        var without = Property(null);

        Assert.True(PropertiesCard.HasRealEstateTiles(withLink, withLink.RealEstateDetails!));
        Assert.False(PropertiesCard.HasRealEstateTiles(without, without.RealEstateDetails!));
    }

    [Fact]
    public void The_foot_names_the_type_or_states_the_archived_contact_and_since_when()
    {
        var active = new PropertyHomeownerAssociation { ContactId = Guid.NewGuid(), Name = "A" };
        var archived = active with { Archived = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc) };

        Assert.Equal("Organization", PropertiesCard.AssociationFoot(active));
        Assert.Equal("Archived contact · since Mar 04, 2026", PropertiesCard.AssociationFoot(archived));
    }
}
