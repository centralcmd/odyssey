using Odyssey.Context;
using Odyssey.Core.Journal;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Api.Tests;

public static class TestContextFactory
{
    public static OdysseyContext Create()
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var context = new OdysseyContext(options);
        context.Currencies.AddRange(
            new Currency { CurrencyCode = "USD", Name = "US Dollar", MinorUnits = 2, Symbol = "$" },
            new Currency { CurrencyCode = "EUR", Name = "Euro", MinorUnits = 2, Symbol = "€" },
            new Currency { CurrencyCode = "SEK", Name = "Swedish Krona", MinorUnits = 2, Symbol = "kr" });
        context.SaveChanges();

        return context;
    }

    public static OdysseyContext CreateJournal()
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new OdysseyContext(options);
    }
}
