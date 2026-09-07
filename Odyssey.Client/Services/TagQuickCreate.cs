using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;

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

public sealed class TagQuickCreate<TTag>(ITagsApiClient<TTag> tags, ISnackbar snackbar) : ITagQuickCreate<TTag>
{
    private readonly List<Task> _pending = [];
    private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _staged = new(StringComparer.Ordinal);

    public Action<string>? OnCreateFailed { get; set; }

    public OdsOption? Begin(string name)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
            return null;
        if (clean.Length > TagWrite.MaxNameLength)
            clean = clean[..TagWrite.MaxNameLength];

        var tempId = Guid.NewGuid().ToString();
        _staged.Add(tempId);
        _pending.Add(CreateAsync(clean, tempId));
        return new OdsOption(tempId, clean);
    }

    public Task WhenSettledAsync() => Task.WhenAll(_pending.ToArray());

    public string? Resolve(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        if (!_staged.Contains(id))
            return id;
        return _resolved.GetValueOrDefault(id);
    }

    private async Task CreateAsync(string name, string tempId)
    {
        try
        {
            var result = await tags.CreateAsync(new TagWrite(name, null, false));
            if (result.IsSuccess && result.CreatedId is { } id)
            {
                _resolved[tempId] = id.ToString();
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
            OnCreateFailed?.Invoke(tempId);
        }
        catch (Exception ex)
        {
            snackbar.Add($"Couldn’t create “{name}”: {ex.Message}", Severity.Error);
            OnCreateFailed?.Invoke(tempId);
        }
    }
}
