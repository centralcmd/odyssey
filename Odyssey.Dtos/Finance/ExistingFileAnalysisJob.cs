namespace Odyssey.Dtos.Finance;

public sealed record ExistingFileAnalysisJob(
    Guid Id,
    Guid AccountFileId,
    FileAnalysisJobStatus Status,
    string? FileTypeDetected,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? FailureCode,
    string? FailureMessage,
    string AnalyzerProvider,
    string? AnalyzerModel,
    string? PromptVersion,
    List<ExistingFileAnalysisCandidateTransaction> Candidates,
    // ── AI matching (issue #266) ──
    FileAnalysisMatchStatus MatchStatus,
    string? MatchFailureMessage,
    // Effective server-side match policy, echoed so the client never hardcodes (or drifts from) it.
    double AutoLinkThreshold,
    // ── Match vocabulary (issue #433) ──
    // What the job's consent covers; NamesOnly for a job created before the feature (NULL column).
    FileAnalysisMatchVocabularyScope MatchVocabularyScope,
    // What the latest match run sent, counted after the payload budget. Null until a match has run.
    int? ContactCount,
    int? TagCount,
    int? AliasCount,
    int? TagDescriptionCount,
    FileAnalysisVocabularyReduction? VocabularyReduction,
    // ── The extraction queue (issue #444) ──
    // When the request was accepted, and when the transfer started (null: nothing was sent).
    DateTime? QueuedAt = null,
    DateTime? TransferStartedAt = null,
    // Computed by the server from the transfer stamp — the client never derives it from a failure code.
    FileAnalysisDocumentSent DocumentSent = FileAnalysisDocumentSent.No,
    // The STAMPED processor, so a "sent to {processor}" line never names today's settings.
    string? ProcessorInForce = null
);
