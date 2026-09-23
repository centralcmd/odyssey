using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The frontend half of issue #159 — which way a contract term's money moves, on every surface that
/// reads or writes one.
///
/// <para>
/// Three things here are properties of the MARKUP rather than of a computation, so those render: that
/// the field's lead shows the direction's WORD where a sign would be and names both states in its
/// accessible name; that a receipt row's mint amount is not its only carrier of direction; and that
/// the breakdown tile's net row is ruled off rather than being another category. The rest are the
/// derivations the surfaces share, pinned once so the dialog's control, the read surfaces and the
/// refusal copy cannot drift apart.
/// </para>
/// </summary>
public class TermDirectionSurfaceTests
{
    static TermDirectionSurfaceTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static ExistingTerm Fee(
        TermDirection direction = TermDirection.Outgoing,
        bool onContract = true,
        TermKind kind = TermKind.Fee) => new()
        {
            TermId = Guid.NewGuid(),
            ContractId = onContract ? Guid.NewGuid() : null,
            AccountId = onContract ? null : Guid.NewGuid(),
            TermKind = kind,
            Label = kind == TermKind.Fee ? "Base salary" : null,
            ValueUnit = kind == TermKind.Fee ? TermValueUnit.Amount : TermValueUnit.Percentage,
            Value = 6200m,
            CurrencyCode = kind == TermKind.Fee ? "USD" : null,
            Direction = direction,
        };

    // ── The registry ─────────────────────────────────────────────────────────

    /// <summary>
    /// Outgoing leads, because it is the default AND the backfill value: a reader meeting the pair
    /// for the first time should meet the state every existing term already carries first.
    /// </summary>
    [Fact]
    public void The_registry_lists_outgoing_first_and_names_both_sides()
    {
        Assert.Equal([TermDirection.Outgoing, TermDirection.Incoming], TermDirectionVisuals.All);

        Assert.Equal("Outgoing", TermDirectionVisuals.Info(TermDirection.Outgoing).Label);
        Assert.Equal("Incoming", TermDirectionVisuals.Info(TermDirection.Incoming).Label);
        Assert.Equal("out", TermDirectionVisuals.Info(TermDirection.Outgoing).Short);
        Assert.Equal("in", TermDirectionVisuals.Info(TermDirection.Incoming).Short);
    }

    /// <summary>
    /// Coral out, mint in — the FINANCE semantics. The brand hues (tide, sea) never encode a
    /// direction, so a change that reached for one here would be reaching for the wrong palette.
    /// </summary>
    [Fact]
    public void Each_side_carries_its_own_finance_hue_and_never_a_brand_one()
    {
        Assert.Equal("expense", TermDirectionVisuals.Info(TermDirection.Outgoing).Tone);
        Assert.Equal("income", TermDirectionVisuals.Info(TermDirection.Incoming).Tone);
        Assert.Equal("var(--finance-expense)", TermDirectionVisuals.Info(TermDirection.Outgoing).Color);
        Assert.Equal("var(--finance-income)", TermDirectionVisuals.Info(TermDirection.Incoming).Color);
    }

    /// <summary>
    /// The lead carries a WORD and NO glyph. Every arrow reads against value rather than against the
    /// household — an up arrow says "gain" before it says "leaves here" — and the unambiguous pairs
    /// are emoji, which product chrome bars.
    /// </summary>
    [Fact]
    public void The_field_lead_is_a_word_with_no_icon()
    {
        var lead = TermDirectionVisuals.LeadOptions;

        Assert.Equal(2, lead.Count);
        Assert.All(lead, option =>
        {
            Assert.Null(option.Icon);
            Assert.False(string.IsNullOrWhiteSpace(option.Short));
        });
        Assert.Equal([nameof(TermDirection.Outgoing), nameof(TermDirection.Incoming)],
            lead.Select(o => o.Value));
    }

    /// <summary>
    /// A value from the wire that is not a member resolves to the DEFAULT rather than throwing or
    /// going blank — the same read-with-a-default rule the rest of the surface follows.
    /// </summary>
    [Theory]
    [InlineData("Incoming", TermDirection.Incoming)]
    [InlineData("Outgoing", TermDirection.Outgoing)]
    [InlineData("Sideways", TermDirection.Outgoing)]
    [InlineData("", TermDirection.Outgoing)]
    [InlineData(null, TermDirection.Outgoing)]
    public void An_unrecognised_direction_resolves_to_the_default(string? value, TermDirection expected) =>
        Assert.Equal(expected, TermDirectionVisuals.Parse(value));

    // ── Where direction means something ──────────────────────────────────────

    /// <summary>
    /// ONE predicate: any term on a CONTRACT, fee and rate alike — an arrears rate charges and a
    /// deposit rate pays, which is the same fact a fee carries. No account surface reads a direction,
    /// so the server refuses <c>Incoming</c> there with a 400 and it is not offered one. If this and
    /// the refusal copy could disagree, a user would meet a rejection the dialog never predicted.
    /// </summary>
    [Theory]
    [InlineData(TermKind.Fee, true, true)]
    [InlineData(TermKind.InterestRate, true, true)]
    [InlineData(TermKind.Fee, false, false)]
    [InlineData(TermKind.InterestRate, false, false)]
    [InlineData(TermKind.ExpectedReturn, false, false)]
    public void Direction_applies_to_every_contract_term_and_no_account_term(TermKind kind, bool onContract, bool applies)
    {
        Assert.Equal(applies, TermKindVisuals.DirectionApplies(kind, onContract));
        Assert.Equal(applies, TermKindVisuals.DirectionRefusal(kind, onContract) is null);
    }

    /// <summary>The owner is read off whichever id the row carries, not passed in beside it.</summary>
    [Fact]
    public void A_terms_owner_is_read_from_the_row_itself()
    {
        Assert.True(TermKindVisuals.DirectionApplies(Fee()));
        Assert.False(TermKindVisuals.DirectionApplies(Fee(onContract: false)));
    }

    /// <summary>
    /// The one refusal left names its reason — an account term has no direction at all — and a
    /// contract's rate is no longer refused: the retired "fee term only" copy must not survive.
    /// </summary>
    [Fact]
    public void The_only_refusal_is_the_account_one()
    {
        var account = TermKindVisuals.DirectionRefusal(TermKind.Fee, isContractOwned: false);

        Assert.Contains("account term", account, StringComparison.OrdinalIgnoreCase);
        Assert.Null(TermKindVisuals.DirectionRefusal(TermKind.InterestRate, isContractOwned: true));
    }

    /// <summary>
    /// Only an INCOMING term re-colours. Outgoing returns null so every surface keeps the colour it
    /// already had — nothing that existed before this field changes appearance, which is the visual
    /// half of the same compatibility promise the backend makes about the figures.
    /// </summary>
    [Fact]
    public void Only_an_incoming_contract_term_takes_the_income_hue()
    {
        Assert.Equal("var(--finance-income)", TermKindVisuals.DirectionColor(Fee(TermDirection.Incoming)));
        Assert.Null(TermKindVisuals.DirectionColor(Fee()));
        // An account term stores Outgoing and would not be re-coloured even if it did not.
        Assert.Null(TermKindVisuals.DirectionColor(Fee(TermDirection.Incoming, onContract: false)));
        // A contract rate carries a direction too, so an incoming one — a deposit rate — is mint.
        Assert.Equal("var(--finance-income)", TermKindVisuals.DirectionColor(Fee(TermDirection.Incoming, kind: TermKind.InterestRate)));
    }

    // ── The field lead ───────────────────────────────────────────────────────

    /// <summary>
    /// The lead shows the direction's short WORD in the slot a sign would occupy, and its accessible
    /// name says what it is AND what clicking does — a button whose label is only its current state
    /// leaves a screen-reader user to guess the consequence.
    /// </summary>
    [Fact]
    public void The_money_field_lead_shows_the_word_and_names_both_states()
    {
        using var ctx = NewContext();
        var field = ctx.Render<OdsMoneyField>(p => p
            .Add(f => f.Value, "6200")
            .Add(f => f.Currency, "USD")
            .Add(f => f.Direction, nameof(TermDirection.Incoming))
            .Add(f => f.DirectionOptions, TermDirectionVisuals.LeadOptions)
            .Add(f => f.DirectionChanged, (string _) => { }));

        var lead = field.Find("button.odc-money-sign");
        Assert.Equal("in", lead.QuerySelector(".odc-money-dir-word")!.TextContent.Trim());
        Assert.Null(lead.QuerySelector(".odc-money-dir-sign"));

        var name = lead.GetAttribute("aria-label")!;
        Assert.Contains("Incoming", name, StringComparison.Ordinal);
        Assert.Contains("switch to outgoing", name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Clicking the lead reports the OTHER state — the pair is a toggle, not a cycle of one.</summary>
    [Fact]
    public void Clicking_the_lead_flips_to_the_other_state()
    {
        using var ctx = NewContext();
        string? reported = null;
        var field = ctx.Render<OdsMoneyField>(p => p
            .Add(f => f.Value, "6200")
            .Add(f => f.Direction, nameof(TermDirection.Outgoing))
            .Add(f => f.DirectionOptions, TermDirectionVisuals.LeadOptions)
            .Add(f => f.DirectionChanged, (string next) => reported = next));

        field.Find("button.odc-money-sign").Click();

        Assert.Equal(nameof(TermDirection.Incoming), reported);
    }

    /// <summary>
    /// The tone comes from the OPTION, not from the direction's name: a vocabulary whose values are
    /// "Outgoing"/"Incoming" would otherwise emit <c>tone-Outgoing</c>, which matches no rule and
    /// leaves the figure the wrong colour with nothing failing.
    /// </summary>
    [Fact]
    public void The_lead_tints_from_the_options_tone_not_its_value()
    {
        using var ctx = NewContext();
        var field = ctx.Render<OdsMoneyField>(p => p
            .Add(f => f.Value, "6200")
            .Add(f => f.Direction, nameof(TermDirection.Incoming))
            .Add(f => f.DirectionOptions, TermDirectionVisuals.LeadOptions)
            .Add(f => f.DirectionChanged, (string _) => { }));

        var box = field.Find("div.odc-money");
        Assert.Contains("tone-income", box.GetAttribute("class")!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A percentage FEE is money too, so the amount field carries the SAME lead — the two units are
    /// one control with one unit swapped, not two differently-shaped questions.
    /// </summary>
    [Fact]
    public void The_amount_field_carries_the_same_lead()
    {
        using var ctx = NewContext();
        var field = ctx.Render<OdsAmountField>(p => p
            .Add(f => f.Value, "2.5")
            .Add(f => f.Suffix, "%")
            .Add(f => f.Direction, nameof(TermDirection.Incoming))
            .Add(f => f.DirectionOptions, TermDirectionVisuals.LeadOptions)
            .Add(f => f.DirectionChanged, (string _) => { }));

        Assert.Equal("in", field.Find(".odc-money-dir-word").TextContent.Trim());
        Assert.Contains("tone-income", field.Find("div.odc-amount").GetAttribute("class")!, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no direction supplied there is no lead at all — which is how a rate kind and an account
    /// term get the refusal in its place rather than a control they would be refused for using.
    /// </summary>
    [Fact]
    public void No_direction_means_no_lead()
    {
        using var ctx = NewContext();
        var field = ctx.Render<OdsAmountField>(p => p
            .Add(f => f.Value, "2.5")
            .Add(f => f.DirectionOptions, TermDirectionVisuals.LeadOptions)
            .Add(f => f.DirectionChanged, (string _) => { }));

        Assert.Empty(field.FindAll("button.odc-money-sign"));
    }

    // ── The movement rows ────────────────────────────────────────────────────

    /// <summary>
    /// A receipt row is the same shape as a charge — the GROUP names the direction — but its amount
    /// is mint, and its accessible sentence says "expected" rather than "due". The colour must never
    /// be the only carrier (WCAG 1.4.1), and a screen reader never sees the group heading's
    /// relationship to the row.
    /// </summary>
    [Fact]
    public void A_receipt_row_tints_its_amount_and_words_its_own_sentence()
    {
        using var ctx = NewContext();
        var charge = new ContractUpcomingCharge
        {
            ContractId = Guid.NewGuid(),
            Name = "Employment Agreement",
            Type = ContractType.Employment,
            Label = "Base salary",
            Amount = 6200m,
            CurrencyCode = "USD",
            Interval = Interval.Monthly,
            IntervalCount = 1,
            ChargeDate = new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc),
            DaysUntil = 10,
        };

        var receipt = ctx.Render<ContractChargeRow>(p => p
            .Add(r => r.Charge, charge)
            .Add(r => r.Incoming, true)
            .Add(r => r.Money, (decimal v, string? c) => $"{v:0.00} {c}"));

        Assert.NotNull(receipt.Find(".con-charge-amt.in"));
        Assert.Contains("expected", receipt.Find("button").GetAttribute("aria-label")!, StringComparison.Ordinal);

        var outgoing = ctx.Render<ContractChargeRow>(p => p
            .Add(r => r.Charge, charge)
            .Add(r => r.Money, (decimal v, string? c) => $"{v:0.00} {c}"));

        Assert.Empty(outgoing.FindAll(".con-charge-amt.in"));
        Assert.Contains("due", outgoing.Find("button").GetAttribute("aria-label")!, StringComparison.Ordinal);
    }

    // ── The breakdown tile's total ───────────────────────────────────────────

    /// <summary>
    /// An explicit total renders as the tile's ruled conclusion, labelled by the caller — "Net" where
    /// the figure is a net rather than a sum.
    /// </summary>
    [Fact]
    public void An_explicit_total_renders_as_the_tiles_ruled_last_row()
    {
        using var ctx = NewContext();
        var tile = ctx.Render<OdsBreakdownTile>(p => p
            .Add(t => t.Label, "Monthly net by type")
            .Add(t => t.Rows, [new OdsBreakdownRow { Label = "Employment", Count = "x" }])
            .Add(t => t.TotalValue, "+ 5,778.00 USD")
            .Add(t => t.TotalLabel, "Net"));

        var total = tile.Find(".odc-breakdown-total");
        Assert.Equal("Net", total.QuerySelector(".odc-breakdown-label")!.TextContent.Trim());
        Assert.Equal("+ 5,778.00 USD", total.QuerySelector(".odc-breakdown-n")!.TextContent.Trim());
    }

    /// <summary>
    /// Counts that are NODES cannot be summed, so the row is omitted rather than guessed — a wrong
    /// total on a money tile is worse than no total at all.
    /// </summary>
    [Fact]
    public void Node_counts_produce_no_summed_total()
    {
        using var ctx = NewContext();
        var tile = ctx.Render<OdsBreakdownTile>(p => p
            .Add(t => t.Total, true)
            .Add(t => t.Rows,
            [
                new OdsBreakdownRow { Label = "Employment", Count = (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "x")) },
            ]));

        Assert.Empty(tile.FindAll(".odc-breakdown-total"));
    }

    /// <summary>
    /// A node count is RENDERED, not stringified. <c>OdsBreakdownRow.Count</c> is an <c>object</c>, so
    /// a bare binding picks the object overload and a render fragment reaches the screen as the
    /// literal text "Microsoft.AspNetCore.Components.RenderFragment" — which is what the contracts
    /// header's two money tiles did, on every row and on the total.
    /// </summary>
    [Fact]
    public void A_node_count_is_rendered_rather_than_stringified()
    {
        using var ctx = NewContext();
        var tile = ctx.Render<OdsBreakdownTile>(p => p
            .Add(t => t.TotalValue,
                (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "net")))
            .Add(t => t.Rows,
            [
                new OdsBreakdownRow
                {
                    Label = "Employment",
                    Count = (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "+ 6,200.00 USD")),
                },
            ]));

        Assert.Equal("+ 6,200.00 USD", tile.Find(".odc-breakdown-row:not(.odc-breakdown-total) .odc-breakdown-n").TextContent.Trim());
        Assert.Equal("net", tile.Find(".odc-breakdown-total .odc-breakdown-n").TextContent.Trim());
        Assert.DoesNotContain("RenderFragment", tile.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Numeric counts DO sum, and read as whole counts rather than acquiring a decimal tail.
    /// </summary>
    [Fact]
    public void Numeric_counts_sum_into_a_whole_total()
    {
        using var ctx = NewContext();
        var tile = ctx.Render<OdsBreakdownTile>(p => p
            .Add(t => t.Total, true)
            .Add(t => t.Rows,
            [
                new OdsBreakdownRow { Label = "Employment", Count = 2 },
                new OdsBreakdownRow { Label = "Rental", Count = 3 },
            ]));

        Assert.Equal("5", tile.Find(".odc-breakdown-total .odc-breakdown-n").TextContent.Trim());
    }

    /// <summary>
    /// The total is ON by default, matching the design system: a distribution whose sum a reader has
    /// to add up in their head is a table, not a summary. Pinned because the default is a one-word
    /// change with an app-wide blast radius, in both directions.
    /// </summary>
    [Fact]
    public void A_tile_that_says_nothing_about_a_total_renders_one()
    {
        using var ctx = NewContext();
        var tile = ctx.Render<OdsBreakdownTile>(p => p
            .Add(t => t.Rows,
            [
                new OdsBreakdownRow { Label = "Employment", Count = 2 },
                new OdsBreakdownRow { Label = "Rental", Count = 3 },
            ]));

        Assert.Equal("5", tile.Find(".odc-breakdown-total .odc-breakdown-n").TextContent.Trim());
    }

    /// <summary>
    /// Total="false" is the opt-out, for a distribution whose sum means nothing — overlapping
    /// buckets, a slice of another row, or rows that omit a bucket the data can hold.
    /// </summary>
    [Fact]
    public void A_tile_that_asks_for_no_total_renders_none()
    {
        using var ctx = NewContext();
        var tile = ctx.Render<OdsBreakdownTile>(p => p
            .Add(t => t.Total, false)
            .Add(t => t.Rows,
            [
                new OdsBreakdownRow { Label = "Employment", Count = 2 },
                new OdsBreakdownRow { Label = "Rental", Count = 3 },
            ]));

        Assert.Empty(tile.FindAll(".odc-breakdown-total"));
    }
}
