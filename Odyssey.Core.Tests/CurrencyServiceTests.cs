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
