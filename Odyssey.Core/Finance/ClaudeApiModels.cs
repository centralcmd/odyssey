using System.Text.Json.Serialization;

namespace Odyssey.Core.Finance;

// ---- Tool input schema (store_transactions) ----

internal record StoreTransactionsInput(
    [property: JsonPropertyName("transactions")] List<ExtractedTransactionRaw> Transactions
);

internal record ExtractedTransactionRaw(
    [property: JsonPropertyName("transaction_date")] string TransactionDate,
    [property: JsonPropertyName("booking_date")] string? BookingDate,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("merchant")] string? Merchant,
    [property: JsonPropertyName("category_hint")] string? CategoryHint,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("external_id")] string? ExternalId,
    [property: JsonPropertyName("reference_number")] string? ReferenceNumber,
    [property: JsonPropertyName("confidence")] decimal? Confidence
);

// ---- Match step (match_transactions tool) ----

// The reference lists + candidates sent to the model (names only; refs are opaque tokens).
internal record MatchToolPayload(
    [property: JsonPropertyName("contacts")] List<VocabRef> Contacts,
    [property: JsonPropertyName("tags")] List<VocabRef> Tags,
    [property: JsonPropertyName("candidates")] List<MatchCandidatePayload> Candidates
);

internal record VocabRef(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("name")] string Name,
    // Issue #433. Omitted when null, so a names-only request stays byte-identical to the old one.
    [property: JsonPropertyName("aliases"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Aliases = null,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Description = null
);

internal record MatchCandidatePayload(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("merchant")] string? Merchant,
    [property: JsonPropertyName("category")] string? Category
);

internal record MatchTransactionsInput(
    [property: JsonPropertyName("matches")] List<MatchResultRaw> Matches
);

internal record MatchResultRaw(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("contact_ref")] string? ContactRef,
    [property: JsonPropertyName("contact_confidence")] decimal? ContactConfidence,
    [property: JsonPropertyName("tag_refs")] List<string>? TagRefs,
    [property: JsonPropertyName("category_confidence")] decimal? CategoryConfidence
);
