using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

public partial class ContactDetailPanel
{
    [Parameter, EditorRequired] public ExistingContact Contact { get; set; } = default!;
    [Parameter] public bool CanEdit { get; set; }
    /// <summary>Raised after a contact mutation so the host can refresh the row (counts + UpdatedAt).</summary>
    [Parameter] public EventCallback<Guid> OnChanged { get; set; }

    /// <summary>An add-contact request driven from the row's ⋯ menu (kind + a fresh nonce per request).</summary>
    [Parameter] public (string Kind, Guid Nonce)? AddRequest { get; set; }
    /// <summary>Raised once an <see cref="AddRequest"/> has been consumed so the host can clear it.</summary>
    [Parameter] public EventCallback OnAddConsumed { get; set; }

    private OdsTypeOption _typeMeta = OdsTypeRegistries.ContactTypeOf(null);
    private List<ExistingAddress> _addresses = new();
    private List<ExistingEmailAddress> _emails = new();
    private List<ExistingPhoneNumber> _phones = new();
    private Guid _loadedId;

    private bool _dialogOpen;
    private string _dialogKind = "address";
    private ContactMethodDraft _dialogDraft = new();
    private bool _dialogIsEdit;
    private bool _dialogIsFirst;

    private bool IsArchived => Contact.Archived is not null;
    private int _total => _addresses.Count + _emails.Count + _phones.Count;
    private Guid _consumedNonce;

    protected override async Task OnParametersSetAsync()
    {
        _typeMeta = OdsTypeRegistries.ContactTypeOf(Contact.Type.ToString());
        if (_loadedId != Contact.ContactId)
        {
            _loadedId = Contact.ContactId;
            _addresses = [.. Contact.Addresses];
            _emails = [.. Contact.EmailAddresses];
            _phones = [.. Contact.PhoneNumbers];
        }

        // A fresh add-request from the row's ⋯ menu opens the matching contact form (DS requestAdd).
        if (AddRequest is { } req && req.Nonce != _consumedNonce && CanEdit && !IsArchived)
        {
            _consumedNonce = req.Nonce;
            OpenAdd(req.Kind);
            await OnAddConsumed.InvokeAsync();
        }
    }

    private sealed record ContactTileModel(
        string Kind, string Icon, string Fg, string Soft, string KindTitle,
        string Value, string Label, bool IsPrimary, Guid Id, string GridColumn,
        Func<Task> Edit, Func<Task> SetPrimary, Func<Task> Delete);

    private IEnumerable<ContactTileModel> Tiles
    {
        get
        {
            foreach (var a in _addresses)
                yield return new("address", "location_on", "oklch(0.77 0.14 55)", "oklch(0.77 0.14 55 / 0.15)", "Address",
                    AddressSummary(a), OdsTypeRegistries.AddressLabelOf(a.Label.ToString()).Label, a.IsPrimary, a.Id, "1 / -1",
                    () => OpenEditAddressAsync(a), () => SetPrimaryAddress(a), () => DeleteAddress(a));
            foreach (var e in _emails)
                yield return new("email", "alternate_email", "oklch(0.72 0.16 295)", "oklch(0.72 0.16 295 / 0.15)", "Email",
                    e.Value, OdsTypeRegistries.EmailLabelOf(e.Label.ToString()).Label, e.IsPrimary, e.Id, "span 2",
                    () => OpenEditEmailAsync(e), () => SetPrimaryEmail(e), () => DeleteEmail(e));
            foreach (var p in _phones)
                yield return new("phone", "call", "oklch(0.78 0.13 200)", "oklch(0.78 0.13 200 / 0.15)", "Phone number",
                    p.Value, OdsTypeRegistries.PhoneLabelOf(p.Label.ToString()).Label, p.IsPrimary, p.Id, "span 1",
                    () => OpenEditPhoneAsync(p), () => SetPrimaryPhone(p), () => DeletePhone(p));
        }
    }

    // Mirrors the DS ContactList menu: Copy value, [Set as primary], [Edit], Copy ID, [Delete].
    // Copy value / Copy ID are available even when read-only; mutating actions are gated.
    private IReadOnlyList<OdsMenuItem> TileMenu(ContactTileModel t)
    {
        var editable = CanEdit && !IsArchived;
        var items = new List<OdsMenuItem>
        {
            new() { Icon = "content_copy", Label = $"Copy {KindNoun(t.Kind)}", OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(t.Value, $"{t.KindTitle} copied to clipboard.")) },
        };
        if (editable && !t.IsPrimary)
            items.Add(new() { Icon = "star", Label = "Set as primary", OnClick = EventCallback.Factory.Create(this, t.SetPrimary) });
        if (editable)
            items.Add(new() { Icon = "edit", Label = "Edit", OnClick = EventCallback.Factory.Create(this, t.Edit) });
        items.Add(new() { Icon = "fingerprint", TrailingIcon = "content_copy", Label = "Copy ID", OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(t.Id.ToString(), "ID copied to clipboard.")) });
        if (editable)
        {
            items.Add(new() { Divider = true });
            items.Add(new() { Icon = "delete", Label = "Delete", Danger = true, OnClick = EventCallback.Factory.Create(this, t.Delete) });
        }
        return items;
    }

    private static string KindNoun(string kind) => kind switch { "email" => "email", "phone" => "phone number", _ => "address" };

    // ── Dialog open ───────────────────────────────────────────────────────────
    private void OpenAdd(string kind)
    {
        _dialogKind = kind;
        _dialogIsEdit = false;
        _dialogIsFirst = kind switch { "email" => _emails.Count == 0, "phone" => _phones.Count == 0, _ => _addresses.Count == 0 };
        _dialogDraft = new ContactMethodDraft { IsPrimary = _dialogIsFirst };
        _dialogOpen = true;
    }

    private Task OpenEditAddressAsync(ExistingAddress a) { OpenEdit("address", ContactMethodDraft.FromAddress(a), _addresses.Count == 1); return Task.CompletedTask; }
    private Task OpenEditEmailAsync(ExistingEmailAddress e) { OpenEdit("email", ContactMethodDraft.FromEmail(e), _emails.Count == 1); return Task.CompletedTask; }
    private Task OpenEditPhoneAsync(ExistingPhoneNumber p) { OpenEdit("phone", ContactMethodDraft.FromPhone(p), _phones.Count == 1); return Task.CompletedTask; }

    private void OpenEdit(string kind, ContactMethodDraft draft, bool isFirst)
    {
        _dialogKind = kind;
        _dialogIsEdit = true;
        _dialogIsFirst = isFirst;
        _dialogDraft = draft;
        _dialogOpen = true;
    }

    // ── Mutations ───────────────────────────────────────────────────────────────
    private Guid Id => Contact.ContactId;

    private async Task<bool> CommitContact(ContactMethodDraft draft)
    {
        var ok = _dialogKind switch
        {
            "email" => draft.Id is { } id
                ? (await Contacts.UpdateEmailAsync(Id, id, draft.ToNewEmail())).Toast(Snackbar, "Update failed", "Email saved.")
                : (await Contacts.AddEmailAsync(Id, draft.ToNewEmail())).Toast(Snackbar, "Unable to add email", "Email added."),
            "phone" => draft.Id is { } id
                ? (await Contacts.UpdatePhoneAsync(Id, id, draft.ToNewPhone())).Toast(Snackbar, "Update failed", "Phone saved.")
                : (await Contacts.AddPhoneAsync(Id, draft.ToNewPhone())).Toast(Snackbar, "Unable to add phone", "Phone added."),
            _ => draft.Id is { } id
                ? (await Contacts.UpdateAddressAsync(Id, id, draft.ToNewAddress())).Toast(Snackbar, "Update failed", "Address saved.")
                : (await Contacts.AddAddressAsync(Id, draft.ToNewAddress())).Toast(Snackbar, "Unable to add address", "Address added."),
        };

        if (ok)
            await ReloadAsync();
        return ok;
    }

    private async Task SetPrimaryAddress(ExistingAddress a)
    {
        var body = ContactMethodDraft.FromAddress(a);
        body.IsPrimary = true;
        if ((await Contacts.UpdateAddressAsync(Id, a.Id, body.ToNewAddress())).Toast(Snackbar, "Update failed", "Primary address updated."))
            await ReloadAsync();
    }
    private async Task SetPrimaryEmail(ExistingEmailAddress e)
    {
        var body = ContactMethodDraft.FromEmail(e);
        body.IsPrimary = true;
        if ((await Contacts.UpdateEmailAsync(Id, e.Id, body.ToNewEmail())).Toast(Snackbar, "Update failed", "Primary email updated."))
            await ReloadAsync();
    }
    private async Task SetPrimaryPhone(ExistingPhoneNumber p)
    {
        var body = ContactMethodDraft.FromPhone(p);
        body.IsPrimary = true;
        if ((await Contacts.UpdatePhoneAsync(Id, p.Id, body.ToNewPhone())).Toast(Snackbar, "Update failed", "Primary phone updated."))
            await ReloadAsync();
    }

    private async Task DeleteAddress(ExistingAddress a)
    {
        if ((await Contacts.DeleteAddressAsync(Id, a.Id)).Toast(Snackbar, "Delete failed", "Address deleted."))
            await ReloadAsync();
    }
    private async Task DeleteEmail(ExistingEmailAddress e)
    {
        if ((await Contacts.DeleteEmailAsync(Id, e.Id)).Toast(Snackbar, "Delete failed", "Email deleted."))
            await ReloadAsync();
    }
    private async Task DeletePhone(ExistingPhoneNumber p)
    {
        if ((await Contacts.DeletePhoneAsync(Id, p.Id)).Toast(Snackbar, "Delete failed", "Phone deleted."))
            await ReloadAsync();
    }

    // Re-fetch all three collections after a mutation (the server owns primary arbitration), then let
    // the host refresh the row's contact counts + UpdatedAt.
    private async Task ReloadAsync()
    {
        _addresses = (await Contacts.ListAddressesAsync(Id)).ItemsOrToast(Snackbar, "addresses");
        _emails = (await Contacts.ListEmailsAsync(Id)).ItemsOrToast(Snackbar, "emails");
        _phones = (await Contacts.ListPhonesAsync(Id)).ItemsOrToast(Snackbar, "phone numbers");
        await OnChanged.InvokeAsync(Contact.ContactId);
        StateHasChanged();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────
    private static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string? WebsiteHref(string? website) =>
        !string.IsNullOrWhiteSpace(website)
        && (website.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || website.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ? website
            : null;

    private static string AddressSummary(ExistingAddress a)
    {
        var cityLine = string.Join(' ', new[] { a.PostalCode, a.City }.Where(v => !string.IsNullOrWhiteSpace(v)));
        return string.Join(", ", new[] { a.Line1, cityLine, a.CountryCode }.Where(v => !string.IsNullOrWhiteSpace(v)));
    }
}
