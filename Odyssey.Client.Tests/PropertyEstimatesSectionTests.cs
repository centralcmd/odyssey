using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A property's value history (issue #167; design system · PropertyEstimates.jsx): the Current value
/// band reads the entry in force today — the greatest EffectiveFrom on or before today, ties to the
/// newest created — beside its change from the previous entry and any scheduled one; the ledger marks
/// each row In force / Scheduled / Superseded; and the chart joins entries with a smooth curve.
/// </summary>
public class PropertyEstimatesSectionTests
{
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private static readonly ExistingProperty House = new()
    {
        PropertyId = PropertyId,
        Name = "Storgata 14",
        Description = "Primary residence",
        Type = PropertyType.RealEstate,
        CurrencyCode = "NOK",
    };

    private static ExistingPropertyEstimate Estimate(decimal value, DateTime from, DateTime? created = null, string? note = null) => new()
    {
        PropertyEstimateId = Guid.NewGuid(),
        PropertyId = PropertyId,
        Value = value,
        CurrencyCode = "NOK",
        EffectiveFrom = from,
        CreatedAtUtc = created ?? from,
        Note = note,
    };

    private static IRenderedComponent<PropertyEstimatesSection> Render(
        List<ExistingPropertyEstimate> estimates, bool canWrite = true, Guid? token = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var properties = new Mock<IPropertiesApiClient>();
        properties.Setup(p => p.ListEstimatesAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingPropertyEstimate>>.Success(estimates, HttpStatusCode.OK));
        ctx.Services.AddSingleton(properties.Object);
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton(Mock.Of<IAccountsApiClient>());

        var cut = ctx.Render<PropertyEstimatesSection>(p => p
            .Add(s => s.Property, House)
            .Add(s => s.CanWrite, canWrite)
            .Add(s => s.NewEstimateRequestToken, token)
            .Add(s => s.FormatMoney, (decimal v, string? c) => $"{v:0} {c}"));
        // OnInitializedAsync early-returns outside the browser; the public reload is the way in.
        cut.InvokeAsync(() => cut.Instance.LoadAsync()).GetAwaiter().GetResult();
        return cut;
    }

    [Fact]
    public void With_no_estimates_it_says_so_and_draws_no_chart()
    {
        var cut = Render([]);

        Assert.Contains("No estimate yet — record what Storgata 14 is worth", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindComponents<OdsTermHistoryChart>());
    }

    [Fact]
    public void The_current_band_reads_the_entry_in_force_its_change_and_the_scheduled_one()
    {
        var cut = Render(
        [
            Estimate(4_000_000m, Today.AddYears(-2)),
            Estimate(4_500_000m, Today.AddYears(-1)),
            Estimate(5_000_000m, Today.AddMonths(-1), note: "Broker valuation"),
            Estimate(5_400_000m, Today.AddMonths(3)),
        ]);

        var tiles = cut.FindComponents<OdsInfoTile>().Select(t => t.Instance.Label).ToList();
        Assert.Equal(["Estimated value", "Change vs previous", "Since first estimate", "Scheduled"], tiles);
        var markup = cut.Markup;
        Assert.Contains("5000000 NOK", markup, StringComparison.Ordinal);
        Assert.Contains("+500000 NOK", markup, StringComparison.Ordinal);
        Assert.Contains("+1000000 NOK", markup, StringComparison.Ordinal);
        Assert.Contains("5400000 NOK", markup, StringComparison.Ordinal);
        Assert.Contains("Broker valuation", markup, StringComparison.Ordinal);
    }

    /// <summary>Two entries on one date: the newer-created one is in force, the same rule as the server's.</summary>
    [Fact]
    public void A_same_day_tie_goes_to_the_newest_created()
    {
        var day = Today.AddDays(-10);
        var cut = Render([Estimate(100m, day, created: day.AddHours(1)), Estimate(200m, day, created: day.AddHours(2))]);

        var inForce = cut.FindAll("tr.current");
        Assert.Single(inForce);
        Assert.Contains("200 NOK", inForce[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("In force", inForce[0].TextContent, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll(".trm-superseded"), s => s.TextContent == "Superseded");
    }

    [Fact]
    public void Only_scheduled_entries_leave_nothing_in_force()
    {
        var cut = Render([Estimate(100m, Today.AddMonths(2))]);

        Assert.Contains("Nothing in force today", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll(".trm-scheduled"), s => s.TextContent == "Scheduled");
    }

    [Fact]
    public void The_chart_is_drawn_as_a_smooth_curve()
    {
        var cut = Render([Estimate(100m, Today.AddYears(-1)), Estimate(150m, Today.AddMonths(-1))]);

        Assert.Equal(OdsStepCurve.Smooth, cut.FindComponent<OdsTermHistoryChart>().Instance.Curve);
    }

    [Fact]
    public void A_reader_without_the_write_claim_gets_no_row_actions()
    {
        var cut = Render([Estimate(100m, Today.AddYears(-1))], canWrite: false);

        Assert.Empty(cut.FindComponents<OdsRowActions>());
        Assert.Empty(cut.FindAll("th.act"));
    }

    /// <summary>
    /// Expanding a card must not open the dialog: only a fresh, real token from the row menu does. An
    /// empty token is what a host's dictionary miss produces, so it is ignored like no token at all.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_a_real_token_opens_the_new_estimate_dialog(bool real)
    {
        var cut = Render([Estimate(100m, Today.AddYears(-1))], token: real ? Guid.NewGuid() : Guid.Empty);

        Assert.Equal(real, cut.FindComponents<AddEstimateDialog>().Count == 1);
    }
}
