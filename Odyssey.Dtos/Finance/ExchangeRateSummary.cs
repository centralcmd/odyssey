namespace Odyssey.Dtos.Finance;

/// <summary>
/// The aggregates the exchange-rates page needs (issue #349 §5.10), so it never loads the whole table.
/// Unfiltered; every figure is derivable from rows the caller can already list.
/// </summary>
public sealed record ExchangeRateSummary
{
    public int TotalCount { get; set; }

    public ExchangeRateStatusCounts ByStatus { get; set; } = new();

    public ExchangeRateSourceCounts BySource { get; set; } = new();

    /// <summary>Directed pairs with at least one row.</summary>
    public int PairCount { get; set; }

    public List<string> FromCurrencyCodes { get; set; } = [];

    public List<string> ToCurrencyCodes { get; set; } = [];

    /// <summary>The provider attribution; null when no fetched row exists.</summary>
    public string? Attribution { get; set; }

    /// <summary>The newest <c>AsOf</c> already in force; null when there is none.</summary>
    public DateTime? LatestAsOfUtc { get; set; }

    public string FetchedRateLocalTime { get; set; } = string.Empty;

    public string FetchedRateTimeZone { get; set; } = string.Empty;

    public string FetchedRateTimeZoneLabel { get; set; } = string.Empty;

    /// <summary>The provider's display name, for the New rate dialog's precedence help (issue #351 §3.C).</summary>
    public string ProviderDisplayName { get; set; } = string.Empty;
}

public sealed record ExchangeRateStatusCounts
{
    public int Current { get; set; }

    public int Historical { get; set; }

    public int Upcoming { get; set; }
}

public sealed record ExchangeRateSourceCounts
{
    public int Manual { get; set; }

    public int Fetched { get; set; }
}
