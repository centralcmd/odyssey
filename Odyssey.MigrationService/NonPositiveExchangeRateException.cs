namespace Odyssey.MigrationService;

/// <summary>
/// Thrown before <c>AddExchangeRatePositiveCheck</c> runs when the database still holds a rate the new
/// <c>CK_ExchangeRates_RatePositive</c> forbids (issue #472 §11). Without it the same rows would fail the
/// <c>ADD CONSTRAINT</c> with a bare engine error that names no row. It REPORTS and never repairs: which of a
/// bad rate's two fixes is right — correct it or delete the row — is an operator's call.
/// </summary>
/// <remarks>
/// The message carries ids only, never a rate, and at most <see cref="MigrationRunner.NonPositiveRateIdLimit"/>
/// of them plus the total, so a damaged table cannot turn one log line into a dump.
/// </remarks>
public sealed class NonPositiveExchangeRateException(IReadOnlyList<string> exchangeRateIds, long totalCount)
    : Exception(BuildMessage(exchangeRateIds, totalCount))
{
    public IReadOnlyList<string> ExchangeRateIds { get; } = exchangeRateIds;

    public long TotalCount { get; } = totalCount;

    private static string BuildMessage(IReadOnlyList<string> ids, long total) =>
        $"Migration '{MigrationRunner.RatePositiveMigration}' adds CK_ExchangeRates_RatePositive (`Rate` > 0), " +
        $"but {total} row(s) in ExchangeRates have Rate <= 0. " +
        $"{(total > ids.Count ? $"The first {ids.Count} ids" : "Their ids")}: {string.Join(", ", ids)}. " +
        "No migration of this stage has been applied. Correct each rate or delete the row, then start the " +
        "migration service again.";
}
