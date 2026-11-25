namespace Odyssey.Api;

/// <summary>
/// The per-user limit on starting a recipe import (issue #602 S4, A18): both create endpoints share one fixed window,
/// the numbers of <see cref="FileAnalysisAnalyzeRateLimitOptions"/>. Deploy-time configuration, for the reason
/// <see cref="FileAnalysisRateLimiting"/> gives.
/// </summary>
public static class RecipeImportRateLimiting
{
    public const string CreatePolicy = "recipe-import-create";

    public static IServiceCollection AddRecipeImportRateLimiter(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPerUserFixedWindowOptions<RecipeImportCreateRateLimitOptions>(configuration, RecipeImportCreateRateLimitOptions.SectionName);
        services.AddRateLimiter(options => options.AddPerUserFixedWindowPolicy<RecipeImportCreateRateLimitOptions>(CreatePolicy));
        return services;
    }
}

/// <summary>Bound from <c>RateLimiting:RecipeImportCreate</c>: 10 imports per user per hour.</summary>
public sealed class RecipeImportCreateRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: Odyssey.Dtos.Recipes.RecipeImportLimits.CreatePermitsPerHour, windowSeconds: 3600)
{
    public const string SectionName = "RateLimiting:RecipeImportCreate";
}
