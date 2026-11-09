using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Validates a close against <see cref="InstrumentPriceRules.HasAtMostDecimals"/> — the one shared decimal rule.
/// The range check stays on <see cref="RangeAttribute"/>; this only rejects more than eight significant decimals.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class InstrumentPriceDecimalsAttribute : ValidationAttribute
{
    public InstrumentPriceDecimalsAttribute()
        : base($"Use at most {InstrumentPriceRules.MaxDecimals} decimals.")
    {
    }

    public override bool IsValid(object? value) =>
        value is not decimal close || InstrumentPriceRules.HasAtMostDecimals(close);
}

/// <summary>The body of <c>POST /api/instruments</c> and <c>PUT /api/instruments/{id}</c> (issue #446 §5).</summary>
public sealed record NewInstrument
{
    [Required]
    [StringLength(InstrumentIdentifierRules.SymbolMaxLength)]
    [RegularExpression(InstrumentIdentifierRules.SymbolPattern,
        ErrorMessage = "Use letters, digits, dots, hyphens and spaces, starting with a letter or digit.")]
    public string Symbol { get; set; } = string.Empty;

    [StringLength(InstrumentIdentifierRules.MicLength, MinimumLength = InstrumentIdentifierRules.MicLength)]
    [RegularExpression(InstrumentIdentifierRules.MicPattern,
        ErrorMessage = "Enter the 4-character exchange code, e.g. XOSL for Oslo Børs or XNAS for Nasdaq.")]
    public string? MicCode { get; set; }

    [StringLength(InstrumentIdentifierRules.IsinLength, MinimumLength = InstrumentIdentifierRules.IsinLength)]
    [RegularExpression(InstrumentIdentifierRules.IsinPattern,
        ErrorMessage = "Enter 12 characters: 2 country letters, 9 letters or digits, then 1 check digit.")]
    public string? Isin { get; set; }

    [Required]
    [StringLength(InstrumentIdentifierRules.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Nullable so an omitted type is "required", not "invalid enum value".</summary>
    [Required]
    [EnumDataType(typeof(InstrumentType))]
    public InstrumentType? Type { get; set; }

    [Required]
    [StringLength(3)]
    public string CurrencyCode { get; set; } = string.Empty;

    public bool ShowOnDashboard { get; set; } = true;
}

/// <summary>An instrument as read (issue #446 §5). Price-derived fields are <c>null</c> without <c>instrument-prices.read</c>.</summary>
public sealed record ExistingInstrument
{
    public required Guid InstrumentId { get; set; }

    public required string Symbol { get; set; }

    public string? MicCode { get; set; }

    public string? Isin { get; set; }

    public required string Name { get; set; }

    public required InstrumentType Type { get; set; }

    public required string CurrencyCode { get; set; }

    public bool ShowOnDashboard { get; set; }

    public DateTime? Archived { get; set; }

    public decimal? LatestClose { get; set; }

    public DateOnly? LatestTradingDate { get; set; }

    public int? PriceCount { get; set; }

    /// <summary>Prices or bindings exist, so the identity fields are immutable. Always filled (§7.3 exception).</summary>
    public bool IdentityLocked { get; set; }

    /// <summary>At least one enabled source binding exists; no source details.</summary>
    public bool HasEnabledSource { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>The unfiltered aggregates for <c>GET /api/instruments/summary</c>.</summary>
public sealed record InstrumentSummary
{
    public int TotalCount { get; set; }

    public int FollowedCount { get; set; }

    public int ArchivedCount { get; set; }

    public List<InstrumentTypeCount> ByType { get; set; } = [];

    /// <summary><c>null</c> without <c>instrument-prices.read</c>.</summary>
    public DateOnly? NewestPriceDate { get; set; }

    /// <summary>Plain-text attributions of registered sources with stored prices; <c>null</c> without <c>instrument-prices.read</c>.</summary>
    public List<string>? Attributions { get; set; }
}

public sealed record InstrumentTypeCount
{
    public InstrumentType Type { get; set; }

    public int Count { get; set; }
}

/// <summary>The body of <c>POST /api/instrument-prices</c>.</summary>
public sealed record NewInstrumentPrice
{
    [Required]
    public Guid? InstrumentId { get; set; }

    [Required]
    public DateOnly? TradingDate { get; set; }

    [Required]
    [Range(typeof(decimal), MoneyBounds.InstrumentPriceMin, MoneyBounds.InstrumentPriceMax,
        ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Enter a price above 0.")]
    [InstrumentPriceDecimals]
    public decimal? Close { get; set; }
}

/// <summary>The body of <c>PUT /api/instrument-prices/{id}</c>; the instrument of a price is immutable.</summary>
public sealed record UpdateInstrumentPrice
{
    [Required]
    public DateOnly? TradingDate { get; set; }

    [Required]
    [Range(typeof(decimal), MoneyBounds.InstrumentPriceMin, MoneyBounds.InstrumentPriceMax,
        ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Enter a price above 0.")]
    [InstrumentPriceDecimals]
    public decimal? Close { get; set; }
}

/// <summary>A price row as read.</summary>
public sealed record ExistingInstrumentPrice
{
    public required Guid InstrumentPriceId { get; set; }

    public required Guid InstrumentId { get; set; }

    public required string Symbol { get; set; }

    public required string InstrumentName { get; set; }

    public required string CurrencyCode { get; set; }

    public required DateOnly TradingDate { get; set; }

    public required decimal Close { get; set; }

    public InstrumentPriceSource Source { get; set; }

    /// <summary><c>null</c> for a manual row.</summary>
    public string? SourceKey { get; set; }

    /// <summary><c>null</c> for a manual row; the bare key when the source is no longer registered.</summary>
    public string? SourceDisplayName { get; set; }

    public InstrumentPriceStatus Status { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>The result of a purge.</summary>
public sealed record InstrumentPricePurgeResult
{
    public int Deleted { get; set; }
}

/// <summary>One point of <c>GET /api/instruments/{id}/prices/series</c>.</summary>
public sealed record InstrumentPricePoint
{
    public DateOnly Date { get; set; }

    public decimal Close { get; set; }

    public InstrumentPriceSource Source { get; set; }
}

public sealed record InstrumentPriceSeries
{
    public required string CurrencyCode { get; set; }

    public List<InstrumentPricePoint> Points { get; set; } = [];
}

/// <summary>The dashboard read (issue #446 §5 endpoint 15). Money fields are in <see cref="DisplayCurrencyCode"/>.</summary>
public sealed record InstrumentWatchlist
{
    public required string DisplayCurrencyCode { get; set; }

    /// <summary>From the setting; never a client constant.</summary>
    public int StaleAfterDays { get; set; }

    public bool Truncated { get; set; }

    public List<string> Attributions { get; set; } = [];

    public List<InstrumentWatchlistItem> Items { get; set; } = [];
}

public sealed record InstrumentWatchlistItem
{
    public required Guid InstrumentId { get; set; }

    public required string Symbol { get; set; }

    public required string Name { get; set; }

    public InstrumentType Type { get; set; }

    public required string NativeCurrencyCode { get; set; }

    public decimal? NativeClose { get; set; }

    public DateOnly? LatestTradingDate { get; set; }

    /// <summary>Display currency; <c>null</c> when unconvertible or no price.</summary>
    public decimal? Close { get; set; }

    public decimal? PreviousClose { get; set; }

    public DateOnly? PreviousTradingDate { get; set; }

    public decimal? Change { get; set; }

    public decimal? ChangePercent { get; set; }

    public bool Unconvertible { get; set; }

    public bool IsStale { get; set; }

    public InstrumentPriceSource? Source { get; set; }

    public List<InstrumentWatchlistPoint> Sparkline { get; set; } = [];
}

public sealed record InstrumentWatchlistPoint
{
    public DateOnly Date { get; set; }

    /// <summary>Display currency; <c>null</c> when that day's rate is missing.</summary>
    public decimal? Close { get; set; }
}
