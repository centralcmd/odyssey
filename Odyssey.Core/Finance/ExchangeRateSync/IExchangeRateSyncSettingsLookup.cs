namespace Odyssey.Core.Finance.ExchangeRateSync;

/// <summary>
/// The two global sync settings (issue #349 §8), resolved. The interface lives here so the sync
/// service's tests can fake it; the implementation reads the settings store in <c>Odyssey.Api</c>.
/// </summary>
public interface IExchangeRateSyncSettingsLookup
{
    Task<ExchangeRateSyncSettings> GetAsync(CancellationToken cancellationToken = default);
}

/// <param name="HistoryStartDate">The global start date; null means 1 January of the current UTC year.</param>
/// <param name="IntervalHours">Hours between runs once caught up; a degraded read resolves conservatively LARGER.</param>
/// <param name="ReadFailed">The settings query itself failed — a run fails with <c>SettingsUnusable</c>.</param>
public sealed record ExchangeRateSyncSettings(DateOnly? HistoryStartDate, int IntervalHours, bool ReadFailed = false);
