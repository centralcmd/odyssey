using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The source-lint half of issue #99's rule: <b>a valuation decides membership by the account's
/// open/closed term, and never reads <c>Archived</c>.</b>
///
/// <para>
/// Both valuation services used to test <c>Archived == null</c> and never read <c>Closed</c> at all,
/// which is two defects pushing in opposite directions: archiving an account moved the headline figure
/// (and, in the history, rewrote its whole past), while closing one moved nothing. Neither has an
/// obvious symptom in a healthy dataset, which is why they survived.
/// </para>
///
/// <para>
/// The behavioural tests next to this one pin what the services compute. This pins what they are
/// allowed to read, because the regression is one word long: <c>Archived</c> is the app's generic
/// declutter verb — photos, journal entries, tags, budgets, contracts and insurance policies all carry
/// the same column — so reaching for it in a finance query reads as ordinary and compiles fine.
/// </para>
///
/// <para>
/// Scoped to the two files that value a portfolio rather than to all of <c>Odyssey.Core</c>: every
/// other finance surface legitimately filters archived rows out of a <i>list</i>, which is exactly what
/// the column is for. A third valuation path would need adding here.
/// </para>
/// </summary>
public class NetWorthMembershipSourceTests
{
    private static readonly string[] ValuationServices =
    [
        Path.Combine("Odyssey.Core", "Finance", "AccountTotalsService.cs"),
        Path.Combine("Odyssey.Core", "Finance", "NetWorthHistoryService.cs"),
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NoValuationPathReadsArchived(int service)
    {
        var relative = ValuationServices[service];
        var code = StripComments(File.ReadAllText(Path.Combine(SolutionRoot(), relative)));
        var match = Regex.Match(code, @"\bArchived\b");

        Assert.False(
            match.Success,
            $"{relative} reads Account.Archived. Issue #99: archiving is a list filter, not a valuation "
            + "event — membership is the open/closed term (Opened < bound, and Closed null or after it). "
            + "Archived is additionally a reversible toggle with no transition history, so a per-point "
            + "reading of it is not even expressible.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EveryValuationPathReadsClosed(int service)
    {
        var relative = ValuationServices[service];
        var code = StripComments(File.ReadAllText(Path.Combine(SolutionRoot(), relative)));

        Assert.Matches(@"\bClosed\b", code);
    }

    /// <summary>
    /// Comments are stripped before the search, because both files legitimately <i>discuss</i>
    /// archiving — including <c>AccountTotalsService</c>'s note that an archived <b>currency</b> is a
    /// <c>400</c>, which is a different column on a different table. A lint that fired on prose would
    /// be removed rather than obeyed.
    /// </summary>
    private static string StripComments(string code) =>
        Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"//.*?$", string.Empty, RegexOptions.Multiline);

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the solution root from the test binary.");
    }
}
