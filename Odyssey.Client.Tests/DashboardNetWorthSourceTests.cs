using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The dashboard's net worth must come from <c>GET /api/accounts/totals</c>, never from a
/// client-side aggregate over the accounts list.
///
/// <para>
/// The page was transcribed from the design specimen (<c>Odyssey Design System ·
/// Dashboard.jsx</c>), whose mock data was single-currency and had no value estimates, so its
/// <c>accounts.reduce((s, a) =&gt; s + a.balance, 0)</c> was correct there. Carried into the real
/// contracts it was not: it added unlike currencies as bare numbers, ignored the in-force estimate
/// replace policy (issue #182 §9), counted <c>AccountType.Unknown</c>, and could not know which
/// accounts had no rate to the main currency.
/// </para>
///
/// <para>
/// This is the same class of defect CLAUDE.md already forbids for admin-editable caps — a
/// client-side reimplementation of a server calculation, which is wrong the moment the two
/// disagree and silent about it. These lints are cheap where a behavioural test would need the
/// whole page rendered against a faked API.
/// </para>
/// </summary>
public class DashboardNetWorthSourceTests
{
    private static string CodeBehind() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "Home.razor.cs"));

    private static string Markup() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "Home.razor"));

    [Fact]
    public void NetWorth_ComesFromTheServerTotalsEndpoint()
    {
        Assert.Contains("GetTotalsAsync", CodeBehind(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The specimen's aggregate, in any of the shapes it could come back as. The accounts list is
    /// still loaded — for the account count and the chart's start year — so the guard has to name
    /// the summation rather than the list.
    /// </summary>
    [Theory]
    [InlineData(@"Sum\s*\(\s*\w+\s*=>\s*\w+\.Balance\s*\)")]
    [InlineData(@"Sum\s*\(\s*\w+\s*=>\s*\w+\.CurrentEstimatedValue")]
    public void NetWorth_IsNotSummedClientSide(string pattern)
    {
        var match = Regex.Match(CodeBehind(), pattern);
        Assert.False(
            match.Success,
            $"Home.razor.cs:{ClientSource.LineAt(CodeBehind(), match.Index)} sums account balances "
            + "client-side. Net worth is server-computed by AccountTotalsService — a local sum "
            + "skips FX conversion and the estimate replace policy.");
    }

    /// <summary>
    /// A figure the server did not give us is withheld, not replaced. Substituting the old naive sum
    /// on a failed call would reintroduce the defect exactly when it is least visible — the reader
    /// would see a plausible number with nothing marking it as a fallback.
    /// </summary>
    [Fact]
    public void NetWorth_IsNullableSoAFailedCallShowsNoFigure()
    {
        Assert.Contains("private decimal? NetWorth", CodeBehind(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Accounts with no rate to the main currency contribute 0, so the headline is understated.
    /// The server names them; the header has to pass them on rather than swallow them.
    /// </summary>
    [Fact]
    public void UnconvertedAccounts_AreSurfacedInTheProblemRollup()
    {
        Assert.Contains("UnconvertedAccounts", CodeBehind(), StringComparison.Ordinal);
        Assert.Contains("Problems=\"HeaderProblems\"", Markup(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The specimen hardcoded "$" because its mock data was in one currency. The real figure is
    /// converted into the user's main currency, so the symbol has to follow that currency.
    /// </summary>
    [Fact]
    public void Money_IsFormattedInTheMainCurrencyNotAHardcodedDollar()
    {
        var source = CodeBehind();
        Assert.Contains("MainCurrencyCode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private static string FormatMoney", source, StringComparison.Ordinal);
    }
}
