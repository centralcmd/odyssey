using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Layout;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The smaller client pieces of the Properties page (issue #167): the kind/status vocabularies, the
/// full-replacement archive body, the nav entry's claim gate, the smart-tag section's property host,
/// and the smooth curve the value chart uses.
/// </summary>
public class PropertyClientTests
{
    [Fact]
    public void KindOf_reads_the_detail_row_matching_the_type_and_falls_back_to_Other()
    {
        var cabin = new ExistingProperty
        {
            PropertyId = Guid.NewGuid(), Name = "Hytta", Description = "Cabin", CurrencyCode = "NOK",
            Type = PropertyType.RealEstate, RealEstateDetails = new RealEstateDetailsDto { Kind = RealEstateKind.Cabin },
        };
        var bare = cabin with { Type = PropertyType.Vehicle, RealEstateDetails = null };

        Assert.Equal(("Cabin", "cabin"), (PropertyVisuals.KindOf(cabin).Label, PropertyVisuals.KindOf(cabin).Icon));
        Assert.Equal("Other", PropertyVisuals.KindOf(bare).Label);
    }

    [Fact]
    public void AddressText_joins_the_parts_that_exist()
    {
        Assert.Equal("Storgata 14, 0155 Oslo, NO", PropertyVisuals.AddressText(new RealEstateDetailsDto
        {
            AddressLine = "Storgata 14", PostalCode = "0155", City = "Oslo", CountryCode = "NO",
        }));
        Assert.Equal("Oslo", PropertyVisuals.AddressText(new RealEstateDetailsDto { City = "Oslo" }));
        Assert.Null(PropertyVisuals.AddressText(new RealEstateDetailsDto()));
    }

    [Theory]
    [InlineData(" el 12345 ", "EL12345")]
    [InlineData("yv1 xz0", "YV1XZ0")]
    [InlineData(null, "")]
    public void NormalizePlate_strips_whitespace_and_uppercases_as_the_server_does(string? typed, string expected) =>
        Assert.Equal(expected, PropertyVisuals.NormalizePlate(typed));

    /// <summary>
    /// A one-click Archive rides the full-replacement PUT, so it must carry every field forward —
    /// and only the detail row matching the type, or the server's XOR rule would refuse it.
    /// </summary>
    [Fact]
    public void WithArchived_carries_the_record_forward_and_only_the_matching_detail_row()
    {
        var car = new ExistingProperty
        {
            PropertyId = Guid.NewGuid(), Name = "Outback", Description = "Family car", CurrencyCode = "USD",
            Type = PropertyType.Vehicle, Notes = "Winter tyres in the shed",
            AcquiredDate = new DateTime(2021, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            VehicleDetails = new VehicleDetailsDto { Kind = VehicleKind.Car, RegistrationNumber = "EL12345" },
            RealEstateDetails = new RealEstateDetailsDto { City = "stray" },
        };

        var body = PropertyWrites.WithArchived(car, archived: true);

        Assert.True(body.Archived);
        Assert.Equal(("Outback", "Family car", "USD", "Winter tyres in the shed"), (body.Name, body.Description, body.CurrencyCode, body.Notes));
        Assert.Equal(car.AcquiredDate, body.AcquiredDate);
        Assert.Equal("EL12345", body.VehicleDetails!.RegistrationNumber);
        Assert.Null(body.RealEstateDetails);
    }

    [Fact]
    public void The_nav_entry_sits_after_accounts_and_is_gated_on_properties_read()
    {
        var money = NavModel.All.Single(m => m.Key == "finance").Groups.Single(g => g.Label == "Money").Items;

        Assert.Equal(["accounts", "properties", "transactions", "budgets"], money.Select(p => p.Key));
        Assert.Equal(PermissionClaims.PropertiesRead, money.Single(p => p.Key == "properties").Claim);
    }

    [Fact]
    public async Task The_smart_tag_section_reads_the_property_endpoints_and_the_property_cap()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var propertyId = Guid.NewGuid();

        var properties = new Mock<IPropertiesApiClient>();
        properties.Setup(p => p.ListSmartTagsAsync(propertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingTransactionTag>>.Success([], HttpStatusCode.OK));
        var limits = new Mock<IPropertyLimitsCache>();
        limits.Setup(l => l.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PropertyLimits(1, IsDegraded: false));
        var contracts = new Mock<IContractsApiClient>();
        var accounts = new Mock<IAccountsApiClient>();
        var reference = new Mock<IReferenceDataCache>();
        reference.Setup(r => r.TransactionTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        ctx.Services.AddSingleton(properties.Object);
        ctx.Services.AddSingleton(limits.Object);
        ctx.Services.AddSingleton(contracts.Object);
        ctx.Services.AddSingleton(accounts.Object);
        ctx.Services.AddSingleton(reference.Object);
        ctx.Services.AddSingleton(Mock.Of<ITransactionsApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IContractLimitsCache>());
        ctx.Services.AddSingleton(Mock.Of<IAccountLimitsCache>());

        var cut = ctx.Render<AccountSmartTagsSection>(p => p
            .Add(s => s.Host, SmartTagHost.Property)
            .Add(s => s.SubjectId, propertyId)
            .Add(s => s.Chrome, false)
            .Add(s => s.CanWrite, true)
            .Add(s => s.FormatMoney, (decimal v, string? _) => v.ToString("0.00")));
        await cut.InvokeAsync(() => cut.Instance.ReloadAsync());

        properties.Verify(p => p.ListSmartTagsAsync(propertyId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        limits.Verify(l => l.GetAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        contracts.VerifyNoOtherCalls();
        accounts.VerifyNoOtherCalls();
        Assert.Contains("from this property", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The smooth curve passes through every entry and never overshoots a neighbouring pair — an
    /// estimate chart must not invent a peak or a dip the data does not have.
    /// </summary>
    [Fact]
    public void The_monotone_curve_hits_every_entry_and_never_overshoots()
    {
        (double X, double Y)[] entries = [(0, 100), (40, 20), (80, 60), (200, 60), (260, 10)];

        var curve = OdsStepChart.Monotone(entries);

        foreach (var entry in entries)
            Assert.Contains(curve, p => Math.Abs(p.X - entry.X) < 1e-9 && Math.Abs(p.Y - entry.Y) < 1e-9);

        for (var i = 0; i < entries.Length - 1; i++)
        {
            var (x0, y0) = entries[i];
            var (x1, y1) = entries[i + 1];
            var (lo, hi) = (Math.Min(y0, y1), Math.Max(y0, y1));
            Assert.All(curve.Where(p => p.X >= x0 && p.X <= x1), p => Assert.InRange(p.Y, lo - 1e-9, hi + 1e-9));
        }
    }

    [Fact]
    public void The_monotone_curve_leaves_two_entries_as_a_straight_segment()
    {
        (double X, double Y)[] entries = [(0, 0), (10, 5)];

        Assert.Equal(entries, OdsStepChart.Monotone(entries));
    }
}
