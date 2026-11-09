using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos.Application;

namespace Odyssey.Dtos.Finance;

/// <summary><c>GET /api/system-settings/instrument-price-sources</c> (issue #446 §5 endpoint 16).</summary>
public sealed record InstrumentPriceSourcesStatus
{
    public List<InstrumentPriceSourceInfo> Sources { get; set; } = [];

    public int MaxBindingsPerInstrument { get; set; } = InstrumentPriceBindingLimits.MaxBindingsPerInstrument;

    public DateTime? NextRunAtUtc { get; set; }

    public int ConsecutiveFailures { get; set; }

    public Guid? CurrentRunId { get; set; }

    public int CatchUpDelayMinutes { get; set; }

    public int RunTimeoutMinutes { get; set; }

    public int ManualRunCooldownSeconds { get; set; }
}

public sealed record InstrumentPriceSourceInfo
{
    public required string Key { get; set; }

    public required string DisplayName { get; set; }

    public required string Attribution { get; set; }

    public List<InstrumentType> SupportedTypes { get; set; } = [];

    /// <summary>Every secret the source needs reads <c>Found</c>.</summary>
    public bool Configured { get; set; }

    public DateTime? BackOffUntilUtc { get; set; }

    public JobFailureCategory? BackOffReason { get; set; }

    public int RequestsToday { get; set; }

    public int MaxRequestsPerDay { get; set; }
}

/// <summary>One binding in a <c>PUT …/bindings/{instrumentId}</c> body; its priority is its position.</summary>
public sealed record InstrumentPriceBindingInput
{
    [Required]
    [StringLength(InstrumentIdentifierRules.SourceKeyMaxLength)]
    [RegularExpression(InstrumentIdentifierRules.SourceKeyPattern)]
    public string SourceKey { get; set; } = string.Empty;

    [Required]
    [StringLength(InstrumentIdentifierRules.SourceSymbolMaxLength)]
    [RegularExpression(InstrumentIdentifierRules.SourceSymbolPattern,
        ErrorMessage = "Use letters, digits, dots, hyphens and underscores, without '..'.")]
    public string SourceSymbol { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public DateOnly? HistoryStartDate { get; set; }
}

public sealed record InstrumentPriceBindingsUpdate
{
    [Required]
    [MaxLength(InstrumentPriceBindingLimits.MaxBindingsPerInstrument)]
    public List<InstrumentPriceBindingInput> Bindings { get; set; } = [];
}

/// <summary>A stored binding as read by an Admin.</summary>
public sealed record ExistingInstrumentPriceBinding
{
    public required string SourceKey { get; set; }

    public required string SourceSymbol { get; set; }

    public int Priority { get; set; }

    public bool Enabled { get; set; }

    public DateOnly? HistoryStartDate { get; set; }

    public DateOnly? NewestFetched { get; set; }

    public DateOnly? BackfilledFrom { get; set; }

    public bool BackfillComplete { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public JobFailureCategory? LastFailure { get; set; }

    public DateTime? LastFailureUtc { get; set; }

    public int ConsecutiveFailures { get; set; }

    /// <summary><c>false</c> when the key names a source this build no longer has.</summary>
    public bool SourceRegistered { get; set; }
}
