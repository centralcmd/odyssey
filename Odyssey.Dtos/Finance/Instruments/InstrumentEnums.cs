namespace Odyssey.Dtos.Finance;

/// <summary>
/// What kind of instrument a watchlist entry is (issue #446 §4.1). Ordinals are a wire and persistence contract:
/// never renumber, and <c>0</c> is deliberately unused so an omitted value fails validation.
/// </summary>
public enum InstrumentType
{
    Stock = 1,
    Etf = 2,
    Fund = 3,
    Other = 9,
}

/// <summary>A price row's read-side status: the newest-rule winner of its instrument, or an older row.</summary>
public enum InstrumentPriceStatus
{
    Current,
    Historical,
}

/// <summary>Whether a price was entered by hand or fetched from a source.</summary>
public enum InstrumentPriceSource
{
    Manual,
    Fetched,
}

/// <summary>Which prices a purge removes (issue #446 §5 endpoint 13).</summary>
public enum InstrumentPricePurgeScope
{
    Manual = 1,
    Fetched = 2,
    All = 3,
}

/// <summary>Sortable keys for the instruments list.</summary>
public enum InstrumentSortBy
{
    Symbol,
    Name,
    Type,
    Currency,

    /// <summary>By the newest price date. Requires <c>instrument-prices.read</c>: the order alone reveals price recency.</summary>
    LatestDate,
}

/// <summary>Sortable keys for the instrument-prices list.</summary>
public enum InstrumentPriceSortBy
{
    TradingDate,
    Instrument,
    Close,
    CreatedAt,
}
