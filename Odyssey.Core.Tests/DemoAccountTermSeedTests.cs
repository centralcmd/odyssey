using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.TestData.Catalog;
using Odyssey.TestData.Generators;
using Xunit;
using DtoBillingPeriod = Odyssey.Dtos.Finance.BillingPeriod;
using DtoTermKind = Odyssey.Dtos.Finance.TermKind;
using DtoTermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using NewAccountTerm = Odyssey.Dtos.Finance.NewAccountTerm;

namespace Odyssey.Core.Tests;

/// <summary>
/// The demo term seed is a set <see cref="AccountTermService"/> itself would accept.
///
/// <para>
/// <c>DemoDataSeeder</c> writes <c>AccountTerm</c> entities straight to the context, so it bypasses
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
public class DemoAccountTermSeedTests
{
    [Fact]
    public async Task Every_seeded_term_is_accepted_by_the_write_path()
    {
        await using var context = await SeededAccountsAsync();
        var service = new AccountTermService(context);

        foreach (var term in AccountTermGenerator.Build())
        {
            // Throws DomainValidationException / DomainConflictException if the seed ever drifts from
            // what the service permits — eligibility, unit, currency, the label rules, or the series
            // duplicate guard.
            await service.Create(term.AccountId, ToRequest(term));
        }
    }

    [Fact]
    public async Task The_seed_exercises_a_percentage_fee_which_the_service_accepts()
    {
        await using var context = await SeededAccountsAsync();
        var service = new AccountTermService(context);

        var percentageFees = AccountTermGenerator.Build()
            .Where(t => t.TermKind == TermKind.Fee && t.ValueUnit == TermValueUnit.Percentage)
            .ToList();

        Assert.NotEmpty(percentageFees);

        foreach (var fee in percentageFees)
        {
            var created = await service.Create(fee.AccountId, ToRequest(fee));

            // A percentage carries no currency, and a fee always carries its name.
            Assert.Null(created.CurrencyCode);
            Assert.False(string.IsNullOrWhiteSpace(created.Label));
        }
    }

    [Fact]
    public void Every_seeded_fee_is_named_and_every_seeded_rate_is_not()
    {
        var terms = AccountTermGenerator.Build();

        Assert.All(
            terms.Where(t => t.TermKind == TermKind.Fee),
            fee => Assert.False(string.IsNullOrWhiteSpace(fee.Label)));
        Assert.All(
            terms.Where(t => t.TermKind != TermKind.Fee),
            rate => Assert.Null(rate.Label));
    }

    [Fact]
    public void No_two_seeded_terms_share_a_series_and_a_date()
    {
        // The duplicate guard would reject the second on a real write, and the deterministic id would
        // collide before that — the seed must not contain one.
        var terms = AccountTermGenerator.Build();

        var duplicates = terms
            .GroupBy(t => (t.AccountId, t.TermKind, t.LabelKey, t.EffectiveFrom))
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_seeded_term_has_a_distinct_id()
    {
        // Two fees on one account can now share a kind and a date, so the label is part of the seed
        // key; dropping it would hand both the same deterministic id.
        var terms = AccountTermGenerator.Build();

        Assert.Equal(terms.Count, terms.Select(t => t.AccountTermId).Distinct().Count());
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

    private static NewAccountTerm ToRequest(AccountTerm term) => new()
    {
        TermKind = (DtoTermKind)(int)term.TermKind,
        Label = term.Label,
        ValueUnit = (DtoTermValueUnit)(int)term.ValueUnit,
        Value = term.Value,
        CurrencyCode = term.CurrencyCode,
        BillingPeriod = term.BillingPeriod is { } period ? (DtoBillingPeriod)(int)period : null,
        EffectiveFrom = term.EffectiveFrom,
        Note = term.Note,
    };
}
