using System.Text.Json.Serialization;

namespace Odyssey.Core.Ai;

// The Anthropic Messages API wire shapes, shared by every Claude flow (statement analysis, AI recipe
// import #602). Flow-specific tool inputs stay with their flow.

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

internal record ClaudeImageContent(ClaudeDocumentSource Source)
    : ClaudeContent("image")
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

// ---- Error envelope ----
// Only the `type` is ever read (never the message): classification is by status and error type, and
// no provider text reaches a log, an exception or a stored field (#602 §3.1).

internal record ClaudeErrorEnvelope(
    [property: JsonPropertyName("error")] ClaudeErrorBody? Error
);

internal record ClaudeErrorBody(
    [property: JsonPropertyName("type")] string? Type
);
