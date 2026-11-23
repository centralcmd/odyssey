namespace Odyssey.Api.Tests.ApiDocs;

/// <summary>
/// The reviewed exceptions to <see cref="OpenApiParityTests"/> (issue #569 §8), each keyed by the exact
/// operation and parameter it excuses and carrying its reason. <c>EveryAllowListEntry_NamesAnInventoriedParameter</c>
/// fails when an entry stops naming a real parameter.
/// </summary>
internal static class OpenApiParityAllowList
{
    /// <summary>
    /// Parameters whose <c>required</c> flag the new document states differently, keyed
    /// <c>"METHOD /path|in:name"</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RequiredMismatches =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Schema component names the new generator spells differently, keyed by the inventoried name, with the new
    /// name and the reason.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Reason)> RenamedSchemas =
        new Dictionary<string, (string Name, string Reason)>(StringComparer.Ordinal);
}
