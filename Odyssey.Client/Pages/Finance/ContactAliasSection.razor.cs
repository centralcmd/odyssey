using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

public partial class ContactAliasSection
{
    [Parameter, EditorRequired] public ExistingContact Contact { get; set; } = default!;

    /// <summary>The caller holds <c>contacts.create</c> — gates <b>Add alias</b> and the empty line's copy.</summary>
    [Parameter] public bool CanCreate { get; set; }

    /// <summary>The caller holds <c>contacts.update</c> — gates Edit.</summary>
    [Parameter] public bool CanUpdate { get; set; }

    /// <summary>
    /// The caller holds <c>contacts.delete</c> — gates Delete. Distinct from
    /// <see cref="CanUpdate"/> on purpose: ContactDetailPanel today gates Edit <i>and</i> Delete on
    /// one flag, so a <c>contacts.update</c>-without-<c>.delete</c> principal is offered a Delete that
    /// 403s. Do not inherit that.
    /// </summary>
    [Parameter] public bool CanDelete { get; set; }

    /// <summary>Raised after any alias mutation so the host can refresh the row (UpdatedAt + the list).</summary>
    [Parameter] public EventCallback<Guid> OnChanged { get; set; }

    /// <summary>
    /// An add-alias request driven from the card's ⋯ menu (a fresh nonce per request), travelling the
    /// same path the three contact-method kinds do.
    /// </summary>
    [Parameter] public (string Kind, Guid Nonce)? AddRequest { get; set; }

    [Parameter] public EventCallback OnAddConsumed { get; set; }

    /// <summary>
    /// Routes an outcome to the card's single polite OdsLiveAnnouncer. This component hosts no region
    /// of its own: ContactsCard already owns exactly one, and a component-level Assertive parameter is
    /// fixed at render rather than per message, so a second region would not buy a per-message choice.
    /// </summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    private List<ExistingContactAlias> _aliases = new();
    private Guid _loadedId;

    private bool _dialogOpen;
    private Guid _dialogKey;
    private ExistingContactAlias? _dialogTarget;
    private Guid _consumedNonce;

    private bool IsArchived => Contact.Archived is not null;

    protected override async Task OnParametersSetAsync()
    {
        // Aliases arrive INLINE on the contact (§6), so the section seeds from the already-loaded
        // record rather than fetching — which is why it has no load-failure state: aliases fail with
        // the contact.
        if (_loadedId != Contact.ContactId)
        {
            _loadedId = Contact.ContactId;
            _aliases = [.. Contact.Aliases];
        }

        if (AddRequest is { } request
            && request.Kind == "alias"
            && request.Nonce != _consumedNonce
            && CanCreate && !IsArchived)
        {
            _consumedNonce = request.Nonce;
            OpenDialog(null);
            await OnAddConsumed.InvokeAsync();
        }
    }

    // The copy items are UNCONDITIONAL, mirroring ContactDetailPanel.TileMenu — which is what keeps
    // the menu from ever being empty, so the ⋯ trigger is always rendered and the tile never becomes
    // a dead target. Edit and Delete carry distinct gates.
    private IReadOnlyList<OdsMenuItem> TileMenu(ExistingContactAlias alias)
    {
        var items = new List<OdsMenuItem>
        {
            new()
            {
                Icon = "content_copy", Label = "Copy alias",
                OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(alias.Value, "Alias copied to clipboard.")),
            },
        };

        if (CanUpdate && !IsArchived)
        {
            items.Add(new() { Icon = "edit", Label = "Edit", OnClick = EventCallback.Factory.Create(this, () => OpenDialog(alias)) });
        }

        items.Add(new()
        {
            Icon = "fingerprint", TrailingIcon = "content_copy", Label = "Copy ID",
            OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(alias.Id.ToString(), "ID copied to clipboard.")),
        });

        if (CanDelete && !IsArchived)
        {
            items.Add(new() { Divider = true });
            items.Add(new() { Icon = "delete", Label = "Delete", Danger = true, OnClick = EventCallback.Factory.Create(this, () => DeleteAsync(alias)) });
        }

        return items;
    }

    private void OpenDialog(ExistingContactAlias? alias)
    {
        _dialogTarget = alias;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    /// <summary>
    /// Performs the write and returns the message the dialog should render on the value field, or
    /// null on success.
    ///
    /// <para>
    /// The <c>400</c>/<c>409</c>/<c>422</c> family is deliberately <b>not</b> toasted: the user would
    /// otherwise get a snackbar <i>and</i> a field error for the same rejection. A <c>404</c> is the
    /// exception — the contact is gone, so there is no field to attach the message to; it toasts,
    /// closes the dialog and refreshes the row, and is announced explicitly because a bare
    /// MudSnackbarProvider cannot be assumed to announce.
    /// </para>
    /// </summary>
    private async Task<string?> CommitAsync(string value, string? label)
    {
        // Uniqueness is on the VALUE alone, case- and accent-insensitively — the same comparison the
        // service, the unique index and the InMemory tier agree on.
        if (_aliases.Any(a => a.Id != _dialogTarget?.Id && ContactAliasText.Equal(a.Value, value)))
        {
            return "This contact already has that alias.";
        }

        var body = new NewContactAlias { Value = value, Label = label };
        var result = _dialogTarget is { } target
            ? await Contacts.UpdateAliasAsync(Contact.ContactId, target.Id, body)
            : await Contacts.AddAliasAsync(Contact.ContactId, body);

        if (result.IsSuccess)
        {
            await ReloadAsync();
            await OnAnnounce.InvokeAsync(_dialogTarget is null ? $"Alias {value} added." : $"Alias {value} updated.");
            return null;
        }

        if (result.Status == System.Net.HttpStatusCode.NotFound)
        {
            Snackbar.Add("That contact no longer exists.", Severity.Error);
            await OnAnnounce.InvokeAsync("That contact no longer exists.");
            _dialogOpen = false;
            await OnChanged.InvokeAsync(Contact.ContactId);
            return null;
        }

        // The server's own per-field message when it supplied one (its `errors` dictionary joins on
        // `value`, the same key ApiProblem.Errors uses), else its summary.
        return FieldError(result) ?? result.Error ?? "Unable to save the alias.";
    }

    // ApiProblem.ErrorFor is case-insensitive, because ASP.NET keys model-validation errors by the
    // JSON property name whose casing need not match the CLR one — so this reaches both the service's
    // own `value` entry and model validation's `Value`.
    private static string? FieldError(ApiClient.ApiResult result) => result.Problem?.ErrorFor("value");

    /// <summary>
    /// Deleting is IMMEDIATE — no confirmation, matching the sibling tiles, since an alias is cheap
    /// to re-add. A failure is announced explicitly: this path has neither a dialog to hold open nor
    /// a field to focus.
    /// </summary>
    private async Task DeleteAsync(ExistingContactAlias alias)
    {
        var result = await Contacts.DeleteAliasAsync(Contact.ContactId, alias.Id);
        if (result.IsSuccess)
        {
            await ReloadAsync();
            await OnAnnounce.InvokeAsync($"Alias {alias.Value} deleted.");
            return;
        }

        var message = result.Error ?? "Unable to delete the alias.";
        Snackbar.Add(message, Severity.Error);
        await OnAnnounce.InvokeAsync(message);
    }

    private async Task ReloadAsync()
    {
        _aliases = (await Contacts.ListAliasesAsync(Contact.ContactId)).ItemsOrToast(Snackbar, "aliases");
        await OnChanged.InvokeAsync(Contact.ContactId);
        StateHasChanged();
    }
}
