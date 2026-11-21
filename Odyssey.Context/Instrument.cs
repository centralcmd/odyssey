using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;

namespace Odyssey.Context;

/// <summary>
/// A stock, ETF or fund the household follows (issue #446 §4.1) — the counterpart of <see cref="Currency"/>.
/// Shared single-tenant data: access is governed by the <c>instruments.*</c> claims, not row ownership.
/// </summary>
/// <remarks>
/// The identity fields (<see cref="Symbol"/>, <see cref="MicCode"/>, <see cref="Isin"/>, <see cref="Type"/>,
/// <see cref="CurrencyCode"/>) are immutable once the instrument has any price or source binding: a re-typed
/// instrument would leave its history describing something else. <see cref="ListingKey"/> is computed by the
/// service from the normalised identity and is unique.
/// </remarks>
[Index(nameof(ListingKey), IsUnique = true)]
[Index(nameof(Archived))]
public class Instrument
{
    [Key]
    public Guid InstrumentId { get; set; }

    [StringLength(InstrumentIdentifierRules.SymbolMaxLength)]
    public required string Symbol { get; set; }

    /// <summary>ISO 10383 MIC. Required for stocks and ETFs; optional for funds and other.</summary>
    [StringLength(InstrumentIdentifierRules.MicLength)]
    public string? MicCode { get; set; }

    /// <summary>ISO 6166 ISIN, check digit validated. Required for funds.</summary>
    [StringLength(InstrumentIdentifierRules.IsinLength)]
    public string? Isin { get; set; }

    [StringLength(InstrumentIdentifierRules.NameMaxLength)]
    public required string Name { get; set; }

    public InstrumentType Type { get; set; }

    /// <summary>The quote currency; every price of this instrument is in it. FK to <see cref="Currency"/>, RESTRICT.</summary>
    [StringLength(3)]
    public required string CurrencyCode { get; set; }

    [StringLength(InstrumentIdentifierRules.ListingKeyMaxLength)]
    public required string ListingKey { get; set; }

    public bool ShowOnDashboard { get; set; } = true;

    public DateTime? Archived { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }
}

/// <summary>
/// One daily close of an instrument (issue #446 §4.2) — the counterpart of <see cref="ExchangeRate"/>. The close
/// is in the instrument's <see cref="Instrument.CurrencyCode"/>; there is no currency column on the price.
/// </summary>
/// <remarks>
/// <see cref="SourceKey"/> <c>NULL</c> is a manual row. <see cref="SourceKeyGuard"/> is a stored generated column
/// <c>COALESCE(SourceKey, '')</c>, so the unique <c>(InstrumentId, TradingDate, SourceKeyGuard)</c> index holds one
/// manual row and one row per source per date even for concurrent writers (MariaDB treats NULLs as distinct).
/// </remarks>
[Index(nameof(InstrumentId), nameof(TradingDate), nameof(SourceKeyGuard), IsUnique = true)]
[Index(nameof(TradingDate))]
public class InstrumentPrice
{
    [Key]
    public Guid InstrumentPriceId { get; set; }

    public Guid InstrumentId { get; set; }

    public DateOnly TradingDate { get; set; }

    public decimal Close { get; set; }

    [StringLength(InstrumentIdentifierRules.SourceKeyMaxLength)]
    public string? SourceKey { get; set; }

    /// <summary>Database-generated <c>COALESCE(SourceKey, '')</c>; never written by the application.</summary>
    [StringLength(InstrumentIdentifierRules.SourceKeyMaxLength)]
    public string SourceKeyGuard { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public string? CreatedByUserId { get; set; }
}

/// <summary>
/// Binds an instrument to a code-declared price source (issue #446 §4.3). A separate table, like
/// <see cref="ExchangeRateSyncCurrency"/>: instruments are written by Owners, while a binding decides what leaves
/// the deployment and is Admin-only.
/// </summary>
[PrimaryKey(nameof(InstrumentId), nameof(SourceKey))]
[Index(nameof(InstrumentId), nameof(Priority), IsUnique = true)]
public class InstrumentPriceSourceBinding
{
    public Guid InstrumentId { get; set; }

    [StringLength(InstrumentIdentifierRules.SourceKeyMaxLength)]
    public required string SourceKey { get; set; }

    [StringLength(InstrumentIdentifierRules.SourceSymbolMaxLength)]
    public required string SourceSymbol { get; set; }

    /// <summary>1 = primary. Unique per instrument; the upper bound is <see cref="InstrumentPriceBindingLimits.MaxBindingsPerInstrument"/>.</summary>
    public int Priority { get; set; }

    public bool Enabled { get; set; }

    public DateOnly? HistoryStartDate { get; set; }

    /// <summary>The forward-fill watermark: the newest date this binding has fetched.</summary>
    public DateOnly? NewestFetched { get; set; }

    /// <summary>The contiguous backfill watermark: every day from here to <see cref="NewestFetched"/> was fetched completely.</summary>
    public DateOnly? BackfilledFrom { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public Dtos.Application.JobFailureCategory? LastFailure { get; set; }

    public DateTime? LastFailureUtc { get; set; }

    public int ConsecutiveFailures { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedByUserId { get; set; }
}

/// <summary>
/// Per-source state shared across worker replicas (issue #446 §4.4): back-off and the daily and hourly request
/// counters (issue #447 A1). No
/// foreign key — sources are code-declared; a row for an unregistered key is ignored.
/// </summary>
public class InstrumentPriceSourceState
{
    [Key]
    [StringLength(InstrumentIdentifierRules.SourceKeyMaxLength)]
    public required string SourceKey { get; set; }

    public DateTime? BackOffUntilUtc { get; set; }

    public Dtos.Application.JobFailureCategory? BackOffReason { get; set; }

    /// <summary>The Europe/Oslo day <see cref="RequestCount"/> counts.</summary>
    public DateOnly? RequestDay { get; set; }

    public int RequestCount { get; set; }

    /// <summary>
    /// The UTC clock hour <see cref="RequestHourCount"/> counts (issue #447 A1). It only ever moves forward: a
    /// reservation from a replica whose clock lags counts against this bucket rather than resetting it.
    /// </summary>
    public DateTime? RequestHourUtc { get; set; }

    public int RequestHourCount { get; set; }

    /// <summary>The concurrency token of the daily and hourly counters; rotated by every save of a modified row.</summary>
    [ConcurrencyCheck]
    public Guid Version { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
