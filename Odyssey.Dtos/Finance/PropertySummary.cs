using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>How many live (unarchived) properties are of one type.</summary>
public sealed record PropertyTypeCount
{
    public PropertyType Type { get; set; }

    public int Count { get; set; }
}

/// <summary>How many properties are in each derived status. The three sum to the whole file.</summary>
public sealed record PropertyStatusCounts
{
    public int Owned { get; set; }

    public int Disposed { get; set; }

    public int Archived { get; set; }
}

/// <summary>The in-force estimates of the owned properties in one currency, summed unconverted.</summary>
public sealed record PropertyCurrencyValue
{
    [StringLength(3)]
    public required string CurrencyCode { get; set; }

    public decimal Total { get; set; }

    /// <summary>How many owned properties contribute an in-force estimate to <see cref="Total"/>.</summary>
    public int Count { get; set; }
}

/// <summary>
/// What the owned properties are estimated to be worth: one row per currency, plus a total converted
/// to <see cref="BaseCurrency"/> at the latest rate. A currency with no rate to base is named in
/// <see cref="UnconvertedCurrencies"/> and left out of <see cref="Total"/> — never folded in at 1:1.
/// Nothing here feeds net worth.
/// </summary>
public sealed record PropertyValueSummary
{
    [StringLength(3)]
    public required string BaseCurrency { get; set; }

    public List<PropertyCurrencyValue> ByCurrency { get; set; } = new();

    /// <summary>The converted sum, or <c>null</c> when no owned property has an estimate in force.</summary>
    public decimal? Total { get; set; }

    public List<string> UnconvertedCurrencies { get; set; } = new();
}

/// <summary>The header overview of <c>/properties</c>.</summary>
public sealed record PropertySummary
{
    public int TotalProperties { get; set; }

    /// <summary>Live properties by type; archived ones are left out.</summary>
    public List<PropertyTypeCount> ByType { get; set; } = new();

    public PropertyStatusCounts ByStatus { get; set; } = new();

    /// <summary><c>null</c> when the caller does not hold <c>properties.estimates.read</c>.</summary>
    public PropertyValueSummary? Value { get; set; }
}
