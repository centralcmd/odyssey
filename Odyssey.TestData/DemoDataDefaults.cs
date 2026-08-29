namespace Odyssey.TestData;

/// <summary>
/// Fixed parameters that make the demo dataset deterministic. Everything that varies
/// (amounts, dates, jitter) is derived from these, so a given anchor + seed always
/// produces byte-for-byte identical data. See docs/test-environment-and-e2e-spec.md.
/// </summary>
public static class DemoDataDefaults
{
    /// <summary>Bogus randomizer seed. Fixed so generated values are reproducible.</summary>
    public const int RandomizerSeed = 20260619;

    /// <summary>
    /// The reference "today" the dataset is anchored to. All relative dates (account
    /// lifetimes, transaction streams) are computed from this. Fixed for determinism;
    /// callers may override <see cref="DemoDataSet.Build"/> with another anchor.
    /// </summary>
    public static readonly DateTime AnchorDate = new(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>First budget year and the earliest the data reaches back (~10 years).</summary>
    public const int FirstYear = 2016;

    /// <summary>Last budget year (inclusive); the year that contains <see cref="AnchorDate"/>.</summary>
    public const int LastYear = 2026;

    /// <summary>Shared password for every seeded demo login user.</summary>
    public const string UserPassword = "Odyssey!Demo1";

    /// <summary>
    /// When the demo users accepted the License and ToS, and when the demo ToS version was published
    /// (issue #354). Before <see cref="AnchorDate"/> so the demo history reads as "accepted at signup".
    /// </summary>
    public static readonly DateTime LegalRespondedAt = new(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The demo Terms of Service text. Deliberately plain and obviously non-binding — the demo stack is
    /// not a place to ship text that reads like a real agreement.
    /// </summary>
    public const string TermsOfServiceContent = """
        Odyssey Demo Terms of Service

        This is sample text seeded into the Odyssey demo environment. It is not a real
        agreement and creates no obligations for anyone.

        1. This environment holds synthetic demo data only. Do not store real personal or
           financial information in it.
        2. Demo accounts are shared and may be reset without notice.
        3. Administrators may publish a new version of these terms at any time, which will
           ask every user to accept it again.
        """;

    /// <summary>Year-over-year growth applied to income budget items / income streams.</summary>
    public const double IncomeGrowth = 1.04;

    /// <summary>Year-over-year growth applied to expense budget items / expense streams.</summary>
    public const double ExpenseGrowth = 1.03;

    /// <summary>Currencies referenced by the demo data. These already exist (seeded by OdysseyContext).</summary>
    public static class Currencies
    {
        public const string Usd = "USD";
        public const string Eur = "EUR";
        public const string Sek = "SEK";
        public const string Gbp = "GBP";

        /// <summary>Not used by accounts, but it is the default display/main currency
        /// (<c>AccountController.DefaultMainCurrency</c>), so exchange rates must reach it.</summary>
        public const string Nok = "NOK";
    }

    /// <summary>
    /// Escalates a base (year-<see cref="FirstYear"/>) amount to <paramref name="year"/>,
    /// rounded to the nearest 100. Income grows faster than expense (spec §3.9).
    /// </summary>
    public static decimal Escalate(decimal baseAmount, int year, bool isIncome)
    {
        var periods = year - FirstYear;
        var growth = Math.Pow(isIncome ? IncomeGrowth : ExpenseGrowth, periods);
        var raised = (decimal)((double)baseAmount * growth);
        return Math.Round(raised / 100m, MidpointRounding.AwayFromZero) * 100m;
    }
}
