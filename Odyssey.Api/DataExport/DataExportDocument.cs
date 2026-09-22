using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.Api.DataExport;

// Export-only DTOs (issue #160). Deliberately NOT the write DTOs: these are flat table-row
// projections that mirror the stored columns, reference related entities by foreign-key column
// only (never a nested navigation object), and omit anything that would carry file bytes. Enum
// properties keep their enum type and serialize as the stored integer (the DB column form).
//
// The enums here are the API-facing Odyssey.Dtos.Finance copies, never the Odyssey.Context
// ones (issue #392): this file is an API contract reached by [ProducesResponseType], so importing
// the persistence namespace both leaked it onto the OpenAPI surface and let a Dtos-only enum member
// change the export's wire meaning with no compiler error. DataExportService casts across the
// boundary explicitly so any future divergence has to be a deliberate decision.

/// <summary>
/// Top-level export envelope: metadata plus the finance database section.
///
/// Since issue #395 the response is streamed, so nothing ever builds one of these at request time —
/// <see cref="DataExportService"/> writes the same document property by property straight to the
/// response body. This type remains the *shape* contract: it is what
/// <c>[ProducesResponseType]</c> publishes, what a reader deserializes into, and what
/// <c>DataExportStreamingTests</c> re-serializes to prove the streamed bytes are byte-identical to
/// what <see cref="System.Text.Json.JsonSerializer"/> would have produced. Property order here is
/// therefore wire order: add new members at the end of the relevant class, and keep
/// <see cref="Complete"/> last of all.
/// </summary>
public sealed class DataExportDocument
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>UTC instant the export was produced (also used for the download filename).</summary>
    public DateTime ExportedAt { get; init; }

    public string ExportedByUserId { get; init; } = string.Empty;

    public string Format { get; init; } = "odyssey.database-export.v1";

    public DataExportExclusions Exclusions { get; init; } = new();

    public DataExportDatabases Databases { get; init; } = new();

    /// <summary>
    /// Terminal completeness sentinel — written last, and only once every table has been written in
    /// full. A streamed response cannot be turned back into a ProblemDetails once the first byte is
    /// out (status and headers are already sent), so a mid-stream failure leaves the client holding a
    /// truncated body. Truncation usually shows up as a JSON parse error, but not always: cutting
    /// between two rows can leave something that still parses. A reader must treat a document without
    /// <c>"complete": true</c> as partial and discard it.
    /// </summary>
    public bool Complete { get; init; }
}

/// <summary>Declares what the export intentionally leaves out, for the reader's benefit.</summary>
public sealed class DataExportExclusions
{
    public bool FileContentsExcluded { get; init; } = true;

    /// <summary>Specific Finance tables deliberately left out of the export below (size/privacy) —
    /// distinct from <see cref="OutOfScopeDatabases"/>, whose tables this export does not attempt to
    /// cover at all.</summary>
    public IReadOnlyList<string> ExcludedTables { get; init; } =
        [
            "FileBlob",
            "FileAnalysisJobs",
            "FileAnalysisCandidateTransactions",
            "FileAnalysisCandidateTags",
        ];

    public IReadOnlyList<string> ExcludedFields { get; init; } = ["FileBlob.Content"];

    /// <summary>
    /// Whole databases/contexts this export does not cover at all (issue #160 follow-up) — this is not
    /// a full-database backup. A reader should not assume Identity or Journal data is captured
    /// elsewhere by this export just because it is absent from <see cref="ExcludedTables"/>.
    /// </summary>
    public IReadOnlyList<string> OutOfScopeDatabases { get; init; } =
        [
            "Application (Identity: users, roles, profiles, preferences, system settings)",
            "Journal (journal entries, tasks, photos, albums, calendars, tags) — except Contacts, " +
            "which is exported below under \"finance\" for backward compatibility even though the " +
            "Contact entity itself now lives in Journal (issue #325 follow-up). Only the Contact " +
            "row itself is exported: its detail tables (PersonDetails, OrganizationDetails, " +
            "ContactAliases, Addresses, EmailAddresses, PhoneNumbers) are served by the vCard " +
            "export at GET /api/contacts/vcard instead",
        ];
}

/// <summary>Container keyed by database. Only <c>finance</c> is in scope today — see
/// <see cref="DataExportExclusions.OutOfScopeDatabases"/> for what is not.</summary>
public sealed class DataExportDatabases
{
    public FinanceDatabaseExport Finance { get; init; } = new();
}

/// <summary>
/// The finance database section — one collection per exported table. <see cref="ContactExport"/> is
/// included here even though the Contact entity itself now lives in OdysseyContext (issue #325
/// follow-up); see <see cref="DataExportExclusions.OutOfScopeDatabases"/>.
/// </summary>
public sealed class FinanceDatabaseExport
{
    public IReadOnlyList<AccountExport> Accounts { get; init; } = [];
    public IReadOnlyList<TermExport> Terms { get; init; } = [];
    public IReadOnlyList<BudgetExport> Budgets { get; init; } = [];
    public IReadOnlyList<BudgetItemExport> BudgetItems { get; init; } = [];
    public IReadOnlyList<ContactExport> Contacts { get; init; } = [];
    public IReadOnlyList<CurrencyExport> Currencies { get; init; } = [];
    public IReadOnlyList<ExchangeRateExport> ExchangeRates { get; init; } = [];
    public IReadOnlyList<TransactionExport> Transactions { get; init; } = [];
    public IReadOnlyList<TransactionTagExport> TransactionTags { get; init; } = [];
    public IReadOnlyList<FileMetadataExport> FileMetadata { get; init; } = [];
    public IReadOnlyList<AccountFileExport> AccountFiles { get; init; } = [];
    public IReadOnlyList<TransactionFileExport> TransactionFiles { get; init; } = [];

    // Issue #33. Everything below was in the database but in neither the export nor
    // ExcludedTables — a silent omission rather than a stated one. ContractParties are
    // (contract, target) link rows: they export the relationship COLUMNS only and never a
    // resolved name, matching the read path's posture for an archived link and the
    // FK-column-only rule the rest of this document follows.
    public IReadOnlyList<AccountEstimateExport> AccountEstimates { get; init; } = [];
    public IReadOnlyList<AccountSmartTagExport> AccountSmartTags { get; init; } = [];
    public IReadOnlyList<TaxStatementExport> TaxStatements { get; init; } = [];
    public IReadOnlyList<TaxStatementTagExport> TaxStatementTags { get; init; } = [];
    public IReadOnlyList<TaxStatementFileExport> TaxStatementFiles { get; init; } = [];
    public IReadOnlyList<ContractExport> Contracts { get; init; } = [];
    public IReadOnlyList<ContractPartyExport> ContractParties { get; init; } = [];
    public IReadOnlyList<ContractFileExport> ContractFiles { get; init; } = [];
    public IReadOnlyList<ContractEventExport> ContractEvents { get; init; } = [];
    public IReadOnlyList<ContractSmartTagExport> ContractSmartTags { get; init; } = [];
}

public sealed class AccountExport
{
    public Guid AccountId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTime Opened { get; init; }
    public string? AccountNumber { get; init; }
    public AccountType AccountType { get; init; }
    public DateTime? Closed { get; init; }
    public DateTime? Archived { get; init; }
    public string CurrencyCode { get; init; } = string.Empty;
}

/// <summary>
/// Time-versioned terms (interest rate, expected return, or fee price) — issue #172, widened to two
/// owners by issue #135. Scalar columns and the two owner relationship columns only, exactly one of
/// which is populated per row; without both, the export would attribute every contract term to no
/// owner at all.
/// </summary>
public sealed class TermExport
{
    public Guid TermId { get; init; }
    public Guid? AccountId { get; init; }
    public Guid? ContractId { get; init; }
    public TermKind TermKind { get; init; }
    public string? Label { get; init; }
    public string? LabelKey { get; init; }
    public TermValueUnit ValueUnit { get; init; }
    public decimal Value { get; init; }
    public string? CurrencyCode { get; init; }
    public Interval? Interval { get; init; }
    public int? IntervalCount { get; init; }
    public DateTime? AnchorDate { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public string? Note { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class BudgetExport
{
    public Guid BudgetId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public DateTime? Archived { get; init; }
    public string BaseCurrencyCode { get; init; } = string.Empty;
}

// A budget item has no name or description of its own (issue #75) — its transaction tag carries
// them, and the tag is exported in its own table. The FK column only, matching the document's rule
// everywhere else; no nested tag.
public sealed class BudgetItemExport
{
    public Guid BudgetItemId { get; init; }
    public Guid BudgetId { get; init; }
    public BudgetCategoryType CategoryType { get; init; }
    public decimal PlannedAmount { get; init; }
    public Guid TransactionTagId { get; init; }
}

public sealed class ContactExport
{
    public Guid ContactId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string NormalizedName { get; init; } = string.Empty;
    public Odyssey.Dtos.ContactType Type { get; init; }
    public string? Description { get; init; }
    public DateTime? Archived { get; init; }
}

public sealed class CurrencyExport
{
    public string CurrencyCode { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int MinorUnits { get; init; }
    public string? Symbol { get; init; }
    public DateTime? Archived { get; init; }
}

public sealed class ExchangeRateExport
{
    public Guid ExchangeRateId { get; init; }
    public string FromCurrencyCode { get; init; } = string.Empty;
    public string ToCurrencyCode { get; init; } = string.Empty;
    public decimal Rate { get; init; }
    public DateTime AsOf { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class TransactionExport
{
    public Guid TransactionId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public DateTime TimeStamp { get; init; }
    public Guid AccountId { get; init; }
    public IReadOnlyList<Guid> TransactionTagIds { get; init; } = [];
    public Guid? ContactId { get; init; }
    public string? ExternalId { get; init; }
    public string? InternalId { get; init; }
    public string? ExtraData { get; init; }
    public TransactionStatus Status { get; init; }
    public string? StatusComment { get; init; }
    public DateTime StatusChangedAt { get; init; }
    public string CurrencyCode { get; init; } = string.Empty;
}

public sealed class TransactionTagExport
{
    public Guid TransactionTagId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime? Archived { get; init; }
}

/// <summary>
/// File metadata rows — identifiers, name, MIME/content type, size, hash, timestamps, and the
/// <see cref="FileBlobId"/> relationship column. The blob payload (<c>FileBlob.Content</c>) is
/// never projected.
/// </summary>
public sealed class FileMetadataExport
{
    public Guid Id { get; init; }
    public string? UploadedByUserId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Sha256Hash { get; init; } = string.Empty;
    public Guid FileBlobId { get; init; }
    public string? Description { get; init; }
    public DateTime UploadedAtUtc { get; init; }
}

public sealed class AccountFileExport
{
    public Guid Id { get; init; }
    public Guid AccountId { get; init; }
    public Guid FileMetadataId { get; init; }
    public string? AttachedByUserId { get; init; }
    public DateTime AttachedAtUtc { get; init; }
    public AccountFileType FileType { get; init; }
}

public sealed class TransactionFileExport
{
    public Guid Id { get; init; }
    public Guid TransactionId { get; init; }
    public Guid FileMetadataId { get; init; }
    public string? AttachedByUserId { get; init; }
    public DateTime AttachedAtUtc { get; init; }
    public TransactionFileType Type { get; init; }
}

// ── Issue #33: the tables the export used to omit silently ────────────────────

/// <summary>
/// Time-versioned account value estimates — the sibling of <see cref="TermExport"/> for
/// accounts whose worth is appraised rather than derived from a rate.
/// </summary>
public sealed class AccountEstimateExport
{
    public Guid AccountEstimateId { get; init; }
    public Guid AccountId { get; init; }
    public decimal Value { get; init; }
    public string? CurrencyCode { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public string? Note { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

/// <summary>
/// Auto-tagging rules: the tags applied to transactions imported into an account. The table's key is
/// the <c>(AccountId, TransactionTagId)</c> pair — there is no surrogate id to export.
/// </summary>
public sealed class AccountSmartTagExport
{
    public Guid AccountId { get; init; }
    public Guid TransactionTagId { get; init; }
    public DateTime AddedAt { get; init; }
}

/// <summary>
/// A contract's saved tag filter (issue #166): the transaction tags the contract watches. The table's
/// key is the <c>(ContractId, TransactionTagId)</c> pair — there is no surrogate id to export.
/// </summary>
public sealed class ContractSmartTagExport
{
    public Guid ContractId { get; init; }
    public Guid TransactionTagId { get; init; }
    public DateTime AddedAt { get; init; }
}

/// <summary>
/// A fiscal-year tax statement: the declared figures, the assessment and the settlement. Wholly
/// user-authored financial data, and among the strongest omissions issue #33 found.
/// </summary>
public sealed class TaxStatementExport
{
    public Guid TaxStatementId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int FiscalYear { get; init; }
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public decimal? DeclaredTotalAssets { get; init; }
    public decimal? DeclaredTotalLiabilities { get; init; }
    public decimal? DeclaredNetWorth { get; init; }
    public decimal? DeclaredTotalIncome { get; init; }
    public decimal? AssessedTax { get; init; }
    public decimal? SettlementAmount { get; init; }
    public DateTime? SettledAtUtc { get; init; }
    public DateTime? FiledAtUtc { get; init; }
    public DateTime? TaxOfficeApprovedAtUtc { get; init; }
    public TaxStatementStatus Status { get; init; }
    public string? StatusComment { get; init; }
    public DateTime StatusChangedAt { get; init; }
    public string? Notes { get; init; }
    public DateTime? Archived { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class TaxStatementTagExport
{
    public Guid Id { get; init; }
    public Guid TaxStatementId { get; init; }
    public Guid TransactionTagId { get; init; }
    public TaxStatementTagRole Role { get; init; }
}

public sealed class TaxStatementFileExport
{
    public Guid Id { get; init; }
    public Guid TaxStatementId { get; init; }
    public Guid FileMetadataId { get; init; }
    public string? AttachedByUserId { get; init; }
    public DateTime AttachedAtUtc { get; init; }
    public TaxStatementFileType FileType { get; init; }
}

public sealed class ContractExport
{
    public Guid ContractId { get; init; }
    public string Name { get; init; } = string.Empty;
    public ContractType Type { get; init; }
    public string? Description { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public DateTime? CompletionDate { get; init; }
    public DateTime? Archived { get; init; }

    /// <summary>
    /// When the contract was paused, or null when it is not (issue #140). A stored fact about the
    /// contract that the export silently omitted would be an incomplete subject-access response.
    /// </summary>
    public DateTime? Paused { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

/// <summary>
/// A contract party: one-of-two, an <see cref="AccountId"/> or a <see cref="ContactId"/>. The table
/// carries no kind discriminator — which column is set is what says which — so both nullable
/// relationship columns are exported and neither target's name is resolved, following the
/// FK-column-only rule the rest of this document uses.
/// </summary>
public sealed class ContractPartyExport
{
    public Guid ContractPartyId { get; init; }
    public Guid ContractId { get; init; }
    public Guid? AccountId { get; init; }
    public Guid? ContactId { get; init; }

    /// <summary>What the linked record does in the agreement (issue #121). Orthogonal to which column is set.</summary>
    public ContractPartyRole Role { get; init; }

    /// <summary>The party's own term; both null is the contract's extent, not an unset value.</summary>
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
}

public sealed class ContractFileExport
{
    public Guid ContractFileId { get; init; }
    public Guid ContractId { get; init; }
    public Guid FileMetadataId { get; init; }
    public ContractFileType FileType { get; init; }
    public string? AttachedByUserId { get; init; }
    public DateTime AttachedAtUtc { get; init; }
}

/// <summary>
/// One entry in a contract's user-maintained event log (issue #138). <b>All three</b> free-text fields
/// are exported, <see cref="Notes"/> included: the timeline does not display it, but that is a
/// presentation rule and not an access one, and a field the subject typed that a subject-access
/// response omitted would be the export's own silent hole (§7.10, §4.1).
///
/// <para>
/// <see cref="CreatedByUserId"/> is exported as the raw column, matching every other attribution
/// column in this document — the API's read path resolves a display label instead, but an export is a
/// dump of the rows and the FK-column-only rule applies here as everywhere else.
/// </para>
/// </summary>
public sealed class ContractEventExport
{
    public Guid ContractEventId { get; init; }
    public Guid ContractId { get; init; }
    public ContractEventType Type { get; init; }

    /// <summary>
    /// Whether the row was hand-written or recorded by the server (issue #154 §4). Exported because
    /// without it the two kinds of line are indistinguishable in the dump — which is precisely the
    /// distinction the column exists to create.
    /// </summary>
    public ContractEventSource Source { get; init; }

    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Notes { get; init; }
    public DateTime OccurredAt { get; init; }
    public string? CreatedByUserId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}
