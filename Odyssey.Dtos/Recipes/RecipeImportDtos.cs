using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Recipes;

// AI recipe import (issue #602 §5). Every relationship is a scalar id; no request can set a server-owned column.

/// <summary>
/// A draft recipe's content — the relaxed recipe shape (every field may be null: a draft can lack a title or
/// servings). Stored as the draft's JSON; also the <c>PUT</c> body's content.
/// </summary>
public sealed record RecipeImportDraftContent
{
    [Range(1, RecipeImportLimits.DraftSchemaVersion)]
    public int SchemaVersion { get; set; } = RecipeImportLimits.DraftSchemaVersion;

    [StringLength(RecipeLimits.MaxTitleLength)]
    public string? Title { get; set; }

    [StringLength(RecipeLimits.MaxDescriptionLength)]
    public string? Description { get; set; }

    [StringLength(RecipeLimits.MaxNotesLength)]
    public string? Notes { get; set; }

    [StringLength(RecipeLimits.MaxSourceLength)]
    public string? Source { get; set; }

    [Range(RecipeLimits.MinServings, RecipeLimits.MaxServings)]
    public int? Servings { get; set; }

    [StringLength(RecipeLimits.MaxServingsLabelLength)]
    public string? ServingsLabel { get; set; }

    [Range(0, RecipeLimits.MaxMinutes)]
    public int? PrepMinutes { get; set; }

    [Range(0, RecipeLimits.MaxMinutes)]
    public int? CookMinutes { get; set; }

    [MaxLength(RecipeLimits.MaxIngredients)]
    public RecipeImportIngredient[] Ingredients { get; set; } = [];

    [MaxLength(RecipeLimits.MaxSteps)]
    public RecipeImportStep[] Steps { get; set; } = [];

    /// <summary>
    /// Suggested recipe tags. On <c>PUT</c>, <c>null</c> (or absent) leaves the stored ids unchanged and needs no
    /// tag claim; an array (including <c>[]</c>) replaces them and needs <c>recipes.tags.read</c>. Every other field
    /// is a full replacement. Reads return <c>null</c> to a caller without <c>recipes.tags.read</c>.
    /// </summary>
    [MaxLength(RecipeLimits.MaxTags)]
    public Guid[]? TagIds { get; set; }
}

public sealed record RecipeImportIngredient
{
    [StringLength(RecipeLimits.MaxSectionLength)]
    public string? Section { get; set; }

    [Range(typeof(decimal), RecipeLimits.MinAmount, RecipeLimits.MaxAmount, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Amount { get; set; }

    [EnumDataType(typeof(IngredientUnit))]
    public IngredientUnit Unit { get; set; }

    [Required]
    [StringLength(RecipeLimits.MaxIngredientNameLength)]
    public string Name { get; set; } = string.Empty;

    public bool Optional { get; set; }
}

public sealed record RecipeImportStep
{
    [StringLength(RecipeLimits.MaxSectionLength)]
    public string? Section { get; set; }

    [Required]
    [StringLength(RecipeLimits.MaxStepTextLength)]
    public string Text { get; set; } = string.Empty;
}

/// <summary><c>GET /api/recipe-imports/options</c>: the disclosure, the consent sentence and every limit.</summary>
public sealed record RecipeImportOptions
{
    public bool Enabled { get; set; }

    public string Processor { get; set; } = string.Empty;

    public string ProcessorRegion { get; set; } = string.Empty;

    public string LawfulBasis { get; set; } = string.Empty;

    public string PrivacyNoticeUrl { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>Per caller: it covers <see cref="SendsTagNames"/>, which depends on the caller's tag claim.</summary>
    public string DisclosureVersion { get; set; } = string.Empty;

    public bool SendsTagNames { get; set; }

    /// <summary>The server-rendered consent sentence for <see cref="DisclosureVersion"/> — the binding text.</summary>
    public string ConsentText { get; set; } = string.Empty;

    public int MaxSourceTextLength { get; set; }

    public int MaxFilesPerImport { get; set; }

    public long MaxTotalBytes { get; set; }

    public int MaxPdfPages { get; set; }

    public int RetentionDays { get; set; }

    public IReadOnlyList<string> AllowedContentTypes { get; set; } = [];

    public IReadOnlyDictionary<string, long> MaxFileBytes { get; set; } = new Dictionary<string, long>();
}

/// <summary><c>POST /api/recipe-imports</c> (JSON): text and/or existing Library files.</summary>
public sealed record CreateRecipeImport : IValidatableObject
{
    [Required]
    public Guid? ClientRequestId { get; set; }

    [StringLength(RecipeImportLimits.MaxSourceTextLength)]
    public string? Text { get; set; }

    [MaxLength(RecipeImportLimits.MaxFilesPerImport)]
    public Guid[] ExistingFileIds { get; set; } = [];

    public bool ConsentAcknowledged { get; set; }

    [StringLength(16)]
    public string? DisclosureVersion { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Text) && ExistingFileIds.Length == 0 && validationContext.Items.ContainsKey(UploadCountKey) is false)
        {
            yield return new ValidationResult("Paste recipe text or add a file.", [nameof(Text)]);
        }
    }

    /// <summary>Set by the multipart endpoint, which carries files the binder does not see.</summary>
    public const string UploadCountKey = "recipe-import-upload-count";
}

/// <summary>A recipe-import list row (§5).</summary>
public sealed record RecipeImportSummary
{
    public Guid Id { get; set; }

    public RecipeImportStatus Status { get; set; }

    public bool HasText { get; set; }

    public int FileCount { get; set; }

    /// <summary><c>null</c> without <c>files.read</c>.</summary>
    public string? FirstFileName { get; set; }

    /// <summary>A display name, never a user id.</summary>
    public string? RequestedByName { get; set; }

    public string? DraftTitle { get; set; }

    public int PendingDraftCount { get; set; }

    public int AcceptedDraftCount { get; set; }

    public Guid? AcceptedRecipeId { get; set; }

    public DateTime QueuedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool ContentPurged { get; set; }

    public string? FailureCode { get; set; }
}

public sealed record RecipeImportSourceFile
{
    public Guid FileId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long Size { get; set; }
}

public sealed record RecipeImportSourceItem
{
    public int Ordinal { get; set; }

    public RecipeImportSourceKind Kind { get; set; }

    public string? Text { get; set; }

    public RecipeImportSourceFile? File { get; set; }

    /// <summary>The file was deleted from the Files store after it was sent.</summary>
    public bool FileRemoved { get; set; }
}

public sealed record RecipeImportDraftItem
{
    public Guid Id { get; set; }

    public int Ordinal { get; set; }

    public RecipeImportDraftStatus Status { get; set; }

    public RecipeImportDraftContent? Content { get; set; }

    public IReadOnlyList<string> Warnings { get; set; } = [];

    public Guid? AcceptedRecipeId { get; set; }

    public string? UpdatedByName { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public int Version { get; set; }
}

/// <summary>One import with its sources and draft recipes (§5).</summary>
public sealed record RecipeImportDetail
{
    public RecipeImportSummary Summary { get; set; } = new();

    /// <summary>File sources are omitted without <c>files.read</c>; <see cref="RecipeImportSummary.FileCount"/> stays.</summary>
    public IReadOnlyList<RecipeImportSourceItem> Sources { get; set; } = [];

    public string? FailureMessage { get; set; }

    /// <summary>Derived from the transfer stamp alone: did anything leave the deployment.</summary>
    public bool DocumentSent { get; set; }

    public IReadOnlyList<RecipeImportDraftItem> Drafts { get; set; } = [];
}

/// <summary><c>PUT /api/recipe-imports/{id}/drafts/{draftId}</c>: Save and finish later.</summary>
public sealed record UpdateRecipeImportDraft
{
    [Required]
    public RecipeImportDraftContent? Content { get; set; }

    [Required]
    [Range(0, int.MaxValue)]
    public int? Version { get; set; }
}

/// <summary><c>POST …/drafts/{draftId}/accept</c>: create the recipe from the reviewed content.</summary>
public sealed record AcceptRecipeImportDraft
{
    [Required]
    public NewRecipe? Recipe { get; set; }

    public bool AttachSourceFiles { get; set; }

    [Required]
    [Range(0, int.MaxValue)]
    public int? Version { get; set; }
}

public sealed record AcceptRecipeImportDraftResult
{
    public Guid RecipeId { get; set; }

    public int AttachedFileCount { get; set; }

    public int MissingFileCount { get; set; }
}

/// <summary>A row of <c>GET /api/recipe-imports/library-files</c> — a purpose-built five-field projection.</summary>
public sealed record RecipeImportLibraryFile
{
    public Guid FileId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime UploadedAt { get; set; }
}

public enum RecipeImportSortBy
{
    QueuedAt,
    Status,
    DraftTitle,
}

/// <summary><c>GET /api/recipe-imports</c>.</summary>
public sealed class RecipeImportsQueryParams : QueryParams<RecipeImportSortBy>
{
    [MaxLength(5)]
    public RecipeImportStatus[]? Status { get; set; }

    /// <summary>Only imports with a pending draft recipe — the "ready to review" count.</summary>
    public bool? HasPendingDraft { get; set; }

    public bool IncludePurged { get; set; }
}

/// <summary><c>GET /api/recipe-imports/library-files</c>.</summary>
public sealed class RecipeImportLibraryQuery
{
    [StringLength(200)]
    public string? Search { get; set; }

    [Range(0, int.MaxValue)]
    public int Offset { get; set; }

    [Range(1, 100)]
    public int Limit { get; set; } = 25;
}

/// <summary>Problem-details <c>code</c> values recipe import emits (#602 §9).</summary>
public static class RecipeImportErrorCodes
{
    public const string Disabled = "recipe-import-disabled";
    public const string Unavailable = "recipe-import-unavailable";
    public const string Busy = "recipe-import-busy";
    public const string DisclosureChanged = "disclosure_changed";
    public const string ConsentRequired = "consent_required";
    public const string UnsupportedSourceType = "unsupported-source-type";
    public const string SourceTooLarge = "source-too-large";
    public const string LimitReached = "recipe-import-limit-reached";
    public const string VersionConflict = "recipe-import-version-conflict";
    public const string DraftNotPending = "recipe-import-draft-not-pending";
    public const string ContentPurged = "recipe-import-content-purged";
    public const string AlreadySent = "recipe-import-already-sent";
    public const string InProgress = "recipe-import-in-progress";
}

/// <summary>Worker failure codes recorded on an import (#602 §9). Ordinals are not used: the string is stored.</summary>
public static class RecipeImportFailureCodes
{
    public const string NotStarted = "not_started";
    public const string Disabled = "disabled";
    public const string DestinationChanged = "destination_changed";
    public const string DisclosureChanged = "disclosure_changed";
    public const string RequesterRevoked = "requester_revoked";
    public const string SourceMissing = "source_missing";
    public const string SourceInvalid = "source_invalid";
    public const string CredentialUnavailable = "credential_unavailable";
    public const string Interrupted = "interrupted";
    public const string ProviderError = "provider_error";
    public const string DocumentRejected = "document_rejected";
    public const string Truncated = "truncated";
    public const string NoRecipeFound = "no_recipe_found";
    public const string MalformedOutput = "malformed_output";

    /// <summary>The codes a failure was decided before anything was sent.</summary>
    public static readonly IReadOnlySet<string> BeforeSend = new HashSet<string>(StringComparer.Ordinal)
    {
        NotStarted, Disabled, DestinationChanged, DisclosureChanged, RequesterRevoked, SourceMissing, SourceInvalid, CredentialUnavailable,
    };
}

/// <summary>The closed list of draft warning codes (§13). <c>truncated:&lt;field&gt;</c> adds a field name.</summary>
public static class RecipeImportWarningCodes
{
    public const string ServingsMissing = "servings-missing";
    public const string AmountUnclear = "amount-unclear";
    public const string AmountRange = "amount-range";
    public const string UnitUnmapped = "unit-unmapped";
    public const string Illegible = "illegible";
    public const string MultipleRecipesFound = "multiple-recipes-found";
    public const string ContainsUrl = "contains-url";
    public const string TruncatedPrefix = "truncated:";

    /// <summary>The fields a <c>truncated:</c> warning may name.</summary>
    public static readonly IReadOnlySet<string> TruncatableFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "title", "description", "notes", "source", "servingsLabel", "ingredients", "steps", "ingredient", "step", "section",
    };
}
