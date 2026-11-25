namespace Odyssey.Dtos.Recipes;

/// <summary>
/// AI recipe import's compile-time bounds (issue #602 §5–§6). Named by the entity, the DTO annotations, the
/// service and the client alike — the client reads the effective values from <c>GET /api/recipe-imports/options</c>,
/// which combines the per-type caps with the deploy-time body ceiling. Byte caps are in bytes.
/// </summary>
/// <remarks>
/// The provider limits behind these numbers (request 32 MB, image 5 MB, PDF 32 MB and 100 pages) are planning
/// values from #602 §6 and are re-verified against Anthropic's documentation in the implementing PR; a lower
/// verified limit lowers the constant, never the encoded-budget rule.
/// </remarks>
public static class RecipeImportLimits
{
    public const int MaxSourceTextLength = 100_000;

    public const int MaxFilesPerImport = 10;

    /// <summary>The raw total; the encoded budget below is what is actually checked.</summary>
    public const long MaxTotalBytes = 20_000_000;

    /// <summary>Base64 grows 4/3; with the prompt this stays under the provider's 32 MB request cap.</summary>
    public const long MaxEncodedRequestBytes = 30_000_000;

    /// <summary>A fixed allowance for the prompt and tag vocabulary inside the encoded budget.</summary>
    public const long PromptAllowanceBytes = 64_000;

    public const long MaxPdfBytes = 15_000_000;

    /// <summary>After stripping; ≈ 4.67 MB as base64, under a 5 MB per-image limit in either unit.</summary>
    public const long MaxImageBytes = 3_500_000;

    /// <summary>The raw size an existing Library image may have before it is even read for stripping.</summary>
    public const long MaxExistingImageRawBytes = 15_000_000;

    public const long MaxTextFileBytes = 1_000_000;

    public const int MaxImageDimension = 8000;

    /// <summary>The provider's page limit, shown as guidance; the server does not count pages.</summary>
    public const int MaxPdfPages = 100;

    public const int MaxOpenImportsPerUser = 5;

    public const int MaxRecipesPerImport = 1;

    public const int MaxTagVocabulary = 200;

    public const int MaxTagNameLengthSent = 64;

    public const int MaxTagVocabularyBytes = 8_000;

    public const int CreatePermitsPerHour = 10;

    public const int DraftSchemaVersion = 1;

    public const string PromptVersion = "recipe-import/1.0";

    /// <summary>The types an import accepts, before the administrator's Files-store allow-list narrows them.</summary>
    public static readonly IReadOnlyList<string> ContentTypes =
        ["application/pdf", "image/jpeg", "image/png", "image/webp", "text/plain"];

    public static long MaxBytesFor(string contentType) => contentType switch
    {
        "application/pdf" => MaxPdfBytes,
        "image/jpeg" or "image/png" or "image/webp" => MaxImageBytes,
        "text/plain" => MaxTextFileBytes,
        _ => 0,
    };

    public static bool IsImage(string? contentType) => contentType is "image/jpeg" or "image/png" or "image/webp";

    /// <summary>The bytes a source adds to the encoded request: base64 for a file, UTF-8 for text.</summary>
    public static long EncodedBytes(long rawBytes, bool isFile) => isFile ? (rawBytes + 2) / 3 * 4 : rawBytes;
}
