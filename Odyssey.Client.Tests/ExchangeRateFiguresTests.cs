using Odyssey.Client.Pages.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Inverse column's arithmetic. It arrived with the design system's flat rates table, where the
/// reciprocal moved out of an expanded detail panel and became a column — and, with it, a server
/// sort key that orders by Rate REVERSED rather than by 1/Rate.
///
/// <para>
/// That equivalence is what these pin. It holds only because the reciprocal is strictly decreasing
/// in the rate, so a test that merely checked one value would not notice if the two ever diverged.
/// </para>
/// </summary>
public class ExchangeRateFiguresTests
{
    [Theory]
    [InlineData(2, 0.5)]
    [InlineData(0.5, 2)]
    [InlineData(1, 1)]
    [InlineData(10.8696, 0.092)]
    public void Inverse_IsTheReciprocal(double rate, double expected)
    {
        Assert.Equal((decimal)expected, ExchangeRateFigures.Inverse((decimal)rate), precision: 6);
    }

    /// <summary>
    /// A zero rate cannot arrive through the API — both write DTOs bound Rate above zero — but the
    /// guard is what keeps a hand-written or legacy row from throwing a division fault in a cell.
    /// </summary>
    [Fact]
    public void Inverse_OfZero_IsZeroRatherThanAThrow()
    {
        Assert.Equal(0m, ExchangeRateFigures.Inverse(0m));
    }

    /// <summary>
    /// The property the server's sort key leans on: over any set of positive rates, ordering by
    /// inverse ascending is exactly ordering by rate descending. ExchangeRateService implements
    /// Inverse as that reversal, so if this stopped holding, the column and the sort would disagree.
    /// </summary>
    [Fact]
    public void Inverse_OrdersExactlyOppositeToRate()
    {
        decimal[] rates = [0.0001m, 0.5m, 1m, 1.0001m, 13.7609m, 1_000_000m];

        var byInverseAscending = rates.OrderBy(ExchangeRateFigures.Inverse).ToList();
        var byRateDescending = rates.OrderByDescending(r => r).ToList();

        Assert.Equal(byRateDescending, byInverseAscending);
    }

    [Fact]
    public void Inverse_RoundTrips()
    {
        Assert.Equal(4m, ExchangeRateFigures.Inverse(ExchangeRateFigures.Inverse(4m)), precision: 10);
    }

    // ── Display ──────────────────────────────────────────────────────────────
    // Invariant culture is deliberate: the column is tabular figures aligned against its neighbours,
    // so a comma decimal separator under a Norwegian locale would break the alignment it relies on.

    [Theory]
    [InlineData(1, "1.00")]
    [InlineData(10.8696, "10.8696")]
    [InlineData(0.092, "0.092")]
    [InlineData(1234.5, "1,234.50")]
    public void Format_AlwaysShowsTwoDecimals_AndAtMostFour(double value, string expected)
    {
        Assert.Equal(expected, ExchangeRateFigures.Format((decimal)value));
    }
}
