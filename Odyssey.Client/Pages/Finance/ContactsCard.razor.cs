using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages.Finance;

public partial class ContactsCard
{
    private List<ExistingContact> _contacts = new();
    private List<ExistingContact> _allContacts = new();
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private string _announce = "";

    // Server pagination (OdsPager): 1-based page + rows-per-page; TotalCount from the PagedResult.
    private int _page = 1;
    private int _pageSize = OdsPageSizes.Default[0];
    private int _totalCount;

    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    // The second half of the composed detach gate (issue #27 §7 #6). The server enforces it with a
    // 403; this only keeps the blocked-delete dialog from offering an action it knows will fail.
    private bool _canUpdateInsurance;
    private bool _canImport => _canCreate && _canUpdate; // POST vcard requires BOTH claims (§7.3)

    private bool _exporting;
    private bool _importOpen;

    // The blocked-delete dialog's state. The key forces a fresh component per refusal, so a second
    // blocked delete never reopens on the previous one's result step.
    private bool _blockedOpen;
    private Guid _blockedKey;
    private ExistingContact? _blockedContact;
    private ContactInsuranceLinkBlockers _blockedLinks = new();

    private const string PageStateKey = "contacts-page";
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;

    // Sort (§6.4): Name + Type curated; one OdsTableSort synced with the table headers.
    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<ExistingContact>> _sortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text },
        new() { Key = "type", Label = "Type", Type = OdsSortType.Status },
    ];

    // Overview/breakdown reflect the whole dataset (issue #277 follow-up): derived from the unfiltered
    // _allContacts, not the server-filtered display list.
    private IReadOnlyList<OdsBreakdownRow> TypeRows => OdsBreakdown.TypeRows(
        _allContacts.Where(c => c.Archived is null), c => c.Type, Enum.GetValues<ContactType>(),
        t => { var m = OdsTypeRegistries.ContactTypeOf(t.ToString()); return (m.Icon, m.Color, m.Label); });

    private IReadOnlyList<OdsBreakdownRow> StatusRows => OdsBreakdown.StatusRows(
        _allContacts, c => c.Archived is not null ? "archived" : "active",
        new OdsBreakdownDef<string>("active", "Active", "income", "task_alt"),
        new OdsBreakdownDef<string>("archived", "Archived", "outline", "inventory_2"));
    private IReadOnlyCollection<string> _typeFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];

    private static readonly IReadOnlyList<OdsOption> _statusOptions =
        [new("active", "Active"), new("archived", "Archived")];

    private int _activeCount => _allContacts.Count(c => c.Archived is null);
    private int _archivedCount => _allContacts.Count - _activeCount;
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_search) || _typeFilter.Count > 0 || _statusFilter.Count > 0;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        StateHasChanged();
        await LoadPermissionsAsync();
        await RefreshAsync();
    }

    // Full refresh: unfiltered overview set + server-filtered display list.
    private async Task RefreshAsync()
    {
        _allContacts = (await Contacts.ListAllAsync()).ItemsOrToast(Snackbar, "contacts");
        await ReloadAsync();
    }

    // ── Page-state persistence (search section + filters) ─────────────────────
    // Type values are stable ContactType enum names → restored as-is; the
    // static Status filter is sanitised against its options.
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<ContactsPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(ContactsPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _typeFilter = state.TypeFilter ?? [];
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _pageSize = OdsPageSizes.Restore(state.PageSize);
    }

    private ContactsPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _search,
        TypeFilter = [.. _typeFilter],
        StatusFilter = [.. _statusFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        PageSize = _pageSize,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; PersistPageState(); }
    private async Task OnTypeFilterChanged(IReadOnlyCollection<string> values) { _typeFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await ReloadAsync(); }

    private sealed class ContactsPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> TypeFilter { get; set; } = [];
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int PageSize { get; set; } = OdsPageSizes.Default[0];
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();

        _canCreate = user.HasPermission(PermissionClaims.ContactsCreate);
        _canUpdate = user.HasPermission(PermissionClaims.ContactsUpdate);
        _canDelete = user.HasPermission(PermissionClaims.ContactsDelete);
        _canUpdateInsurance = user.HasPermission(PermissionClaims.InsuranceUpdate);
    }

    // Server-side fetch (issue #277): search/type/status/sort applied by the API.
    private async Task GetContacts()
    {
        // First load blanks the table for a spinner; every later fetch keeps the rows and shows the bar.
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        var result = await Contacts.ListAsync(
            _page, _pageSize,
            search: _search,
            types: _typeFilter,
            status: _statusFilter,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

        var load = result.PagedOrToast(Snackbar, "contacts");
        if (load.IsSuccess)
        {
            _contacts = [.. load.Items];
            _totalCount = load.TotalCount;
            _loadError = false;
            _announce = _totalCount == 0 ? "No contacts match your filters."
                : $"Showing {OdsPagerMath.FirstShown(_page, _pageSize, _totalCount)}–{OdsPagerMath.LastShown(_page, _pageSize, _totalCount)} of {_totalCount} contact{(_totalCount == 1 ? "" : "s")}.";
        }
        else
        {
            _loadError = true;
            _announce = "Couldn't load contacts.";
        }

        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    // Reset to page 1, then fetch — for any search / filter / sort / size change. Page navigation
    // calls GetContacts directly so it keeps the requested page.
    private Task ReloadAsync()
    {
        _page = 1;
        return GetContacts();
    }

    private Task OnPageChanged(int page)
    {
        _page = page;
        return GetContacts();
    }

    private Task OnPageSizeChanged(int size)
    {
        _pageSize = size;
        _page = 1;
        PersistPageState();
        return GetContacts();
    }

    private async Task ClearFilters()
    {
        _search = string.Empty;
        _typeFilter = [];
        _statusFilter = [];
        PersistPageState();
        await ReloadAsync();
    }

    private IReadOnlyList<OdsMenuItem> BuildActions(ExistingContact c, OdsRecordActionContext ctx)
    {
        var items = new List<OdsMenuItem>();

        if (!ctx.Editing)
        {
            items.Add(new OdsMenuItem
            {
                Icon = ctx.Expanded ? "close" : "expand_more",
                Label = ctx.Expanded ? "Collapse" : "View details",
                OnClick = EventCallback.Factory.Create(this, ctx.Toggle),
            });
        }

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit", OnClick = EventCallback.Factory.Create(this, () => EditClicked(c)) });
        }

        // Per-row vCard export (issue #338 §7.1) — requires only contacts.read, so it's always
        // available to anyone who can see the page.
        items.Add(new OdsMenuItem { Icon = "download", Label = "Export vCard", OnClick = EventCallback.Factory.Create(this, () => ExportRowAsync(c)) });

        // Add a contact from the row menu (DS): expands the row and opens the matching contact form.
        if (_canCreate && c.Archived is null)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem { Icon = "add_location_alt", Label = "New address", OnClick = EventCallback.Factory.Create(this, () => RequestAddContact(c, ctx, "address")) });
            items.Add(new OdsMenuItem { Icon = "alternate_email", Label = "New email", OnClick = EventCallback.Factory.Create(this, () => RequestAddContact(c, ctx, "email")) });
            items.Add(new OdsMenuItem { Icon = "add_call", Label = "New phone number", OnClick = EventCallback.Factory.Create(this, () => RequestAddContact(c, ctx, "phone")) });
        }

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = c.Archived is not null ? "unarchive" : "archive",
                Label = c.Archived is not null ? "Restore" : "Archive",
                OnClick = EventCallback.Factory.Create(this, () => ToggleArchive(c)),
            });
        }

        items.Add(new OdsMenuItem { Icon = "fingerprint", TrailingIcon = "content_copy", Label = "Copy ID", OnClick = EventCallback.Factory.Create(this, () => CopyId(c.ContactId)) });

        if (_canDelete)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem { Icon = "delete", Label = "Delete", Danger = true, OnClick = EventCallback.Factory.Create(this, ctx.Remove) });
        }

        return items;
    }

    // A pending add-contact request routed to the expanded row's detail panel (DS requestAdd): a fresh
    // nonce each time so re-picking the same kind re-triggers the form.
    private (Guid Id, string Kind, Guid Nonce)? _addRequest;

    private void RequestAddContact(ExistingContact c, OdsRecordActionContext ctx, string kind)
    {
        if (!ctx.Expanded)
        {
            ctx.Toggle();
        }

        _addRequest = (c.ContactId, kind, Guid.NewGuid());
        StateHasChanged();
    }

    private bool _createOpen;
    private Guid _createKey;

    private bool _editOpen;
    private Guid _editKey;
    private ExistingContact? _editTarget;

    private void AddClicked()
    {
        if (!_canCreate)
            return;

        _createKey = Guid.NewGuid();
        _createOpen = true;
    }

    // Edit opens the shared contact dialog in edit mode (DS AddContactModal), not an inline
    // row editor. A fresh key each time re-initialises the dialog from the chosen row.
    private void EditClicked(ExistingContact contact)
    {
        if (!_canUpdate)
            return;

        _editTarget = contact;
        _editKey = Guid.NewGuid();
        _editOpen = true;
    }

    private async Task OnEditSaved()
    {
        if (_editTarget is not null)
            await RefreshContactAsync(_editTarget.ContactId);
        _allContacts = (await Contacts.ListAllAsync()).ItemsOrToast(Snackbar, "contacts");
        StateHasChanged();
    }

    // Archive/restore replays the contact through the full PUT contract (it must carry the
    // type-matching details sub-object, §9), flipping only the archival flag.
    private Task ToggleArchive(ExistingContact contact)
    {
        var body = ToNewContact(contact, archived: contact.Archived is null);
        return PutContact(contact, body);
    }

    private static NewContact ToNewContact(ExistingContact c, bool archived) => new()
    {
        Type = c.Type,
        DisplayName = c.DisplayName,
        Notes = c.Notes,
        Archived = archived,
        PersonDetails = c.Type == ContactType.Person ? c.PersonDetails : null,
        OrganizationDetails = c.Type == ContactType.Organization ? c.OrganizationDetails : null,
    };

    private async Task PutContact(ExistingContact contact, NewContact body)
    {
        if (!_canUpdate)
            return;

        if ((await Contacts.UpdateAsync(contact.ContactId, body)).Toast(Snackbar, "Update failed", "Contact updated."))
        {
            await RefreshContactAsync(contact.ContactId);
            _allContacts = (await Contacts.ListAllAsync()).ItemsOrToast(Snackbar, "contacts");
        }

        StateHasChanged();
    }

    // Re-fetch a single contact and replace it in the display list (after inline edit, archive, or
    // a contact mutation from the detail panel) so the row's resolved name, contact counts and
    // UpdatedAt stay current without a full reload.
    private async Task RefreshContactAsync(Guid contactId)
    {
        // Any write that lands here — inline edit, archive, or a contact-method change from the
        // detail panel — can move a name the pickers show, so drop the session cache (issue #372).
        ReferenceData.InvalidateContacts();

        var fresh = (await Contacts.GetAsync(contactId)).OrToast(Snackbar, "Unable to load contact");
        if (fresh is null)
            return;

        var index = _contacts.FindIndex(c => c.ContactId == contactId);
        if (index >= 0)
        {
            _contacts[index] = fresh;
            StateHasChanged();
        }
    }

    private async Task HandleDelete(object key)
    {
        if (!_canDelete)
            return;

        var contact = _contacts.FirstOrDefault(c => c.ContactId.Equals(key));
        if (contact is null)
            return;

        // On success, a full refresh rather than a local Remove: the delete changes the total, so the
        // pager and the current page have to be re-fetched or the page renders short against a stale
        // count. A refusal may instead be a recoverable insurance one — see ShowInsuranceBlockers.
        var result = await Contacts.DeleteAsync(contact.ContactId);
        if (!ShowInsuranceBlockers(contact, result)
            && result.Toast(Snackbar, "Delete failed", "Contact deleted."))
        {
            ReferenceData.InvalidateContacts();
            await RefreshAsync();
        }

        StateHasChanged();
    }

    /// <summary>
    /// Opens the blocked-delete dialog when the refusal was an insurance one, and reports whether it
    /// did.
    ///
    /// <para>
    /// A 409 here means the contact is named as an insurer, an insured contact or a beneficiary
    /// (issue #27 §7 #5). The payload says which KINDS block and how many link rows, and — only when
    /// the caller holds <c>insurance.read</c> — which policies. It is a recoverable state with a
    /// supported route out, so it opens a dialog rather than becoming a toast the user cannot act on.
    /// </para>
    /// </summary>
    private bool ShowInsuranceBlockers(ExistingContact contact, ApiClient.ApiResult result)
    {
        if (result.Status != System.Net.HttpStatusCode.Conflict
            || result.Problem?.Extension<ContactInsuranceLinkBlockers>("insuranceLinks")
                is not { Kinds.Count: > 0 } blockers)
        {
            return false;
        }

        _blockedContact = contact;
        _blockedLinks = blockers;
        _blockedKey = Guid.NewGuid();
        _blockedOpen = true;
        return true;
    }

    /// <summary>
    /// The detach path: removes every insurance link naming the contact and deletes it in ONE request
    /// and one transaction (issue #27 §7 #6). Composed from <c>contacts.delete</c> and
    /// <c>insurance.update</c>; a caller holding only the first gets a 403 rather than a silent
    /// downgrade to the refused delete.
    /// </summary>
    private async Task<DetachedInsuranceLinks?> DetachAndDeleteAsync()
    {
        if (_blockedContact is not { } contact)
            return null;

        var result = await Contacts.DeleteWithInsuranceDetachAsync(contact.ContactId);
        if (!result.IsSuccess)
        {
            Snackbar.Add($"Unable to detach and delete: {result.Error}", Severity.Error);
            return null;
        }

        ReferenceData.InvalidateContacts();
        // Same reason as the ordinary delete: the total changed, so the pager has to be re-fetched.
        await RefreshAsync();
        StateHasChanged();
        return result.Value;
    }

    private Task CopyId(Guid contactId) =>
        Clipboard.CopyAsync(contactId.ToString(), "Contact ID copied to clipboard.");

    // ── vCard export/import (issue #338) ──────────────────────────────────────

    // Per-row export (§7.1) — requires only contacts.read; no cap. _exporting guards a
    // re-entrant double-click firing concurrent downloads (mirrors the Calendar ICS export).
    private async Task ExportRowAsync(ExistingContact c)
    {
        if (_exporting)
            return;

        _exporting = true;
        try
        {
            var result = await VCardApi.ExportOneAsync(c.ContactId);
            if (result.OrToast(Snackbar, "Unable to export the contact") is not { } file)
                return;

            await JS.InvokeVoidAsync("downloadFileFromBytes", file.Bytes, file.FileName, "text/vcard");
            Snackbar.Add($"Exported {file.FileName}", Severity.Success);
        }
        finally
        {
            _exporting = false;
        }
    }

    // Page-level export (§7.2): "all" omits filters; "filtered" sends the same search/type/status
    // query the list view is currently using (minus paging — export is unpaginated server-side).
    private async Task ExportAsync(bool filtered)
    {
        if (_exporting)
            return;

        _exporting = true;
        try
        {
            var result = filtered
                ? await VCardApi.ExportManyAsync(_search, _typeFilter, _statusFilter)
                : await VCardApi.ExportManyAsync();
            if (result.OrToast(Snackbar, "Unable to export contacts") is not { } file)
                return;

            await JS.InvokeVoidAsync("downloadFileFromBytes", file.Bytes, file.FileName, "text/vcard");
            Snackbar.Add($"Exported {file.FileName}", Severity.Success);
        }
        finally
        {
            _exporting = false;
        }
    }

    private void OpenImportDialog()
    {
        if (!_canImport)
            return;

        _importOpen = true;
    }

    // After an import that created/updated rows, do a full refresh (mirrors RefreshAsync) so both
    // the list and the overview counts reflect the new/changed contacts.
    private Task OnImportedAsync() => RefreshAsync();
}
