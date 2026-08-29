namespace Odyssey.Dtos.Finance;

/// <summary>
/// List-filter status for exchange rates, derived at query time: <see cref="Current"/> is the newest
/// rate for a directed currency pair, <see cref="Historical"/> is any superseded rate.
/// </summary>
public enum ExchangeRateStatus
{
    Current,
    Historical,
}
