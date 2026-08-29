namespace Odyssey.Dtos.Finance;

/// <summary>
/// One row in the external-AI file-analysis audit trail: a single statement sent to the
/// configured AI processor (e.g. Anthropic's Claude API). Surfaced on the admin-only
/// analysis-log page for ISO 27001 accountability and GDPR transfer traceability —
/// who sent which file, when, under what lawful basis, and what came back.
/// </summary>
public sealed record FileAnalysisAuditEntry(
    Guid Id,
    DateTime? At,
    string? RequestedByUserId,
    FileAnalysisAuditUser? User,
    FileAnalysisAuditFile? File,
    FileAnalysisAuditAccount? Account,
    string Provider,
    string? Model,
    string? PromptVersion,
    int? Pages,
    long? SizeBytes,
    FileAnalysisJobStatus Status,
    int Candidates,
    int Imported,
    string? LawfulBasis,
    string RequestId,
    long? DurationMs,
    string? Failure,
    bool ConsentRecorded,
    string? ConsentMethod,
    string? ConsentText,
    // ── AI matching transfer (issue #266) ──
    FileAnalysisMatchStatus MatchStatus,
    int? VocabularyCount,
    // ── Transfer provenance (issue #439) ──
    // Admin-surface only, under the existing file-analysis.audit claim: a file-analysis.read user has
    // no need for the deployment's egress host, and an internal gateway hostname is infrastructure
    // detail. Null means "recorded before this was tracked", not "the current value" — the audit
    // surface renders it as such rather than filling in today's settings.
    string? AnalyzerBaseUrlHost = null,
    string? ProcessorInForce = null,
    string? ProcessorRegionInForce = null
);

public sealed record FileAnalysisAuditUser(string? Name, string? Email);

public sealed record FileAnalysisAuditFile(Guid Id, string Name, string Kind);

public sealed record FileAnalysisAuditAccount(string Name, string? Number);
