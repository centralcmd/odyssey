namespace Odyssey.Dtos.Recipes;

/// <summary>
/// Every bound of the Recipes module (issue #525 §8.1), in one place: the entity columns, the request-DTO
/// annotations and the client name these symbols, never a literal (the <c>NewsLimits</c> precedent). This
/// project has zero project references, so the WASM client reads the same constants the server enforces.
/// </summary>
/// <remarks>
/// These are compile-time product limits, not admin policy, so they are not <c>SystemSettings</c> rows.
/// </remarks>
public static class RecipeLimits
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 1000;
    public const int MaxNotesLength = 8000;
    public const int MaxSourceLength = 500;
    public const int MaxServingsLabelLength = 32;
    public const int MaxSectionLength = 64;
    public const int MaxIngredientNameLength = 200;
    public const int MaxPreparationLength = 200;
    public const int MaxStepTextLength = 4000;

    public const int MaxIngredients = 100;
    public const int MaxSteps = 100;
    public const int MaxPhotos = 20;
    public const int MaxTags = 20;

    public const int MinServings = 1;
    public const int MaxServings = 100;

    /// <summary>The upper bound of the read-time <c>?servings=</c> scale factor (§5 item 2).</summary>
    public const int MaxScaleServings = 1000;

    /// <summary>Prep or cook time, in minutes (one week).</summary>
    public const int MaxMinutes = 10080;

    /// <summary>The list's <c>maxTotalMinutes</c> filter: prep + cook, each at most <see cref="MaxMinutes"/>.</summary>
    public const int MaxTotalMinutes = 2 * MaxMinutes;

    /// <summary>Strings, because <c>[Range(typeof(decimal), …)]</c> takes its bounds as text.</summary>
    public const string MinAmount = "0.001";

    /// <inheritdoc cref="MinAmount"/>
    public const string MaxAmount = "999999";

    /// <summary>Decimal places an amount may carry — the <c>decimal(12,3)</c> column's scale.</summary>
    public const int AmountScale = 3;

    /// <summary>The transport cap on a recipe <c>POST</c>/<c>PUT</c> body (§7.11): above the ~2.9 MB worst case.</summary>
    public const int MaxRequestBytes = 4 * 1024 * 1024;

    /// <summary>The amount column's precision (digits) — <c>decimal(12,3)</c>.</summary>
    public const int AmountPrecision = 12;
}
