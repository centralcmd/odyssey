using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Services;

/// <summary>
/// The inline "Create ‹name›" path shared by every tag picker (Odyssey Design System · <i>every
/// TagMultiSelect can create, and its create rows name what they make</i>).
///
/// <para>
/// The same staged shape as <see cref="IContactQuickCreate"/>, for the same reason: the picker's
/// create callback is synchronous and has to hand back the option to select in the same gesture, so
/// the option carries a temporary id and the POST runs behind it. A host calls
/// <see cref="WhenSettledAsync"/> before submitting and <see cref="Resolve"/> to map each selected id.
/// </para>
///
/// <para>
/// Closed over the tag resource, so a field only ever mints the vocabulary it searches — a journal-tag
/// field cannot create a transaction tag. That is what the create row's kind caption states, and it is
/// enforced here by which client is injected rather than by a parameter.
/// </para>
/// </summary>
public interface ITagQuickCreate<TTag>
{
    /// <summary>Stage a tag and return the option to select now. Null for a blank name.</summary>
    OdsOption? Begin(string name);

    /// <summary>Await every create staged so far.</summary>
    Task WhenSettledAsync();

    /// <summary>
    /// The real id for a value handed out by <see cref="Begin"/> — the value itself when it was never
    /// one of ours, and <c>null</c> when its create failed.
    /// </summary>
    string? Resolve(string? id);

    /// <summary>
    /// Called with a temporary id whose create failed, so the host can drop it from its options and
    /// clear a selection that pointed at it.
    /// </summary>
    /// <remarks>
    /// A settable callback rather than an event: the creator is registered <b>transient</b>, so it has
    /// exactly one owner for its whole life. An event would invite a second subscriber and oblige
    /// every host to unsubscribe in <c>Dispose</c> — ceremony for a multiplicity that cannot arise.
    /// </remarks>
    Action<string>? OnCreateFailed { get; set; }
}

public sealed class TagQuickCreate<TTag>(
    ITagsApiClient<TTag> tags,
    IReferenceDataCache referenceData,
    INewsCatalogueCache newsCatalogue,
    ISnackbar snackbar) : ITagQuickCreate<TTag>
{
    private readonly QuickCreateLedger ledger = new();

    // Temporary ids whose create has already failed — a create that fails before Begin returns (a synchronous
    // refusal) has fired OnCreateFailed for an option the host has not added yet, so Begin must not hand it out.
    private readonly HashSet<string> failed = new(StringComparer.Ordinal);

    public Action<string>? OnCreateFailed { get; set; }

    public OdsOption? Begin(string name)
    {
        if (QuickCreateLedger.CleanName(name, TagWrite.MaxNameLength) is not { } clean)
        {
            return null;
        }

        var tempId = ledger.Stage(id => CreateAsync(clean, id));
        return failed.Contains(tempId) ? null : new OdsOption(tempId, clean);
    }

    public Task WhenSettledAsync() => ledger.WhenSettledAsync();

    public string? Resolve(string? id) => ledger.Resolve(id);

    private async Task CreateAsync(string name, string tempId)
    {
        try
        {
            var result = await tags.CreateAsync(new TagWrite(name, null, false, Icon: null, Color: null));
            if (result.IsSuccess && result.CreatedId is { } id)
            {
                ledger.Complete(tempId, id);
                InvalidateCachedTags();
                return;
            }

            // The journal, task and photo tag services refuse a name an ACTIVE tag already holds;
            // transaction tags have no such guard. Either way the picker withholds the create row for
            // a name it is already offering, so a conflict means the name is taken by a tag this field
            // could not show — say that rather than reporting a bare "create failed".
            var reason = result.Status == System.Net.HttpStatusCode.Conflict
                ? "that name is already taken by a tag this field can't offer."
                : result.Error;
            snackbar.Add($"Couldn’t create “{name}”: {reason}", Severity.Error);
            failed.Add(tempId);
            OnCreateFailed?.Invoke(tempId);
        }
        catch (Exception ex)
        {
            snackbar.Add($"Couldn’t create “{name}”: {ex.Message}", Severity.Error);
            failed.Add(tempId);
            OnCreateFailed?.Invoke(tempId);
        }
    }

    // Transaction tags are cached for the whole session (issue #372), so a tag minted here would be
    // missing from every later picker and the Transactions filter until a reload — and the picker
    // would offer to create it again (issue #251). News tags are cached the same way by the news
    // catalogue (the News tags page drops that cache on every write). The other tag resources aren't
    // cached.
    private void InvalidateCachedTags()
    {
        if (typeof(TTag) == typeof(ExistingTransactionTag))
        {
            referenceData.InvalidateTransactionTags();
        }
        else if (typeof(TTag) == typeof(ExistingNewsTag))
        {
            newsCatalogue.InvalidateTags();
        }
    }
}

/// <summary>What every host of an inline tag create does around its save.</summary>
public static class TagQuickCreateHost
{
    /// <summary>
    /// The created options re-keyed to their real ids once the creates settled (a failed create drops out), so a
    /// field whose save then fails still offers the tags it minted — under the ids the selection now carries —
    /// instead of showing them as unavailable until a reload.
    /// </summary>
    public static List<OdsOption> ResolveCreated<TTag>(this ITagQuickCreate<TTag> creator, IEnumerable<OdsOption> created) =>
        [.. created.Select(o => creator.Resolve(o.Value) is { } id ? o with { Value = id } : null).OfType<OdsOption>()];

    /// <summary>True when a saved record carries a tag id the page's list does not hold — a tag created inline.</summary>
    public static bool NeedsTagReload<TValue>(IEnumerable<Guid> savedTagIds, IReadOnlyDictionary<Guid, TValue> known) =>
        savedTagIds.Any(id => !known.ContainsKey(id));
}
