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

    /// <summary>Fires with a temporary id whose create failed, so the host can drop it from its
    /// selection and re-render.</summary>
    event Action<string>? CreateFailed;
}

public sealed class TagQuickCreate<TTag>(ITagsApiClient<TTag> tags, ISnackbar snackbar) : ITagQuickCreate<TTag>
{
    private readonly List<Task> _pending = [];
    private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _staged = new(StringComparer.Ordinal);

    public event Action<string>? CreateFailed;

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

            // The picker withholds the create row when a listed option already carries the name, so a
            // conflict here means an ARCHIVED tag holds it — which the row cannot select and this
            // cannot un-archive. Say so rather than reporting a bare "create failed".
            var reason = result.Status == System.Net.HttpStatusCode.Conflict
                ? "an archived tag already uses that name."
                : result.Error;
            snackbar.Add($"Couldn’t create “{name}”: {reason}", Severity.Error);
            CreateFailed?.Invoke(tempId);
        }
        catch (Exception ex)
        {
            snackbar.Add($"Couldn’t create “{name}”: {ex.Message}", Severity.Error);
            CreateFailed?.Invoke(tempId);
        }
    }
}
