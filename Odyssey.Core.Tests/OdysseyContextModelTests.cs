using Odyssey.Context;
using Xunit;

namespace Odyssey.Core.Tests;

public class OdysseyContextModelTests
{
    [Fact]
    public void ArchivedIndexesExistOnFinanceEntities()
    {
        using var context = TestContextFactory.Create();
        using var journal = TestContextFactory.CreateJournal();

        var accountIndexes = context.Model.FindEntityType(typeof(Account))!.GetIndexes();
        var budgetIndexes = context.Model.FindEntityType(typeof(Budget))!.GetIndexes();
        var tagIndexes = context.Model.FindEntityType(typeof(TransactionTag))!.GetIndexes();
        var contactIndexes = journal.Model.FindEntityType(typeof(Contact))!.GetIndexes();
        var supportedCurrencyIndexes = context.Model.FindEntityType(typeof(Currency))!.GetIndexes();

        Assert.Contains(accountIndexes, i => i.Properties.Any(p => p.Name == nameof(Account.Archived)));
        Assert.Contains(budgetIndexes, i => i.Properties.Any(p => p.Name == nameof(Budget.Archived)));
        Assert.Contains(tagIndexes, i => i.Properties.Any(p => p.Name == nameof(TransactionTag.Archived)));
        Assert.Contains(contactIndexes, i => i.Properties.Any(p => p.Name == nameof(Contact.Archived)));
        Assert.Contains(supportedCurrencyIndexes, i => i.Properties.Any(p => p.Name == nameof(Currency.Archived)));
    }

    [Fact]
    public void ContactModelHasConfiguredIndexes()
    {
        using var journal = TestContextFactory.CreateJournal();

        var contactIndexes = journal.Model.FindEntityType(typeof(Contact))!.GetIndexes().ToList();

        Assert.Contains(contactIndexes, index =>
            !index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual([nameof(Contact.NormalizedName)]));

        Assert.Contains(contactIndexes, index =>
            !index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(Contact.Type), nameof(Contact.Archived)]));
    }

    [Fact]
    public void TransactionModelHasContactAndCurrencyIndexes()
    {
        using var context = TestContextFactory.Create();

        var transactionIndexes = context.Model.FindEntityType(typeof(Transaction))!.GetIndexes();

        Assert.Contains(transactionIndexes, index =>
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(Transaction.ContactId)]));
        Assert.Contains(transactionIndexes, index =>
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(Transaction.CurrencyCode)]));
    }

    [Fact]
    public void ExchangeRateModelHasLatestRateLookupIndex()
    {
        using var context = TestContextFactory.Create();

        var exchangeRateIndexes = context.Model.FindEntityType(typeof(ExchangeRate))!.GetIndexes();

        Assert.Contains(exchangeRateIndexes, index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(ExchangeRate.FromCurrencyCode),
                    nameof(ExchangeRate.ToCurrencyCode),
                    nameof(ExchangeRate.AsOf),
                ]));
    }
}
