namespace Odyssey.Dtos.Recipes;

/// <summary>
/// An ingredient's unit (issue #525 §8.2). Ordinals are a wire and persistence contract: never renumbered, and a
/// retired member leaves a permanent hole. Metric first; <see cref="Cup"/> is the 250 ml metric cup.
/// </summary>
/// <remarks>
/// <see cref="None"/> is a real member — a unitless count ("3 eggs") — not an "unspecified" sentinel. A request
/// carries the unit as a nullable <c>[Required]</c> value, so an omitted unit is a <c>400</c>, never a silent
/// <see cref="None"/>.
/// </remarks>
public enum IngredientUnit
{
    None = 0,
    Milligram = 1,
    Gram = 2,
    Kilogram = 3,
    Millilitre = 4,
    Centilitre = 5,
    Decilitre = 6,
    Litre = 7,
    Teaspoon = 8,
    Tablespoon = 9,
    Cup = 10,
    Piece = 11,
    Clove = 12,
    Slice = 13,
    Can = 14,
    Package = 15,
    Bunch = 16,
    Pinch = 17,
    ToTaste = 18,
}

/// <summary>Which units a servings scale applies to (§8.3).</summary>
public static class IngredientUnitScaling
{
    /// <summary>
    /// Whether an amount in <paramref name="unit"/> scales with servings. <see cref="IngredientUnit.Pinch"/> and
    /// <see cref="IngredientUnit.ToTaste"/> do not; neither does an unknown ordinal.
    /// </summary>
    public static bool IsScalable(IngredientUnit unit) =>
        unit is >= IngredientUnit.None and <= IngredientUnit.Bunch;
}
