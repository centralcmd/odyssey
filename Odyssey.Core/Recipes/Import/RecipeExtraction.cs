using Odyssey.Dtos.Recipes;

namespace Odyssey.Core.Recipes.Import;

/// <summary>One source, as sent: text (pasted or a decoded text file) or the stripped bytes of a PDF or image.</summary>
public sealed record RecipeExtractionSource(RecipeImportSourceKind Kind, string? Text, byte[]? Bytes, string? ContentType);

/// <summary>A tag the model may suggest, referenced by an opaque <c>t1…tn</c> token, never by id.</summary>
public sealed record RecipeExtractionTag(string Ref, string Name);

/// <summary>Everything one Messages request is built from — the import's stamps, not live values.</summary>
public sealed record RecipeExtractionRequest(
    IReadOnlyList<RecipeExtractionSource> Sources,
    IReadOnlyList<RecipeExtractionTag> Tags,
    string Model,
    string BaseUrl,
    int MaxTokens);

public enum RecipeExtractionOutcome
{
    /// <summary>The forced tool answered with a schema-valid input (zero or more recipes).</summary>
    Extracted,

    /// <summary><c>stop_reason = max_tokens</c>.</summary>
    Truncated,

    /// <summary>The tool was missing, or its input was not schema-valid.</summary>
    Malformed,
}

/// <summary>The model's answer, before sanitising (§13 output schema).</summary>
public sealed record RecipeExtractionResult(RecipeExtractionOutcome Outcome, IReadOnlyList<ExtractedRecipe> Recipes)
{
    public static RecipeExtractionResult Truncated { get; } = new(RecipeExtractionOutcome.Truncated, []);

    public static RecipeExtractionResult Malformed { get; } = new(RecipeExtractionOutcome.Malformed, []);
}

public sealed record ExtractedRecipe(
    string Title,
    string? Description,
    string? Notes,
    string? Source,
    int? Servings,
    string? ServingsLabel,
    int? PrepMinutes,
    int? CookMinutes,
    IReadOnlyList<ExtractedIngredient> Ingredients,
    IReadOnlyList<ExtractedStep> Steps,
    IReadOnlyList<string> TagRefs,
    IReadOnlyList<string> Warnings);

public sealed record ExtractedIngredient(
    string? Section, decimal? Amount, decimal? AmountMax, IngredientUnit Unit, string? UnitText, string Name, bool Optional);

public sealed record ExtractedStep(string? Section, string Text);

/// <summary>Why the provider refused or failed, classified from the HTTP status and error type only (§9).</summary>
public enum RecipeExtractionFailure
{
    /// <summary>A non-2xx, a timeout or an open breaker, after the one retry.</summary>
    ProviderError,

    /// <summary>The provider refused a document it could not read (a <c>400 invalid_request_error</c> or a <c>413</c>).</summary>
    DocumentRejected,
}

/// <summary>A provider failure with no provider text in it.</summary>
public sealed class RecipeExtractionException(RecipeExtractionFailure failure, string message) : Exception(message)
{
    public RecipeExtractionFailure Failure { get; } = failure;
}

/// <summary>
/// The recipe-import model seam (issue #602 §3.2). Tests stub it; the Claude implementation builds the prompt and the
/// content blocks and parses <c>store_recipes</c>.
/// </summary>
public interface IRecipeExtractionProvider
{
    /// <exception cref="RecipeExtractionException">The provider failed or refused; nothing of its body is kept.</exception>
    /// <exception cref="Odyssey.Core.Ai.AiCredentialUnavailableException">No usable key; nothing was sent.</exception>
    Task<RecipeExtractionResult> ExtractAsync(RecipeExtractionRequest request, CancellationToken cancellationToken);
}
