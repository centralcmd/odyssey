using System.Text.Json.Serialization;

namespace Odyssey.Core.Finance;

// ---- Request models ----

internal record ClaudeRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("tools")] List<ClaudeTool> Tools,
    [property: JsonPropertyName("tool_choice")] ClaudeToolChoice ToolChoice,
    [property: JsonPropertyName("messages")] List<ClaudeMessage> Messages
);

internal record ClaudeTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("input_schema")] object InputSchema
);

internal record ClaudeToolChoice(
    [property: JsonPropertyName("type")] string Type
);

internal record ClaudeMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] List<ClaudeContent> Content
);

internal abstract record ClaudeContent(
    [property: JsonPropertyName("type")] string Type
);

internal record ClaudeTextContent(string Text)
    : ClaudeContent("text")
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = Text;
}

internal record ClaudeDocumentContent(ClaudeDocumentSource Source)
    : ClaudeContent("document")
{
    [JsonPropertyName("source")]
    public ClaudeDocumentSource Source { get; init; } = Source;
}

internal record ClaudeDocumentSource(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("data")] string Data
);

// ---- Response models ----

internal record ClaudeResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("stop_reason")] string StopReason,
    [property: JsonPropertyName("content")] List<ClaudeResponseContent> Content,
    [property: JsonPropertyName("usage")] ClaudeUsage? Usage
);

internal record ClaudeResponseContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("input")] System.Text.Json.JsonElement? Input
);

internal record ClaudeUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens
);

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
    [property: JsonPropertyName("name")] string Name
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
