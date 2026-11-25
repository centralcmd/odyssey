using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>How an import reads on screen: its display status (server status + its drafts), a chip in words.</summary>
public sealed record RecipeImportDisplay(string Key, string Label, OdsChipTone Tone, string Icon);

/// <summary>
/// The words of AI recipe import (issue #604; design system components-recipes.md · AI recipe import), in one place
/// so the composer, the page and the review dialog never phrase one thing two ways. Every number in a sentence is
/// passed in from <c>GET /api/recipe-imports/options</c> or the problem details, never a client constant.
/// </summary>
public static class RecipeImportText
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    private static readonly Dictionary<string, string> TypeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = "PDF",
        ["image/jpeg"] = "JPEG photo",
        ["image/png"] = "PNG image",
        ["image/webp"] = "WebP image",
        ["text/plain"] = "Text file",
    };

    /// <summary>A content type in words — never a raw MIME type on screen.</summary>
    public static string TypeLabel(string? contentType) =>
        contentType is not null && TypeLabels.TryGetValue(contentType, out var label) ? label : "File";

    /// <summary>"PDF, JPEG photo, PNG image, WebP image or Text file", from the server's list.</summary>
    public static string TypeList(IEnumerable<string> contentTypes)
    {
        var labels = contentTypes.Select(TypeLabel).Distinct().ToList();
        return labels.Count switch
        {
            0 => "file",
            1 => labels[0],
            _ => $"{string.Join(", ", labels[..^1])} or {labels[^1]}",
        };
    }

    /// <summary>Decimal megabytes, one decimal, as the server's byte caps are decimal: 3 500 000 → "3.5 MB".</summary>
    public static string Megabytes(long bytes) =>
        $"{Math.Round(bytes / 1_000_000.0, 1).ToString("0.#", En)} MB";

    public static string Count(int n) => n.ToString("N0", En);

    public static string ContentTypeIcon(string? contentType) =>
        contentType == "application/pdf" ? "picture_as_pdf"
        : contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? "image"
        : "notes";

    /// <summary>The content type an upload will be validated as, from its name (the server re-checks the bytes).</summary>
    public static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".txt" => "text/plain",
            _ => "application/octet-stream",
        };

    /// <summary>The display status (frontend spec #604 §3): server status plus the draft counts.</summary>
    public static RecipeImportDisplay Display(RecipeImportSummary summary) => summary.Status switch
    {
        RecipeImportStatus.Queued => new("waiting", "Waiting", OdsChipTone.Pending, "schedule"),
        RecipeImportStatus.Running => new("reading", "Reading", OdsChipTone.Info, "auto_awesome"),
        RecipeImportStatus.Failed => new("failed", "Failed", OdsChipTone.Error, "error_outline"),
        RecipeImportStatus.Cancelled => new("cancelled", "Cancelled", OdsChipTone.Outline, "block"),
        _ when summary.PendingDraftCount > 0 => new("ready", "Ready to review", OdsChipTone.Income, "rate_review"),
        _ when summary.AcceptedDraftCount > 0 => new("created", "Recipe created", OdsChipTone.Outline, "task_alt"),
        _ => new("discarded", "Draft discarded", OdsChipTone.Outline, "delete_sweep"),
    };

    public static bool IsOpen(RecipeImportStatus status) => status is RecipeImportStatus.Queued or RecipeImportStatus.Running;

    /// <summary>A warning code as a sentence, and the step it is about; unknown codes are dropped (an allow-list).</summary>
    public static (string Text, int? Step)? Warning(string code) => code switch
    {
        RecipeImportWarningCodes.ServingsMissing => ("Servings weren’t in the source — fill them in.", 0),
        RecipeImportWarningCodes.AmountUnclear => ("Some amounts were unclear — check them against the source.", 1),
        RecipeImportWarningCodes.AmountRange => ("A range was given; the lower amount is used and the range kept in the name.", 1),
        RecipeImportWarningCodes.UnitUnmapped => ("A unit wasn’t recognised; it is kept in the ingredient name.", 1),
        RecipeImportWarningCodes.Illegible => ("Parts of a page were hard to read.", null),
        RecipeImportWarningCodes.MultipleRecipesFound => ("The source has more than one recipe; only the first was drafted.", null),
        RecipeImportWarningCodes.ContainsUrl => ("A web address appears in the text. It is kept as plain text.", null),
        _ when code.StartsWith(RecipeImportWarningCodes.TruncatedPrefix, StringComparison.Ordinal) => Truncated(code),
        _ => null,
    };

    private static (string Text, int? Step)? Truncated(string code) =>
        code[RecipeImportWarningCodes.TruncatedPrefix.Length..] switch
        {
            "ingredients" or "ingredient" => ("Some ingredients were too long or too many and were shortened.", 1),
            "steps" or "step" => ("Some steps were too long or too many and were shortened.", 2),
            "title" => ("The title was too long and was shortened.", 0),
            "description" or "notes" or "source" or "servingsLabel" or "section" => ("A field was too long and was shortened.", 0),
            _ => null,
        };

    /// <summary>A worker failure code in plain words (#604 §7); an unknown code reads as a generic failure.</summary>
    public static string Failure(string? code) => code switch
    {
        RecipeImportFailureCodes.NotStarted => "The import wasn’t picked up in time.",
        RecipeImportFailureCodes.Disabled => "AI recipe import was turned off before your import was sent.",
        RecipeImportFailureCodes.DestinationChanged => "The AI service’s address changed before sending. Start a new import to confirm.",
        RecipeImportFailureCodes.DisclosureChanged => "The processor details changed before sending. Start a new import to confirm the new details.",
        RecipeImportFailureCodes.RequesterRevoked => "The person who started this import no longer has access.",
        RecipeImportFailureCodes.SourceMissing => "A source file was deleted or moved before sending.",
        RecipeImportFailureCodes.SourceInvalid => "One of the photos couldn’t be prepared for sending.",
        RecipeImportFailureCodes.CredentialUnavailable => "The AI service isn’t configured on this server. Ask an administrator.",
        RecipeImportFailureCodes.DocumentRejected => "The AI service couldn’t read one of the documents — too many pages, password-protected or damaged.",
        RecipeImportFailureCodes.Truncated => "The recipe was too long for one draft. Try sending fewer pages.",
        RecipeImportFailureCodes.NoRecipeFound => "No recipe was found in what you sent.",
        RecipeImportFailureCodes.MalformedOutput => "The AI’s answer couldn’t be read. Start a new import to try again.",
        _ => "The AI service didn’t answer. Start a new import to try again.",
    };

    /// <summary>"14 min ago", "3 h ago", "2 days ago".</summary>
    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var minutes = Math.Max(0, (int)Math.Round((nowUtc - utc).TotalMinutes));
        return minutes switch
        {
            < 1 => "just now",
            < 60 => $"{minutes} min ago",
            < 60 * 24 => $"{(int)Math.Round(minutes / 60.0)} h ago",
            _ => $"{(int)Math.Round(minutes / 1440.0)} days ago",
        };
    }

    /// <summary>"in 3 days" / "today" while content is kept; null once purged or while still open.</summary>
    public static string? Expires(DateTime? expiresAt, DateTime nowUtc)
    {
        if (expiresAt is not { } at)
        {
            return null;
        }

        var left = (int)Math.Ceiling((at - nowUtc).TotalDays);
        return left <= 0 ? "today" : $"in {left} {(left == 1 ? "day" : "days")}";
    }
}
