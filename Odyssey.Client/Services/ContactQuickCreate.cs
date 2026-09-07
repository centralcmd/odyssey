using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using System.Net;

namespace Odyssey.Client.Services;

/// <summary>
/// The inline "Add ‹name›" path shared by every contact picker (Odyssey Design System ·
/// <c>ContactSelect</c> — <i>a picker that can select a contact can add one</i>).
///
/// <para>
/// The picker's create callback is <b>synchronous</b> — it has to hand back the option to select in
/// the same gesture — so a create is staged optimistically: <see cref="Begin"/> returns an option
/// carrying a temporary id straight away and POSTs in the background. The host calls
/// <see cref="WhenSettledAsync"/> before it submits, then <see cref="Resolve"/> to swap the temporary
/// id for the real one. A create that failed resolves to <c>null</c>, so a bogus id is never posted.
/// </para>
///
/// <para>
/// Registered <b>transient</b>: one creator per dialog, because the temporary ids it hands out are
/// only meaningful to the surface that is holding them.
/// </para>
/// </summary>
public interface IContactQuickCreate
{
    /// <summary>
    /// Stage a contact and return the option to select now. <paramref name="kind"/> is the
    /// ContactType key the create row named — it is never guessed after the fact. Returns null for a
    /// blank name.
    /// </summary>
    OdsOption? Begin(string name, string? kind);

    /// <summary>True while any staged create is still in flight.</summary>
    bool HasPending { get; }

    /// <summary>Await every create staged so far. Call this before submitting the host's form.</summary>
    Task WhenSettledAsync();

    /// <summary>
    /// The real id for a value handed out by <see cref="Begin"/> — the value itself when it was never
    /// one of ours, and <c>null</c> when its create failed (so the caller drops the link rather than
    /// posting an id the server never issued).
    /// </summary>
    string? Resolve(string? id);

    /// <summary>Fires with a temporary id whose create failed, so the host can clear a selection that
    /// pointed at it and re-render.</summary>
    event Action<string>? CreateFailed;
}

public sealed class ContactQuickCreate(
    IContactsApiClient contacts,
    IReferenceDataCache referenceData,
    ISnackbar snackbar) : IContactQuickCreate
{
    // Contact.DisplayName / OrganizationDetails.LegalName are both [StringLength(128)]; a typed name
    // over that is truncated rather than rejected, so the gesture still completes.
    private const int MaxNameLength = 128;

    private readonly List<Task> _pending = [];
    private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _staged = new(StringComparer.Ordinal);

    public event Action<string>? CreateFailed;

    public bool HasPending => _pending.Any(t => !t.IsCompleted);

    public OdsOption? Begin(string name, string? kind)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
            return null;
        if (clean.Length > MaxNameLength)
            clean = clean[..MaxNameLength];

        var type = kind == nameof(ContactType.Person) ? ContactType.Person : ContactType.Organization;
        var tempId = Guid.NewGuid().ToString();
        var meta = OdsTypeRegistries.ContactTypeOf(type.ToString());

        _staged.Add(tempId);
        _pending.Add(CreateAsync(clean, type, tempId));

        return new OdsOption(tempId, clean) { Icon = meta.Icon, IconColor = meta.Color };
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

    private async Task CreateAsync(string name, ContactType type, string tempId)
    {
        try
        {
            var result = await contacts.CreateAsync(BuildPayload(name, type));
            if (result.IsSuccess)
            {
                // The session-wide contact cache is now stale — the next picker must re-fetch.
                referenceData.InvalidateContacts();
                if (result.CreatedId is { } id)
                    _resolved[tempId] = id.ToString();
                return;
            }

            // A duplicate name (409) means the contact already exists — link the staged option to the
            // existing record by name rather than failing the whole form.
            if (result.Status == HttpStatusCode.Conflict)
            {
                var existing = (await referenceData.ContactsAsync())
                    .FirstOrDefault(c => c.Archived is null
                        && string.Equals(c.ResolvedDisplayName, name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    _resolved[tempId] = existing.ContactId.ToString();
                    return;
                }
            }

            Fail(tempId, name, result.Error);
        }
        catch (Exception ex)
        {
            Fail(tempId, name, ex.Message);
        }
    }

    private void Fail(string tempId, string name, string? reason)
    {
        snackbar.Add($"Couldn’t create “{name}”: {reason}", Severity.Error);
        CreateFailed?.Invoke(tempId);
    }

    /// <summary>
    /// The create payload for a quick-created contact.
    ///
    /// <para>
    /// An organization takes the typed text as its legal name. A person needs both a first and a last
    /// name (each <c>[Required]</c>), and a picker only ever collects one string — so the text is
    /// split on its last whitespace, and a single-token name additionally carries a
    /// <c>DisplayName</c> override so the record still <i>reads</i> as exactly what was typed. The
    /// duplicated token is the honest floor: the alternative is refusing the create, which is the
    /// dead end the typed rows exist to close.
    /// </para>
    /// </summary>
    internal static NewContact BuildPayload(string name, ContactType type)
    {
        if (type != ContactType.Person)
        {
            return new NewContact
            {
                Type = ContactType.Organization,
                Archived = false,
                OrganizationDetails = new OrganizationDetailsDto { LegalName = name },
            };
        }

        var split = name.LastIndexOf(' ');
        var first = split > 0 ? name[..split].Trim() : name;
        var last = split > 0 ? name[(split + 1)..].Trim() : name;

        return new NewContact
        {
            Type = ContactType.Person,
            Archived = false,
            DisplayName = split > 0 ? null : name,
            PersonDetails = new PersonDetailsDto { FirstName = first, LastName = last },
        };
    }
}
