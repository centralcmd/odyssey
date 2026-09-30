using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos;
using Xunit;
using AccountType = Odyssey.Context.AccountType;
using TransactionStatus = Odyssey.Dtos.Finance.TransactionStatus;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine proof that <see cref="MoneyBounds"/> names exactly what the columns hold (issue #240):
/// each limit round-trips through MariaDB unchanged, and the first value past it does not — the amount
/// is refused by the engine (the <c>500</c> the <c>[Range]</c> now pre-empts) and a rate below the
/// floor is stored as zero (the silent failure it pre-empts). EF InMemory stores any <c>decimal</c>,
/// so none of this is observable on the fast tiers.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class MoneyBoundsIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_money_bounds";

    private static decimal Parse(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private OdysseyContext NewContext()
    {
        var connectionString = fixture.ConnectionStringFor(Database);
        return new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options);
    }

    private async Task EnsureSchemaAsync()
    {
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private async Task<Guid> AddAccountAsync()
    {
        var accountId = Guid.NewGuid();
        await using var context = NewContext();
        context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = "Money bounds",
            Description = "issue #240",
            Opened = DateTime.UtcNow,
            AccountType = AccountType.SavingsAccount,
            CurrencyCode = "USD",
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    private static Transaction NewTransaction(Guid accountId, decimal amount) => new()
    {
        TransactionId = Guid.NewGuid(),
        Description = "bound",
        Amount = amount,
        TimeStamp = DateTime.UtcNow,
        AccountId = accountId,
        CurrencyCode = "USD",
        Status = TransactionStatus.New,
        StatusChangedAt = DateTime.UtcNow,
    };

    private static ExchangeRate NewRate(decimal rate) => new()
    {
        ExchangeRateId = Guid.NewGuid(),
        FromCurrencyCode = "USD",
        ToCurrencyCode = "EUR",
        Rate = rate,
        AsOf = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
    };

    [SkippableTheory]
    [InlineData(MoneyBounds.AmountMax)]
    [InlineData(MoneyBounds.AmountMin)]
    public async Task AnAmountAtTheBound_RoundTripsExactly(string bound)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureSchemaAsync();
        var accountId = await AddAccountAsync();
        var amount = Parse(bound);

        var transaction = NewTransaction(accountId, amount);
        await using (var context = NewContext())
        {
            context.Transactions.Add(transaction);
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            var stored = await context.Transactions.AsNoTracking()
                .SingleAsync(t => t.TransactionId == transaction.TransactionId);
            Assert.Equal(amount, stored.Amount);
        }
    }

    [SkippableFact]
    public async Task AnAmountPastTheBound_IsRefusedByTheEngine()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureSchemaAsync();
        var accountId = await AddAccountAsync();

        await using var context = NewContext();
        context.Transactions.Add(NewTransaction(accountId, Parse(MoneyBounds.AmountMax) + 1m));

        // MariaDB error 1264 (out of range) — what reached the API as a 500 before the [Range].
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [SkippableTheory]
    [InlineData(MoneyBounds.ExchangeRateMin)]
    [InlineData(MoneyBounds.ExchangeRateMax)]
    public async Task ARateAtTheBound_RoundTripsExactly(string bound)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureSchemaAsync();
        var rate = NewRate(Parse(bound));

        await using (var context = NewContext())
        {
            context.ExchangeRates.Add(rate);
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            var stored = await context.ExchangeRates.AsNoTracking()
                .SingleAsync(r => r.ExchangeRateId == rate.ExchangeRateId);
            Assert.Equal(Parse(bound), stored.Rate);
        }
    }

    [SkippableFact]
    public async Task ARateBelowTheFloor_IsStoredAsZero()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureSchemaAsync();
        var rate = NewRate(0.000000004m);

        await using (var context = NewContext())
        {
            context.ExchangeRates.Add(rate);
            await context.SaveChangesAsync();
        }

        // The silent failure the floor pre-empts: accepted, and every conversion through it is zero.
        await using (var context = NewContext())
        {
            var stored = await context.ExchangeRates.AsNoTracking()
                .SingleAsync(r => r.ExchangeRateId == rate.ExchangeRateId);
            Assert.Equal(0m, stored.Rate);
        }
    }
}
