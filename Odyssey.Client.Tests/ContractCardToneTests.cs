using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The contract record card's derived tone: the headline figure and the header mark read one colour,
/// taken from the headline's own urgency where it has one and from the status chip otherwise.
/// </summary>
public class ContractCardToneTests
{
    [Theory]
    [InlineData(ContractStatus.Active, OdsRecordFigureTone.Income)]
    [InlineData(ContractStatus.Upcoming, OdsRecordFigureTone.Info)]
    [InlineData(ContractStatus.Expired, OdsRecordFigureTone.Expense)]
    [InlineData(ContractStatus.Paused, OdsRecordFigureTone.Pending)]
    [InlineData(ContractStatus.Ready, OdsRecordFigureTone.Pending)]
    // An outline status has no hue of its own, so it reads muted rather than in the default ink.
    [InlineData(ContractStatus.Draft, OdsRecordFigureTone.Muted)]
    [InlineData(ContractStatus.Archived, OdsRecordFigureTone.Muted)]
    public void Without_a_headline_urgency_the_figure_takes_the_status_tone(ContractStatus status, OdsRecordFigureTone expected)
    {
        Assert.Equal(expected, ContractCardTone.Headline("", status));
    }

    [Theory]
    [InlineData("expired", OdsRecordFigureTone.Expense)]
    [InlineData("soon", OdsRecordFigureTone.Pending)]
    [InlineData("paused", OdsRecordFigureTone.Pending)]
    public void A_headline_urgency_outranks_the_status_tone(string cls, OdsRecordFigureTone expected)
    {
        // Active would otherwise read income: the headline's own reading wins.
        Assert.Equal(expected, ContractCardTone.Headline(cls, ContractStatus.Active));
    }

    [Fact]
    public void An_unknown_status_falls_back_to_muted()
    {
        Assert.Equal(OdsRecordFigureTone.Muted, ContractCardTone.Headline("", (ContractStatus)999));
    }

    [Theory]
    [InlineData(OdsRecordFigureTone.Income, false, "con-card con-tone-income")]
    [InlineData(OdsRecordFigureTone.Info, false, "con-card con-tone-info")]
    [InlineData(OdsRecordFigureTone.Muted, true, "con-card con-tone-muted con-unsigned")]
    [InlineData(OdsRecordFigureTone.Pending, true, "con-card con-tone-pending con-unsigned")]
    public void The_card_class_carries_the_tone_and_the_unsigned_modifier(OdsRecordFigureTone tone, bool unsigned, string expected)
    {
        Assert.Equal(expected, ContractCardTone.CardClass(tone, unsigned));
    }

    [Fact]
    public void Every_hued_card_tone_has_a_stylesheet_rule()
    {
        // Muted is the base rule's default, so it needs no modifier of its own.
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));
        foreach (var tone in new[] { "income", "expense", "pending", "info" })
            Assert.Matches(new Regex(@"\.odc-record\.con-card\.con-tone-" + tone + @"\s*\{\s*--con-tone:"), css);
        Assert.Matches(new Regex(@"\.odc-record\.con-card\s*\{\s*--con-tone:\s*var\(--mud-palette-text-secondary\)"), css);
    }

    [Theory]
    [InlineData(ContractStatus.Active, OdsInfoTileTone.Income)]
    [InlineData(ContractStatus.Paused, OdsInfoTileTone.Income)]
    [InlineData(ContractStatus.Upcoming, OdsInfoTileTone.Income)]
    // A signature on an agreement that has ended is history, not good news.
    [InlineData(ContractStatus.Expired, OdsInfoTileTone.Default)]
    [InlineData(ContractStatus.Archived, OdsInfoTileTone.Default)]
    public void The_signed_tile_drops_its_tone_once_the_contract_has_ended(ContractStatus status, OdsInfoTileTone expected)
    {
        Assert.Equal(expected, ContractCardTone.Signed(status));
    }

    [Fact]
    public void The_card_reads_its_tones_from_the_helper()
    {
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "ContractsCard.razor"));
        Assert.Contains("ContractCardTone.Headline(headline.Cls, c.Status)", markup);
        Assert.Contains("FigureTone=\"@figureTone\"", markup);
        Assert.Contains("Tone=\"@ContractCardTone.Signed(detail.Status)\"", markup);
    }

    [Theory]
    [InlineData(OdsRecordFigureTone.Info, "info")]
    [InlineData(OdsRecordFigureTone.Muted, "muted")]
    public void The_new_figure_tones_reach_the_rendered_figure(OdsRecordFigureTone tone, string cssClass)
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var cut = ctx.Render<OdsRecordCard>(p => p
            .Add(c => c.Name, "Record")
            .Add(c => c.FigureTone, tone)
            .Add(c => c.Figure, b => b.AddContent(0, "Dec 19, 2026")));

        Assert.Contains(cssClass, cut.Find(".odc-record-value").ClassList);
    }
}
