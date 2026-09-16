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
    /// still loaded — for the account count — so the guard has to name the summation rather than
    /// the list.
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

    // ── AC1 — the fabricated series is gone, and cannot come back ──────────────────────────────
    //
    // The chart used to resample an 11-element constant across the user's account span and scale it
    // so the last point landed on today's real figure. Every point but the last was invented and the
    // curve was monotonic, so it rose for everyone (issue #88). These name the residue by the exact
    // identifiers it went by, because a half-removal — the constant deleted but the caption kept, or
    // the reverse — reads as intentional.

    [Theory]
    [InlineData("GrowthCurve")]
    [InlineData("SampleCurve")]
    [InlineData("_chartStartYear")]
    public void TheSyntheticGrowthCurve_IsGoneFromTheClient(string identifier)
    {
        var offenders = ClientSource.SourceFiles()
            .Where(file => File.ReadAllText(file).Contains(identifier, StringComparison.Ordinal))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"'{identifier}' is part of the fabricated net-worth curve issue #88 removed. "
            + "The series comes from GET /api/accounts/net-worth-history now, and there is no "
            + "fallback series: when the data is not there, the chart is not there. Found in: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The caption said "Since {year}", which described the fabricated curve's span (earliest account
    /// year → this year) rather than any stored series. Keeping it over the real endpoint's points
    /// would misdate the window the reader is being shown.
    /// </summary>
    [Fact]
    public void TheSinceYearCaption_IsGone()
    {
        Assert.DoesNotContain("Since {", CodeBehind(), StringComparison.Ordinal);
        Assert.DoesNotContain("$\"Since ", CodeBehind(), StringComparison.Ordinal);
    }
}
