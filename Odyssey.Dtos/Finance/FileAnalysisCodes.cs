namespace Odyssey.Dtos.Finance;

/// <summary>
/// Every machine-readable code the file-analysis surface emits (issue #444 §5): the ProblemDetails
/// <c>code</c> of a refused request, and a terminal job's <c>FailureCode</c>. Declared once, here, in the
/// zero-reference DTO project, so the API, Core, the typed client and the WASM client name one symbol —
/// a client that keyed on a string literal of its own would be the client-side-copy defect.
/// </summary>
public static class FileAnalysisCodes
{
    // ── ProblemDetails codes on a refused request ──────────────────────────────────────────────

    /// <summary><c>503</c>: the kill switch is off. The shared code every feature-disabled 503 carries.</summary>
    public const string FeatureDisabled = "feature_disabled";

    /// <summary><c>503</c> (and a terminal code): the model or destination could not be used.</summary>
    public const string ConfigurationUnavailable = "configuration_unavailable";

    /// <summary><c>409</c> (and a terminal code): the disclosure changed since the gate rendered.</summary>
    public const string DisclosureChanged = "disclosure_changed";

    /// <summary><c>409</c>: an open (Queued or Running) job already exists for the file.</summary>
    public const string AnalysisInProgress = "analysis_in_progress";

    /// <summary><c>422</c>: the file is larger than the analysis ceiling.</summary>
    public const string FileTooLargeForAnalysis = "file_too_large_for_analysis";

    /// <summary><c>409</c> on match: the destination changed since the job was consented.</summary>
    public const string MatchDestinationChanged = "file_analysis_destination_changed";

    /// <summary>The ProblemDetails extension carrying the open job's id on <see cref="AnalysisInProgress"/>.</summary>
    public const string AnalysisJobIdExtension = "analysisJobId";

    /// <summary>The ProblemDetails extension carrying the ceiling, in bytes, on <see cref="FileTooLargeForAnalysis"/>.</summary>
    public const string LimitBytesExtension = "limitBytes";

    // ── Terminal FailureCode values ───────────────────────────────────────────────────────────

    /// <summary>Cancelled: the kill switch was off when the worker came to send.</summary>
    public const string Disabled = "disabled";

    /// <summary>Cancelled: the requester was deleted, disabled, or no longer holds <c>file-analysis.create</c>.</summary>
    public const string RequesterUnavailable = "requester_unavailable";

    /// <summary>Cancelled: the file was deleted, re-typed, or its bytes changed since consent.</summary>
    public const string FileChanged = "file_changed";

    /// <summary>Failed: the destination, model, processor or region no longer matches the consent.</summary>
    public const string DestinationChanged = "destination_changed";

    /// <summary>Failed: the worker's prompt template is not the one the job was stamped with.</summary>
    public const string PromptVersionMismatch = "prompt_version_mismatch";

    /// <summary>Failed: no worker claimed the job in time.</summary>
    public const string NotStarted = "not_started";

    /// <summary>Failed: the run stopped (lease expired or the worker shut down) before it finished.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>Failed: the credential is not stored or cannot be read.</summary>
    public const string ProviderCredentialError = "provider_credential_error";

    /// <summary>Failed: the provider answered with an error after the resilience pipeline's retries.</summary>
    public const string ProviderError = "provider_error";

    /// <summary>Failed: anything else.</summary>
    public const string InternalError = "internal_error";
}
