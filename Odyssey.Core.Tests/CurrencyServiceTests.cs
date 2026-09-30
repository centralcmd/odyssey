using Context = Odyssey.Context;
using Odyssey.Core;
using Odyssey.Dtos.Finance;
using Xunit;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

public class CurrencyServiceTests
{
    [Fact]
    public async Task CreateAndGetCurrency_RoundTrips()
    {
        await using var context = TestContextFactory.Create();
        var service = new CurrencyService(context);

        var created = await service.Create(new NewCurrency
        {
            CurrencyCode = "JPY",
            Name = "Japanese Yen",
            MinorUnits = 0,
            Symbol = "¥",
            Archived = false,
        });

        var fetched = await service.Get("jpy");

        Assert.Equal("JPY", created.CurrencyCode);
        Assert.NotNull(fetched);
        Assert.Equal("JPY", fetched!.CurrencyCode);
        Assert.Equal("Japanese Yen", fetched.Name);
        Assert.Null(fetched.Archived);
    }

    [Fact]
    public async Task UpdateCurrency_ArchiveTransitions_AreCorrectAndIdempotent()
    {
        await using var context = TestContextFactory.Create();
        var service = new CurrencyService(context);

        await service.Create(new NewCurrency
        {
            CurrencyCode = "NOK",
            Name = "Norwegian Krone",
            MinorUnits = 2,
            Symbol = "kr",
            Archived = false,
        });

        var archived = await service.Update("NOK", new NewCurrency
        {
            CurrencyCode = "NOK",
            Name = "Norwegian Krone",
            MinorUnits = 2,
            Symbol = "kr",
            Archived = true,
        });

        Assert.NotNull(archived!.Archived);
        var archivedAt = archived.Archived;

        var archivedAgain = await service.Update("NOK", new NewCurrency
        {
            CurrencyCode = "NOK",
            Name = "Norwegian Krone",
            MinorUnits = 2,
            Symbol = "kr",
            Archived = true,
        });

        Assert.Equal(archivedAt, archivedAgain!.Archived);
    }

    [Fact]
    public async Task Delete_AnUnusedCurrency_RemovesIt()
    {
        await using var context = TestContextFactory.Create();
        var service = new CurrencyService(context);
        await CreateNokAsync(service);

        await service.Delete("nok");

        Assert.Null(await service.Get("NOK"));
    }

    /// <summary>
    /// Issue #241 — the EF InMemory tier enforces no foreign keys, so the refusal is the service's
    /// pre-check. Every blocker class is counted before any is reported, so one message names them all.
    /// </summary>
    [Fact]
    public async Task Delete_ACurrencyInUse_ThrowsConflictNamingEveryBlockerAndKeepsTheRow()
    {
        await using var context = TestContextFactory.Create();
        var service = new CurrencyService(context);
        await CreateNokAsync(service);

        var account = new Context.Account
        {
            Name = "Brokerage", Description = "Brokerage", Opened = DateTime.UtcNow, CurrencyCode = "NOK",
        };
        context.Accounts.Add(account);
        context.Accounts.Add(new Context.Account
        {
            Name = "Savings", Description = "Savings", Opened = DateTime.UtcNow, CurrencyCode = "NOK",
        });
        context.Transactions.Add(new Context.Transaction
        {
            AccountId = account.AccountId, Description = "Coffee", Amount = -4m, TimeStamp = DateTime.UtcNow,
            CurrencyCode = "NOK",
        });
        context.Budgets.Add(new Context.Budget
        {
            Name = "2026", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31),
            BaseCurrencyCode = "NOK",
        });
        context.TaxStatements.Add(new Context.TaxStatement
        {
            Name = "2025", FiscalYear = 2025, StartDate = new DateTime(2025, 1, 1),
            EndDate = new DateTime(2025, 12, 31), BaseCurrencyCode = "NOK", CreatedAtUtc = DateTime.UtcNow,
        });
        context.ExchangeRates.Add(new Context.ExchangeRate
        {
            FromCurrencyCode = "USD", ToCurrencyCode = "NOK", Rate = 10m, AsOf = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => service.Delete("NOK"));

        Assert.Contains("2 accounts", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 transaction.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 budget.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 tax statement.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 exchange rate.", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("propert", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Brokerage", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(await service.Get("NOK"));
    }

    [Fact]
    public async Task Delete_ACurrencyUsedOnlyByAPropertyEstimateOrTerm_ThrowsConflict()
    {
        await using var context = TestContextFactory.Create();
        var service = new CurrencyService(context);
        await CreateNokAsync(service);

        context.Properties.Add(new Context.Property
        {
            Name = "Cabin", Description = "Cabin", Type = PropertyType.RealEstate, CurrencyCode = "NOK",
        });
        context.AccountEstimates.Add(new Context.AccountEstimate
        {
            AccountId = Guid.NewGuid(), Value = 1m, EffectiveFrom = DateTime.UtcNow, CurrencyCode = "NOK",
        });
        context.PropertyEstimates.Add(new Context.PropertyEstimate
        {
            PropertyId = Guid.NewGuid(), Value = 1m, EffectiveFrom = DateTime.UtcNow, CurrencyCode = "NOK",
        });
        context.Terms.Add(new Context.Term
        {
            ContractId = Guid.NewGuid(), EffectiveFrom = DateTime.UtcNow, CurrencyCode = "NOK",
        });
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => service.Delete("NOK"));

        Assert.Contains("1 property.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 account estimate.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 property estimate.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 contract term.", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(await service.Get("NOK"));
    }

    /// <summary>Both ends of a rate pin a currency: the From side counts as much as the To side.</summary>
    [Fact]
    public async Task Delete_ACurrencyOnlyOnTheFromSideOfARate_ThrowsConflict()
    {
        await using var context = TestContextFactory.Create();
        var service = new CurrencyService(context);
        await CreateNokAsync(service);

        context.ExchangeRates.Add(new Context.ExchangeRate
        {
            FromCurrencyCode = "NOK", ToCurrencyCode = "USD", Rate = 0.1m, AsOf = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => service.Delete("NOK"));

        Assert.Contains("1 exchange rate.", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(await service.Get("NOK"));
    }

    private static Task<ExistingCurrency> CreateNokAsync(CurrencyService service) =>
        service.Create(new NewCurrency
        {
            CurrencyCode = "NOK",
            Name = "Norwegian Krone",
            MinorUnits = 2,
            Symbol = "kr",
            Archived = false,
        });

    [Fact]
    public async Task CreateCurrency_WithInvalidCode_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new CurrencyService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewCurrency
        {
            CurrencyCode = "USDX",
            Name = "Invalid",
            MinorUnits = 2,
            Symbol = "$",
            Archived = false,
        }));
    }
}
