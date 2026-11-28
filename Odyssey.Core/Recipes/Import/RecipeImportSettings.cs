using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Ai;
using Odyssey.Dtos;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Core.Recipes.Import;

/// <summary>The recipe-import settings (issue #602 §11): the integration switch and the retention period.</summary>
public interface IRecipeImportSettingsLookup
{
    /// <summary>The switch, read live and never cached; any failure reads as off (fail closed).</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>The retention period in days; a missing or unusable value is the compiled default.</summary>
    Task<int> GetRetentionDaysAsync(CancellationToken cancellationToken = default);
}

public sealed class RecipeImportSettingsLookup(OdysseyContext context, ILogger<RecipeImportSettingsLookup> logger)
    : IRecipeImportSettingsLookup
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await ReadAsync(SystemSettingsKeys.RecipeImportEnabled, cancellationToken);
            if (value is null)
            {
                return SystemSettingsDefaults.RecipeImportEnabled;
            }

            if (bool.TryParse(value, out var parsed))
            {
                return parsed;
            }

            logger.LogError("The stored recipe-import enabled flag is not a boolean; treating recipe import as disabled.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A fallback of true would let a failed settings read be the reason a recipe left the deployment.
            logger.LogError(ex, "Reading the recipe-import enabled flag failed; treating recipe import as disabled.");
            return false;
        }
    }

    public async Task<int> GetRetentionDaysAsync(CancellationToken cancellationToken = default)
    {
        var value = await ReadAsync(SystemSettingsKeys.RecipeImportRetentionDays, cancellationToken);
        return int.TryParse(value, out var days)
            && days is >= SystemSettingsBounds.RecipeImportRetentionDaysMin and <= SystemSettingsBounds.RecipeImportRetentionDaysMax
            ? days
            : SystemSettingsDefaults.RecipeImportRetentionDays;
    }

    private Task<string?> ReadAsync(string key, CancellationToken cancellationToken) =>
        context.SystemSettings.AsNoTracking()
            .Where(row => row.Key == key)
            .Select(row => row.Value)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>
/// The per-caller disclosure of a recipe import (issue #602 §5 item 1, A3, S10): its version token and the consent
/// sentence the server renders for it. The client echoes the version and affirms; it never sends consent text.
/// </summary>
public static class RecipeImportDisclosure
{
    private const char Separator = '\u001f';

    private const int Length = 16;

    /// <summary>Bumped whenever <see cref="ConsentText"/>'s wording changes, so an open consent gate is refused.</summary>
    private const string ConsentTemplateVersion = "consent/1";

    /// <summary>How the consent was given, stored on the import.</summary>
    public const string ConsentMethod = "composer-checkbox";

    /// <summary>The version for one settings snapshot and one caller; empty when the snapshot is unusable.</summary>
    public static string Compute(AiProcessorSettings settings, bool sendsTagNames)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Model is not { } model || AiDestination.Host(settings.BaseUrl) is not { Length: > 0 } host)
        {
            return string.Empty;
        }

        var parts = new[]
        {
            settings.Processor, settings.ProcessorRegion, settings.LawfulBasis, settings.PrivacyNoticeUrl, model, host,
            sendsTagNames ? "tags:on" : "tags:off", RecipeImportLimits.PromptVersion, ConsentTemplateVersion,
        };
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(Separator, parts)));
        return Base64Url.EncodeToString(digest)[..Length];
    }

    /// <summary>A null, blank or different echo is a mismatch, never a skip.</summary>
    public static bool Matches(AiProcessorSettings settings, bool sendsTagNames, string? echoed) =>
        !string.IsNullOrWhiteSpace(echoed)
        && string.Equals(Compute(settings, sendsTagNames), echoed, StringComparison.Ordinal);

    /// <summary>The binding consent sentence (S10), rendered from the same snapshot as the version.</summary>
    public static string ConsentText(AiProcessorSettings settings, bool sendsTagNames)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var text = new StringBuilder()
            .Append("I agree to send this recipe text and every file listed above, in full, to ")
            .Append(settings.Processor).Append(" (").Append(settings.ProcessorRegion).Append(") so it can draft a recipe.");
        if (sendsTagNames)
        {
            text.Append(" My household’s recipe tag names are sent too, so it can suggest tags.");
        }

        text.Append(" Lawful basis: ").Append(settings.LawfulBasis).Append('.')
            .Append(" If the service is busy, the request is sent once more. It may refuse a document it cannot read.");
        var sentence = text.ToString();
        return sentence.Length > 1024 ? sentence[..1024] : sentence;
    }
}
