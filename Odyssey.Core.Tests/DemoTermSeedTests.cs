using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.TestData.Catalog;
using Odyssey.TestData.Generators;
using Xunit;
using DtoInterval = Odyssey.Dtos.Finance.Interval;
using DtoTermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using NewTerm = Odyssey.Dtos.Finance.NewTerm;

namespace Odyssey.Core.Tests;

/// <summary>
/// The demo term seed is a set <see cref="TermService"/> itself would accept.
///
/// <para>
/// <c>DemoDataSeeder</c> writes <c>Term</c> entities straight to the context, so it bypasses
/// every rule the write path enforces — a seeded fee with no label, a rate carrying one, or a
/// duplicate series entry would all persist happily and only fail the first time a user edited the
/// row. The generator's own doc-comment claims the shape "is one the service itself would accept";
/// this replays every seeded term through the real service and holds it to that claim.
/// </para>
///
/// <para>
/// It also covers the <c>Fee</c> + <c>Percentage</c> combination specifically, which the seed uses
/// for the card's currency-conversion markup and the investment platform fees, and which no other
/// test exercises through the service.
/// </para>
/// </summary>
public class DemoTermSeedTests
{
    [Fact]
    public async Task Every_seeded_term_is_accepted_by_the_write_path()
    {
        await using var context = await SeededAccountsAsync();
        var service = new TermService(context);

        foreach (var term in TermGenerator.Build())
        {
            // Throws DomainValidationException / DomainConflictException if the seed ever drifts from
            // what the service permits — eligibility, unit, currency, the label rules, or the series
            // duplicate guard.
            await service.Create(term.AccountId!.Value, ToRequest(term));
        }
    }

    [Fact]
    public async Task The_seed_exercises_a_percentage_term_which_the_service_accepts()
    {
        await using var context = await SeededAccountsAsync();
        var service = new TermService(context);

        var percentageFees = TermGenerator.Build()
            .Where(t => t.ValueUnit == TermValueUnit.Percentage)
            .ToList();

        Assert.NotEmpty(percentageFees);

        foreach (var fee in percentageFees)
        {
            var created = await service.Create(fee.AccountId!.Value, ToRequest(fee));

            // A percentage carries no currency, and a term always carries its name.
            Assert.Null(created.CurrencyCode);
            Assert.False(string.IsNullOrWhiteSpace(created.Label));
        }
    }

    [Fact]
    public void Every_seeded_term_is_named() =>
        Assert.All(TermGenerator.Build(), term => Assert.False(string.IsNullOrWhiteSpace(term.Label)));

    [Fact]
    public void No_two_seeded_terms_share_a_series_and_a_date()
    {
        // The duplicate guard would reject the second on a real write, and the deterministic id would
        // collide before that — the seed must not contain one.
        var terms = TermGenerator.Build();

        var duplicates = terms
            .GroupBy(t => (t.AccountId, t.LabelKey, t.EffectiveFrom))
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_seeded_term_has_a_distinct_id()
    {
        // Two terms on one account can share a date, so the label is part of the seed
        // key; dropping it would hand both the same deterministic id.
        var terms = TermGenerator.Build();

        Assert.Equal(terms.Count, terms.Select(t => t.TermId).Distinct().Count());
    }

    /// <summary>A context holding the demo portfolio, so a term can be written against the account it
    /// names — the service resolves eligibility and the default currency from it.</summary>
    private static async Task<OdysseyContext> SeededAccountsAsync()
    {
        var context = TestContextFactory.Create();

        var currencies = await context.Currencies.Select(c => c.CurrencyCode).ToListAsync();
        var missing = Accounts.Build()
            .Select(a => a.CurrencyCode)
            .Distinct()
            .Where(code => !currencies.Contains(code))
            .Select(code => new Currency { CurrencyCode = code, Name = code, MinorUnits = 2, Symbol = code });
        context.Currencies.AddRange(missing);

        // The portfolio itself, custodian links dropped: this context holds no contacts, and the term
        // service neither reads nor validates them.
        foreach (var account in Accounts.Build())
        {
            account.CustodianId = null;
            context.Accounts.Add(account);
        }

        await context.SaveChangesAsync();
        return context;
    }


    /// <summary>
    /// AC 40 — the seed reaches every state this change introduced, so each is demonstrable on the
    /// fast tiers and in the running demo stack rather than only in a test fixture.
    /// </summary>
    [Fact]
    public void The_seed_exercises_every_new_cadence_state()
    {
        var terms = TermGenerator.Build();

        Assert.Contains(terms, t => t.Interval == Interval.PerUnit);
        Assert.Contains(terms, t => t.Interval == Interval.Weekly);
        Assert.Contains(terms, t => t.Interval == Interval.Monthly && t.IntervalCount == 3);
        Assert.Contains(terms, t => t.AnchorDate is not null && t.AnchorDate != t.EffectiveFrom);
    }

    /// <summary>
    /// The persisted invariant, over the seed: a count is non-null exactly when the unit is
    /// periodic. A seeded row breaking it would be one the write path itself would refuse.
    /// </summary>
    [Fact]
    public void Every_seeded_term_carries_a_count_exactly_when_its_interval_is_periodic()
    {
        foreach (var term in TermGenerator.Build())
        {
            var shouldHaveCount = term.Interval is { } interval && interval.IsPeriodic();

            Assert.Equal(shouldHaveCount, term.IntervalCount is not null);
        }
    }

    /// <summary>The retired ordinal is seeded nowhere — the seed is the post-migration vocabulary.</summary>
    [Fact]
    public void No_seeded_term_holds_the_retired_quarterly_ordinal()
    {
        Assert.DoesNotContain(TermGenerator.Build(), t => t.Interval is { } interval && (int)interval == 4);
    }

    private static NewTerm ToRequest(Term term) => new()
    {
        Label = term.Label,
        ValueUnit = (DtoTermValueUnit)(int)term.ValueUnit,
        Value = term.Value,
        CurrencyCode = term.CurrencyCode,
        Interval = term.Interval is { } period ? (DtoInterval)(int)period : null,
        IntervalCount = term.IntervalCount,
        AnchorDate = term.AnchorDate,
        EffectiveFrom = term.EffectiveFrom,
        Note = term.Note,
    };
}
