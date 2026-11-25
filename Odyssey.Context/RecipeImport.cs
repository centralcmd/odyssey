using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Context;

/// <summary>
/// One AI recipe import (issue #602 §4): what a household member sent to the AI processor and what it made of it.
/// Household-shared — no owner column gates access. The row is the transfer record: its provenance and consent
/// columns are written once at create and never change, and the row is never hard-deleted (a <c>DELETE</c> is a
/// tombstone; retention purges content, not the row).
/// </summary>
[Index(nameof(Status), nameof(QueuedAt))]
[Index(nameof(RequestedByUserId), nameof(Status))]
[Index(nameof(DeletedAt))]
[Index(nameof(RequestedByUserId), nameof(ClientRequestId), IsUnique = true)]
public class RecipeImport
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecipeImportId { get; set; }

    public RecipeImportStatus Status { get; set; }

    /// <summary>The client's idempotency key; unique per requester (§5).</summary>
    public Guid ClientRequestId { get; set; }

    /// <summary>User attribution, <c>SET NULL</c> (declared in <c>OdysseyContext</c>).</summary>
    public string? RequestedByUserId { get; set; }

    public DateTime QueuedAt { get; set; }

    public DateTime? ClaimedAt { get; set; }

    /// <summary>Saved immediately before the provider call — the only "the sources may have left" marker.</summary>
    public DateTime? TransferStartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>Set on every terminal transition; the retention anchor.</summary>
    public DateTime? FinishedAt { get; set; }

    public DateTime? LeaseExpiresAt { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>The concurrency token: claim, cancel, delete, accept and every worker write bump it.</summary>
    [ConcurrencyCheck]
    public int Generation { get; set; }

    [StringLength(128)]
    public string? FailureCode { get; set; }

    [StringLength(1024)]
    public string? FailureMessage { get; set; }

    // ── Provenance, written once at create from one settings snapshot ──────────────────────────────

    [StringLength(256)]
    public string? AnalyzerModel { get; set; }

    [StringLength(32)]
    public string? PromptVersion { get; set; }

    public int? MaxTokensInForce { get; set; }

    [StringLength(256)]
    public string? AnalyzerBaseUrlOrigin { get; set; }

    [StringLength(256)]
    public string? AnalyzerBaseUrlHost { get; set; }

    [StringLength(128)]
    public string? ProcessorInForce { get; set; }

    [StringLength(128)]
    public string? ProcessorRegionInForce { get; set; }

    [StringLength(128)]
    public string? LawfulBasis { get; set; }

    public bool ConsentRecorded { get; set; }

    [StringLength(128)]
    public string? ConsentMethod { get; set; }

    /// <summary>The server-rendered consent sentence the user affirmed, stored verbatim.</summary>
    [StringLength(1024)]
    public string? ConsentText { get; set; }

    [StringLength(16)]
    public string? DisclosureVersionInForce { get; set; }

    public bool SendsTagNames { get; set; }

    public int? TagVocabularyCount { get; set; }

    public bool? TagVocabularyReduced { get; set; }

    // ── Actors (SET NULL) ───────────────────────────────────────────────────────────────────────────

    public string? CancelledByUserId { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? DeletedByUserId { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime? ContentPurgedAt { get; set; }

    public ICollection<RecipeImportSource> Sources { get; set; } = new List<RecipeImportSource>();

    public ICollection<RecipeImportDraft> Drafts { get; set; } = new List<RecipeImportDraft>();
}

/// <summary>
/// One input of an import — the pasted text, or a file — sent to the processor in <see cref="Ordinal"/> order.
/// Written once at create. On purge or delete <see cref="Text"/> and <see cref="FileId"/> are nulled; the row and
/// its kind, type and size stay as the record of what was sent.
/// </summary>
public class RecipeImportSource
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecipeImportSourceId { get; set; }

    public Guid RecipeImportId { get; set; }

    public int Ordinal { get; set; }

    public RecipeImportSourceKind Kind { get; set; }

    /// <summary><c>longtext</c>: the cap (<see cref="RecipeImportLimits.MaxSourceTextLength"/>) is enforced on write.</summary>
    [Column(TypeName = "longtext")]
    public string? Text { get; set; }

    /// <summary><c>SET NULL</c> to the Files store: deleting the file never deletes the import.</summary>
    public Guid? FileId { get; set; }

    /// <summary>The file was uploaded with this import, so it may be released with it when nothing else uses it.</summary>
    public bool ImportOwned { get; set; }

    [StringLength(128)]
    public string? ContentType { get; set; }

    /// <summary>Post-strip size for an image (§6).</summary>
    public long? SizeBytes { get; set; }

    public RecipeImport? RecipeImport { get; set; }
}

/// <summary>
/// The draft recipe the processor made from an import (one in v1, §4). Never a recipe: it reaches
/// <c>Recipes</c> only through accept, which records <see cref="AcceptedRecipeId"/>.
/// </summary>
public class RecipeImportDraft
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecipeImportDraftId { get; set; }

    public Guid RecipeImportId { get; set; }

    public int Ordinal { get; set; }

    public RecipeImportDraftStatus Status { get; set; }

    /// <summary>Denormalised from the content for the list and its sort; never loads <see cref="DraftJson"/>.</summary>
    [StringLength(RecipeLimits.MaxTitleLength)]
    public string? Title { get; set; }

    /// <summary>The <c>RecipeImportDraftContent</c> JSON, with its <c>schemaVersion</c>. <c>longtext</c>; nulled on purge.</summary>
    [Column(TypeName = "longtext")]
    public string? DraftJson { get; set; }

    /// <summary>A JSON array of warning codes from the closed list.</summary>
    [Column(TypeName = "text")]
    public string? Warnings { get; set; }

    /// <summary><c>SET NULL</c>: deleting the recipe later keeps the record.</summary>
    public Guid? AcceptedRecipeId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? AcceptedByUserId { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public string? DiscardedByUserId { get; set; }

    public DateTime? DiscardedAt { get; set; }

    /// <summary>The optimistic-concurrency token for edit and accept.</summary>
    [ConcurrencyCheck]
    public int Version { get; set; }

    public RecipeImport? RecipeImport { get; set; }
}
