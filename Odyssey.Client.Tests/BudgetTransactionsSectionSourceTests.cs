using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The budget's transaction list must bound its period with the shared <c>PeriodBounds</c>, not with
/// the raw dates (issue #238). The server's budget count reads the whole end day, while
/// <c>GET /api/transactions?to=</c> is an inclusive instant — passing the end date's midnight would
/// list fewer rows than the header counts. A source-lint because the section loads only in a browser
/// (<c>OperatingSystem.IsBrowser()</c>), so bUnit cannot reach the fetch; the semantics themselves
/// are exercised over HTTP by <c>BudgetPeriodApiTests</c>.
/// </summary>
public class BudgetTransactionsSectionSourceTests
{
    private static string Markup() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "BudgetTransactionsSection.razor"));

    [Fact]
    public void TransactionFetch_BoundsThePeriodWithPeriodBounds()
    {
        var markup = Markup();

        Assert.Contains("from: PeriodBounds.InclusiveStart(StartDate)", markup);
        Assert.Contains("to: PeriodBounds.InclusiveEndInstant(EndDate)", markup);
        Assert.DoesNotContain("to: EndDate", markup);
    }
}
