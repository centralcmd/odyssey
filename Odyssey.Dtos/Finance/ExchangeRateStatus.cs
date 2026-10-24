namespace Odyssey.Dtos.Finance;

/// <summary>
/// Status of an exchange rate, derived at query time: <see cref="Current"/> is the newest rate in force
/// for a directed currency pair, <see cref="Historical"/> is any superseded rate, and
/// <see cref="Upcoming"/> is a rate dated in the future, which counts only once its date arrives
/// (issue #349 §3).
/// </summary>
public enum ExchangeRateStatus
{
    Current,
    Historical,
    Upcoming,
}
