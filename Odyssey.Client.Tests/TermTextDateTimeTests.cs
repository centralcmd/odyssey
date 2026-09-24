using System.Globalization;
using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
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
/// The client half of the two non-numeric term kinds (issue #192): <c>TermVisuals</c> is exhaustive
/// over the four units and never renders a Text or DateTime term as money or a percentage, the chart
/// never plots a null value as 0, the Current-terms tiles render the fact itself, the dialog writes
/// exactly one value field, and the <c>TermChanged</c> event reads "Term changed".
/// </summary>
public class TermTextDateTimeTests
{
    private static readonly Guid ContractId = Guid.NewGuid();
    private static readonly DateTime Instant = new(2027, 3, 31, 10, 0, 0, DateTimeKind.Utc);
    private const string Notice = "3 months, to the end of a month";

    private static string Money(decimal value, string? currency) =>
        "$" + value.ToString("#,##0.00", CultureInfo.InvariantCulture) + " " + currency;

    // ── TermVisuals ───────────────────────────────────────────────────────────

    [Fact]
    public void A_text_term_formats_as_its_text_with_no_currency_or_percent()
    {
        var formatted = TermVisuals.FormatValue(Text("Notice period", Notice), Money);

        Assert.Equal(Notice, formatted);
        Assert.DoesNotContain("$", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("%", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_time_term_formats_in_the_viewers_zone_with_its_offset_named()
    {
        var berlin = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

        Assert.Equal("31 Mar 2027, 12:00 UTC+02:00", TermVisuals.FormatDateTime(Instant, berlin));
        Assert.Equal("2027-03-31 10:00 UTC", TermVisuals.UtcStamp(Instant));
        Assert.DoesNotContain("$", TermVisuals.FormatValue(DateTimeTerm("Deadline", Instant), Money), StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_numeric_value_and_an_unknown_unit_read_as_a_dash_never_zero()
    {
        var nullAmount = Amount("Rent", 1m, DateTime.UtcNow.Date);
        nullAmount.Value = null;
        var unknown = Text("Future", "x");
        unknown.ValueUnit = (TermValueUnit)99;

        Assert.Equal(TermVisuals.NoValue, TermVisuals.FormatValue(nullAmount, Money));
        Assert.Equal(TermVisuals.NoValue, TermVisuals.FormatValue(unknown, Money));
        Assert.Equal("help_outline", TermVisuals.UnitInfo((TermValueUnit)99).Icon);
    }

    [Fact]
    public void Every_unit_has_its_own_glyph_and_neither_fact_kind_borrows_the_money_one()
    {
        var icons = TermVisuals.AllUnits.Select(u => TermVisuals.UnitInfo(u).Icon).ToList();

        Assert.Equal(4, icons.Distinct().Count());
        Assert.NotEqual("payments", TermVisuals.UnitInfo(TermValueUnit.Text).Icon);
        Assert.NotEqual("payments", TermVisuals.UnitInfo(TermValueUnit.DateTime).Icon);
        Assert.Equal(Enum.GetValues<TermValueUnit>().Order(), TermVisuals.AllUnits.Order());
    }

    [Fact]
    public void A_fact_term_is_never_incoming_and_states_no_direction()
    {
        var text = Text("Notice period", Notice);
        text.Direction = TermDirection.Incoming;

        Assert.False(TermVisuals.IsIncoming(text));
        Assert.False(TermVisuals.HasDirection(text));
    }

    [Fact]
    public void Local_to_utc_round_trips_through_the_viewers_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test-5", TimeSpan.FromHours(-5), "Test-5", "Test-5");

        var utc = TermVisuals.LocalToUtc(new DateTime(2027, 3, 31), new TimeSpan(5, 0, 0), zone);

        Assert.Equal(Instant, utc);
        Assert.Equal(DateTimeKind.Utc, utc!.Value.Kind);
        Assert.Null(TermVisuals.LocalToUtc(new DateTime(2027, 3, 31), null, zone));
    }

    // ── The chart ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A series whose in-force entry became Text leaves the chart entirely, and a numeric series whose
    /// history holds a Text entry plots only its numbers — never a 0 point for the text.
    /// </summary>
    [Fact]
    public void The_chart_skips_fact_series_and_never_plots_a_fact_entry_as_zero()
    {
        var today = DateTime.UtcNow.Date;
        var terms = new List<ExistingTerm>
        {
            Amount("Service charge", 40m, today.AddDays(-300)),
            Text("Service charge", "Included in the rent", today.AddDays(-30)),
            Amount("Monthly rent", 1800m, today.AddDays(-400)),
            Text("Monthly rent", "Under review", today.AddDays(-200)),
            Amount("Monthly rent", 1850m, today.AddDays(-100)),
            Text("Notice period", Notice, today.AddDays(-400)),
            DateTimeTerm("Break deadline", Instant, today.AddDays(-400)),
        };

        var series = TermChartSeries.Build(terms, today, Money);

        var only = Assert.Single(series);
        Assert.Equal("Monthly rent", only.Label);
        Assert.Equal([1800m, 1850m], only.Points.Select(p => p.Value));
        Assert.DoesNotContain(only.Points, p => p.Value == 0m);
    }

    // ── The Current-terms tiles and history rows ──────────────────────────────

    [Fact]
    public void Tiles_render_the_fact_itself_after_the_priced_terms_without_a_direction()
    {
        var today = DateTime.UtcNow.Date;
        var cut = RenderSection(
        [
            Text("Notice period", Notice, today.AddDays(-10)),
            DateTimeTerm("Break deadline", Instant, today.AddDays(-10)),
            Amount("Monthly rent", 1850m, today.AddDays(-10)),
        ]);

        var tiles = cut.FindAll(".odc-infotile");
        Assert.Equal(3, tiles.Count);
        Assert.Contains("Monthly rent", tiles[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Break deadline", tiles[1].TextContent, StringComparison.Ordinal);
        Assert.Contains(Notice, tiles[2].TextContent, StringComparison.Ordinal);

        Assert.DoesNotContain("$", tiles[1].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("$", tiles[2].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Outgoing", tiles[2].TextContent, StringComparison.Ordinal);
        Assert.Equal("2027-03-31 10:00 UTC", tiles[1].QuerySelector("[title]")!.GetAttribute("title"));
    }

    [Fact]
    public void A_history_row_for_a_fact_carries_no_cadence_or_direction_word()
    {
        var today = DateTime.UtcNow.Date;
        var cut = RenderSection([Text("Notice period", Notice, today.AddDays(-10))]);

        var row = cut.Find(".trm-tbl tbody tr");
        Assert.Contains(Notice, row.QuerySelector(".trm-cell-value.trm-v-text")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(row.QuerySelectorAll(".trm-dir"));
    }

    [Fact]
    public void A_contract_with_only_fact_terms_renders_no_chart()
    {
        var cut = RenderSection([Text("Notice period", Notice, DateTime.UtcNow.Date.AddDays(-10))]);

        Assert.Empty(cut.FindAll(".trm-seriesplot"));
    }

    // ── The dialog ────────────────────────────────────────────────────────────

    [Fact]
    public void The_kind_picker_offers_all_four_kinds()
    {
        var (cut, _) = RenderDialog();

        var kinds = cut.FindAll(".trm-kind-seg [role=radio]").Select(b => b.TextContent.Trim()).ToList();

        Assert.Equal(4, kinds.Count);
        Assert.Contains(kinds, k => k.EndsWith("Text", StringComparison.Ordinal));
        Assert.Contains(kinds, k => k.EndsWith("Date & time", StringComparison.Ordinal));
    }

    [Fact]
    public void A_text_term_posts_the_trimmed_text_and_nothing_that_does_not_apply()
    {
        var (cut, client) = RenderDialog();
        PickKind(cut, "Text");
        TypeName(cut, "Notice period");
        TypeText(cut, $"  {Notice}  ");

        Click(cut, "Create term");

        client.Verify(c => c.AddTermAsync(ContractId, It.Is<NewTerm>(t =>
            t.ValueUnit == TermValueUnit.Text
            && t.TextValue == Notice
            && t.Value == null && t.DateTimeValue == null
            && t.CurrencyCode == null && t.Interval == null && t.IntervalCount == null && t.AnchorDate == null
            && t.Direction == TermDirection.Outgoing), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("line\tbreak")]
    [InlineData("hidden‮mark")]
    public void A_forbidden_character_is_refused_inline_without_echoing_the_text(string text)
    {
        var (cut, client) = RenderDialog();
        PickKind(cut, "Text");
        TypeName(cut, "Notice period");
        TypeText(cut, text);

        Assert.Contains("Remove tabs, line breaks and hidden direction marks.", cut.Markup, StringComparison.Ordinal);
        Click(cut, "Create term");
        client.Verify(c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Editing_a_date_time_term_re_saves_the_same_utc_instant()
    {
        var existing = DateTimeTerm("Break deadline", Instant, DateTime.UtcNow.Date.AddDays(-10));
        var (cut, client) = RenderDialog(existing, [existing]);

        Assert.Contains("Saved as", cut.Markup, StringComparison.Ordinal);
        Click(cut, "Save changes");

        client.Verify(c => c.UpdateTermAsync(ContractId, existing.TermId, It.Is<NewTerm>(t =>
            t.ValueUnit == TermValueUnit.DateTime
            && t.DateTimeValue == Instant && t.DateTimeValue!.Value.Kind == DateTimeKind.Utc
            && t.Value == null && t.TextValue == null && t.CurrencyCode == null && t.Interval == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Changing_an_amount_to_text_on_edit_says_what_is_removed()
    {
        var existing = Amount("Service charge", 40m, DateTime.UtcNow.Date.AddDays(-10));
        var (cut, _) = RenderDialog(existing, [existing]);

        PickKind(cut, "Text");

        var note = cut.Find(".trm-kind-switch").TextContent;
        Assert.Contains("from amount to text", note, StringComparison.Ordinal);
        Assert.Contains("value, direction, currency and cadence are removed", note, StringComparison.Ordinal);
    }

    // ── The TermChanged rename (Goal 10) ──────────────────────────────────────

    [Fact]
    public void Ordinal_six_reads_as_term_changed_never_other()
    {
        Assert.Equal(6, (int)ContractEventType.TermChanged);
        Assert.DoesNotContain("PriceChanged", Enum.GetNames<ContractEventType>());

        var info = OdsTypeRegistries.ContractEventTypeOf((ContractEventType)6);
        Assert.Equal("TermChanged", info.Key);
        Assert.Equal("Term changed", info.Label);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ExistingTerm Amount(string label, decimal value, DateTime effectiveFrom) => new()
    {
        TermId = Guid.NewGuid(),
        ContractId = ContractId,
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = "USD",
        Interval = Interval.Monthly,
        IntervalCount = 1,
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = effectiveFrom,
    };

    private static ExistingTerm Text(string label, string text, DateTime? effectiveFrom = null) => new()
    {
        TermId = Guid.NewGuid(),
        ContractId = ContractId,
        Label = label,
        ValueUnit = TermValueUnit.Text,
        TextValue = text,
        EffectiveFrom = effectiveFrom ?? DateTime.UtcNow.Date,
        CreatedAtUtc = effectiveFrom ?? DateTime.UtcNow.Date,
    };

    private static ExistingTerm DateTimeTerm(string label, DateTime instant, DateTime? effectiveFrom = null) => new()
    {
        TermId = Guid.NewGuid(),
        ContractId = ContractId,
        Label = label,
        ValueUnit = TermValueUnit.DateTime,
        DateTimeValue = instant,
        EffectiveFrom = effectiveFrom ?? DateTime.UtcNow.Date,
        CreatedAtUtc = effectiveFrom ?? DateTime.UtcNow.Date,
    };

    private static ExistingContract Lease() => new()
    {
        ContractId = ContractId,
        Name = "Maple St lease",
        Type = ContractType.Rental,
        StartDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static IRenderedComponent<ContractTermsSection> RenderSection(IReadOnlyList<ExistingTerm> terms)
    {
        var ctx = NewContext();
        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.ListTermsAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingTerm>>.Success([.. terms], HttpStatusCode.OK));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<ContractTermsSection>(p => p
            .Add(s => s.Contract, Lease())
            .Add(s => s.CanWrite, true)
            .Add(s => s.FormatMoney, (Func<decimal, string?, string>)Money));

        // The section loads on OnInitializedAsync, which early-returns outside the browser, so the load
        // is driven through its public reload — the entry point the host uses.
        cut.InvokeAsync(() => cut.Instance.ReloadAsync()).GetAwaiter().GetResult();
        return cut;
    }

    private static (IRenderedComponent<TermSeriesSurfaceTests.DialogHost> Cut, Mock<IContractsApiClient> Client) RenderDialog(
        ExistingTerm? editing = null, IReadOnlyList<ExistingTerm>? existing = null)
    {
        var ctx = NewContext();
        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        client
            .Setup(c => c.UpdateTermAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<TermSeriesSurfaceTests.DialogHost>(p => p
            .Add(h => h.Contract, Lease())
            .Add(h => h.Existing, existing ?? [])
            .Add(h => h.Term, editing));
        return (cut, client);
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        return ctx;
    }

    private static void PickKind(IRenderedComponent<TermSeriesSurfaceTests.DialogHost> cut, string label) =>
        cut.InvokeAsync(() => cut.FindAll(".trm-kind-seg [role=radio]")
            .Single(b => b.TextContent.Trim().EndsWith(label, StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs())).GetAwaiter().GetResult();

    private static void TypeName(IRenderedComponent<TermSeriesSurfaceTests.DialogHost> cut, string name)
    {
        cut.InvokeAsync(() => cut.Find("#trm-label").InputAsync(new ChangeEventArgs { Value = name })).GetAwaiter().GetResult();
        cut.InvokeAsync(() => cut.Find("#trm-label").FocusOutAsync(new FocusEventArgs())).GetAwaiter().GetResult();
    }

    private static void TypeText(IRenderedComponent<TermSeriesSurfaceTests.DialogHost> cut, string text) =>
        cut.InvokeAsync(() => cut.Find("#trm-text").InputAsync(new ChangeEventArgs { Value = text })).GetAwaiter().GetResult();

    private static void Click(IRenderedComponent<TermSeriesSurfaceTests.DialogHost> cut, string text) =>
        cut.InvokeAsync(() => cut.FindAll("button")
            .Single(b => b.TextContent.Contains(text, StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs())).GetAwaiter().GetResult();
}
