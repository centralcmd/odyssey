using System.Globalization;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Models;

/// <summary>
/// The analyze dialog's copy that is composed from SERVER facts (issue #434): the consent gate's
/// matching bullet from the disclosure's field list, the one-line match summary from the job's counts,
/// and the wait in a <c>429</c>'s <c>Retry-After</c> in words. Pure functions, so every state is a test.
/// </summary>
public static class FileAnalysisDisclosureText
{
    /// <summary>The only coverage this client knows how to describe.</summary>
    public const string AllActiveCoverage = "allActive";

    private static readonly IReadOnlyDictionary<string, string> FieldPhrases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["contactName"] = "contact names",
        ["contactAlias"] = "the aliases saved on your contacts",
        ["tagName"] = "tag names",
        ["tagDescription"] = "tag descriptions",
    };

    /// <summary>
    /// Whether the disclosure cannot be described: no field list, a field this client does not know,
    /// or a coverage other than <see cref="AllActiveCoverage"/>. The gate then reuses its degraded
    /// presentation and the affirmation stays disabled — describing a transfer from a guess is the
    /// defect this state prevents.
    /// </summary>
    public static bool IsIncomplete(FileAnalysisDisclosureDto disclosure) =>
        disclosure.MatchVocabularyFields is not { Count: > 0 } fields
        || fields.Any(field => !FieldPhrases.ContainsKey(field))
        || !string.Equals(disclosure.MatchVocabularyCoverage, AllActiveCoverage, StringComparison.Ordinal);

    /// <summary>The matching bullet's body, or <see langword="null"/> when the disclosure is incomplete.</summary>
    public static string? MatchBullet(FileAnalysisDisclosureDto disclosure)
    {
        if (IsIncomplete(disclosure))
        {
            return null;
        }

        var fields = disclosure.MatchVocabularyFields;
        var phrases = fields.Select(field => FieldPhrases[field]).ToList();
        var sentence = $"After reading the statement, the model matches each transaction to your existing records using {Join(phrases)} — for every active contact and tag.";

        // Only while free text a user wrote is in the list.
        return fields.Contains("contactAlias") || fields.Contains("tagDescription")
            ? sentence + " Aliases and descriptions are text you wrote, so they can contain personal information."
            : sentence;
    }

    /// <summary>
    /// The one summary line after a match, from the job's server counts; <see langword="null"/> when the
    /// job carries none (no match has run under the feature).
    /// </summary>
    public static string? MatchSummary(ExistingFileAnalysisJob job)
    {
        if (job.MatchVocabularyScope == FileAnalysisMatchVocabularyScope.NamesOnly)
        {
            return "Matched on names only, because this analysis was started before aliases and descriptions were included.";
        }

        if (job.ContactCount is not { } contacts || job.TagCount is not { } tags)
        {
            return null;
        }

        var aliases = job.AliasCount ?? 0;
        var described = job.TagDescriptionCount ?? 0;
        var line = $"Matched against {Count(contacts, "contact", "contacts")} ({Count(aliases, "alias", "aliases")}) "
                   + $"and {Count(tags, "tag", "tags")} ({described} with {(described == 1 ? "a description" : "descriptions")}).";

        return job.VocabularyReduction switch
        {
            FileAnalysisVocabularyReduction.AliasesOmitted =>
                line + " Aliases were left out this time because the list was too large.",
            FileAnalysisVocabularyReduction.AliasesAndDescriptionsOmitted =>
                line + " Aliases and tag descriptions were left out this time because the list was too large.",
            _ => line,
        };
    }

    /// <summary>The longest wait the page will state or time; the raw header is never shown.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);

    /// <summary>The re-enable wait when the server named none.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(60);

    /// <summary>The wait a <c>429</c> named, clamped; <see cref="DefaultWait"/> when it named none.</summary>
    public static TimeSpan EffectiveWait(TimeSpan? retryAfter) =>
        retryAfter is { } wait && wait > TimeSpan.Zero ? (wait > MaxWait ? MaxWait : wait) : DefaultWait;

    /// <summary>"in less than a minute" / "in {m} minute(s)" / "in about {h} hour(s)" / "in a few minutes".</summary>
    public static string When(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } wait || wait <= TimeSpan.Zero)
        {
            return "in a few minutes";
        }

        if (wait > MaxWait)
        {
            wait = MaxWait;
        }

        if (wait < TimeSpan.FromMinutes(1))
        {
            return "in less than a minute";
        }

        if (wait < TimeSpan.FromHours(2))
        {
            var minutes = (int)Math.Ceiling(wait.TotalMinutes);
            return $"in {minutes} minute{(minutes == 1 ? "" : "s")}";
        }

        var hours = (int)Math.Ceiling(wait.TotalHours);
        return $"in about {hours} hour{(hours == 1 ? "" : "s")}";
    }

    /// <summary>The <c>429</c> message, neutral about the window, which is deploy configuration.</summary>
    public static string RateLimited(TimeSpan? retryAfter) =>
        $"You’ve reached the limit for analyses for now. You can try again {When(retryAfter)}.";

    /// <summary>The <c>409 file_analysis_destination_changed</c> message. Nothing was sent.</summary>
    public const string DestinationChanged =
        "The analysis provider settings changed after this statement was analysed, so nothing was sent. Start a new analysis to match it.";

    private static string Count(int n, string one, string many) =>
        n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
