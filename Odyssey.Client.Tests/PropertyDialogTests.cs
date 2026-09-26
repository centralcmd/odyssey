using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The New / Edit property dialog (issue #167; design system · AddPropertyModal.jsx). The request it
/// builds carries exactly one detail sub-object — the one matching the type — so the server's XOR rule
/// holds by construction; the type is fixed once the property exists; and a one-field edit carries the
/// rest of the record forward, because PUT is a full replacement.
/// </summary>
public class PropertyDialogTests
{
    private static readonly Guid PropertyId = Guid.NewGuid();

    private static ExistingProperty Existing(bool archived = false, int? estimates = 0) => new()
    {
        PropertyId = PropertyId,
        Name = "Storgata 14",
        Description = "Primary residence",
        Type = PropertyType.RealEstate,
        CurrencyCode = "NOK",
        Archived = archived ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
        EstimateCount = estimates,
        RealEstateDetails = new RealEstateDetailsDto { Kind = RealEstateKind.Apartment, City = "Oslo", CountryCode = "NO" },
    };

    private static (IRenderedComponent<DialogHost> Cut, Mock<IPropertiesApiClient> Properties) Render(ExistingProperty? property = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var properties = new Mock<IPropertiesApiClient>();
        properties.Setup(p => p.CreateAsync(It.IsAny<NewProperty>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NewProperty body, CancellationToken _) => ApiResult<ExistingProperty>.Success(new ExistingProperty
            {
                PropertyId = Guid.NewGuid(), Name = body.Name, Description = body.Description, CurrencyCode = body.CurrencyCode,
            }, HttpStatusCode.Created));
        properties.Setup(p => p.UpdateAsync(It.IsAny<Guid>(), It.IsAny<NewProperty>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        ctx.Services.AddSingleton(properties.Object);
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        var preferences = new Mock<IUserPreferenceService>();
        preferences.SetupGet(p => p.DefaultCurrency).Returns("NOK");
        ctx.Services.AddSingleton(preferences.Object);

        var cut = ctx.Render<DialogHost>(p => p.Add(h => h.Property, property));
        return (cut, properties);
    }

    private static Task SetField(IRenderedComponent<DialogHost> cut, string label, string value)
    {
        var field = cut.FindComponents<OdsField>().Single(f => f.Instance.Label == label);
        return cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value));
    }

    private static void Submit(IRenderedComponent<DialogHost> cut, string label) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(label, StringComparison.Ordinal)).Click();

    [Fact]
    public void Create_offers_the_two_types_as_a_required_card_picker()
    {
        var (cut, _) = Render();

        Assert.Equal("true", cut.Find(".odc-cardsel").GetAttribute("aria-required"));
        Assert.Equal(["Real estate", "Vehicle"], cut.FindAll(".odc-cardsel-lab").Select(l => l.TextContent));
        Assert.Empty(cut.FindAll(".prop-type-locked"));
    }

    [Fact]
    public async Task Creating_real_estate_sends_only_the_real_estate_details()
    {
        var (cut, properties) = Render();
        await SetField(cut, "Name", "Storgata 14");
        await SetField(cut, "Description", "Primary residence");
        await SetField(cut, "City", "Oslo");

        Submit(cut, "Create property");

        cut.WaitForAssertion(() => properties.Verify(p => p.CreateAsync(
            It.Is<NewProperty>(b => b.Type == PropertyType.RealEstate && b.CurrencyCode == "NOK"
                && b.RealEstateDetails != null && b.RealEstateDetails.City == "Oslo" && b.VehicleDetails == null),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    /// <summary>
    /// Switching the type on create keeps both drafts, but only the matching one is sent — and the
    /// registration number goes out the way the server stores it.
    /// </summary>
    [Fact]
    public async Task Switching_to_vehicle_sends_only_the_vehicle_details_with_the_plate_normalized()
    {
        var (cut, properties) = Render();
        await SetField(cut, "City", "Oslo");
        cut.FindAll(".odc-cardsel-opt")[1].Click();
        await SetField(cut, "Name", "Family car");
        await SetField(cut, "Description", "Daily driver");
        await SetField(cut, "Registration number", " el 12345 ");

        Assert.Contains("Saved as EL12345", cut.Markup, StringComparison.Ordinal);

        Submit(cut, "Create property");

        cut.WaitForAssertion(() => properties.Verify(p => p.CreateAsync(
            It.Is<NewProperty>(b => b.Type == PropertyType.Vehicle && b.RealEstateDetails == null
                && b.VehicleDetails != null && b.VehicleDetails.RegistrationNumber == "EL12345"),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task A_missing_name_or_description_is_refused_on_the_field()
    {
        var (cut, properties) = Render();

        Submit(cut, "Create property");

        cut.WaitForAssertion(() => Assert.Contains("Give the property a name.", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Add a short description.", cut.Markup, StringComparison.Ordinal);
        properties.Verify(p => p.CreateAsync(It.IsAny<NewProperty>(), It.IsAny<CancellationToken>()), Times.Never);
        await Task.CompletedTask;
    }

    [Fact]
    public void Edit_shows_the_type_locked_and_offers_no_picker()
    {
        var (cut, _) = Render(Existing());

        Assert.Empty(cut.FindAll(".odc-cardsel"));
        var locked = cut.Find(".prop-type-locked");
        Assert.Contains("Real estate", locked.TextContent, StringComparison.Ordinal);
        Assert.Contains("delete this property and create a new one", locked.TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Archive / Restore is the row menu's, but it lands on the same full-replacement PUT — so an
    /// ordinary edit must carry the stored flag, or saving a name would silently restore the property.
    /// </summary>
    [Fact]
    public async Task An_edit_carries_the_archived_flag_and_the_detail_row_forward()
    {
        var (cut, properties) = Render(Existing(archived: true));
        await SetField(cut, "Name", "Storgata 14B");

        Submit(cut, "Save changes");

        cut.WaitForAssertion(() => properties.Verify(p => p.UpdateAsync(PropertyId,
            It.Is<NewProperty>(b => b.Name == "Storgata 14B" && b.Archived && b.Type == PropertyType.RealEstate
                && b.RealEstateDetails!.Kind == RealEstateKind.Apartment && b.RealEstateDetails.City == "Oslo"),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task Changing_the_currency_of_a_property_with_estimates_is_refused_before_the_round_trip()
    {
        var (cut, properties) = Render(Existing(estimates: 3));
        var currency = cut.FindComponent<OdsCurrencySelect>();
        await cut.InvokeAsync(() => currency.Instance.ValueChanged.InvokeAsync("SEK"));

        Assert.Contains("3 estimates are", cut.Markup, StringComparison.Ordinal);

        Submit(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Contains("can’t change while the property has estimates", cut.Markup, StringComparison.Ordinal));
        properties.Verify(p => p.UpdateAsync(It.IsAny<Guid>(), It.IsAny<NewProperty>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Disposed_before_acquired_is_refused_on_the_disposed_field()
    {
        var (cut, properties) = Render(Existing());
        var dates = cut.FindComponents<OdsDateField>();
        await cut.InvokeAsync(() => dates.Single(d => d.Instance.Label == "Acquired").Instance
            .ValueChanged.InvokeAsync(new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
        await cut.InvokeAsync(() => dates.Single(d => d.Instance.Label == "Disposed").Instance
            .ValueChanged.InvokeAsync(new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc)));

        Submit(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Contains("Disposed can’t be before Acquired.", cut.Markup, StringComparison.Ordinal));
        properties.Verify(p => p.UpdateAsync(It.IsAny<Guid>(), It.IsAny<NewProperty>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingProperty? Property { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<PropertyDialog>(2);
            builder.AddComponentParameter(3, nameof(PropertyDialog.Open), true);
            builder.AddComponentParameter(4, nameof(PropertyDialog.Property), Property);
            builder.CloseComponent();
        }
    }
}
