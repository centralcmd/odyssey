using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Application;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages.Finance;

public partial class CreateTransactionDialog
{
    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    [Parameter] public Guid? DefaultAccountId { get; set; }
    [Parameter] public bool LockAccount { get; set; }

    /// <summary>When set, the dialog edits this transaction. Null = create mode.</summary>
    [Parameter] public ExistingTransaction? Transaction { get; set; }

    [Parameter] public bool CanDownloadFiles { get; set; }
    [Parameter] public bool CanUploadFiles { get; set; }
    [Parameter] public bool CanDeleteFiles { get; set; }

    private bool IsEdit => Transaction is not null;

    // ── Form state ───────────────────────────────────────────────────────────
    private string? _description;
    private string _amountText = string.Empty;
    private bool _isExpense = true;
    private DateTime? _timeStamp = DateTime.UtcNow;
    private TransactionStatus _status = TransactionStatus.New;

    // Status radio group — the values in display order, plus a ref per chip so arrow-key navigation
    // can move DOM focus in step with selection (WAI-ARIA radiogroup pattern, as OdsSegmentedControl).
    private static readonly TransactionStatus[] _statuses = Enum.GetValues<TransactionStatus>();
    private readonly ElementReference[] _statusRefs = new ElementReference[_statuses.Length];
    private string? _statusComment;
    private string? _externalId;
    private string? _internalId;
    private string? _extraData;
    private string _currencyCode = string.Empty;
    private bool _isSaving;

    // ── Validation flags ───────────────────────────────────────────────────────
    private bool _descError;
    private bool _amountError;
    private bool _accountError;

    // Disclosure state for the account trigger, so it reports aria-expanded.
    // Driven by MudMenu.OpenChanged (reading MudMenu.Open directly trips MUD0012).
    private bool _accountMenuOpen;

    // ── Data ─────────────────────────────────────────────────────────────────
    private List<ExistingAccount> _accounts = [];
    private List<ExistingTransactionTag> _tags = [];
    private List<ExistingContact> _contacts = [];
    private List<ExistingCurrency> _currencies = [];
    private IReadOnlyList<OdsOption> _currencyOptions = [];

    private ExistingAccount? _selectedAccount;
    private IReadOnlyCollection<string> _selectedTagIds = [];
    private IReadOnlyList<OdsOption> _tagOptions = [];

    // Contact combobox — selection is the option value (a contact id string), and
    // inline-created contacts get an optimistic temp id reconciled to the real one on save.
    private string? _contactId;
    private List<OdsOption> _cpOptions = [];
    private bool _canCreateContact;
    private readonly Dictionary<string, string> _cpReconcile = new();   // tempId → realId
    private readonly HashSet<string> _createdTempIds = [];              // optimistic ids from inline create
    private readonly List<Task> _pendingCpCreates = [];

    private List<OdsUploadFile> _pendingFiles = [];

    // The TransactionFileType vocabulary projected to the OdsFileUpload kind shape (per-file picker).
    private static readonly IReadOnlyList<OdsFileKind> _txnKinds =
        [.. OdsTypeRegistries.TransactionFileTypes.Select(t => new OdsFileKind
        {
            Key = t.Key, Label = t.Label, Icon = t.Icon, Color = t.Color, Soft = t.Soft,
        })];

    private const int MaxFileCount = 64;
    private static readonly string[] AllowedExtensions = [".pdf", ".jpg", ".jpeg", ".png"];

    // The hero's own helper line, which is where the direction affordance is explained — the sign
    // segment is a button, and typing − / + in the amount flips it too.
    private string DirectionHint => _isExpense
        ? "Expense — click the sign, or type + in the amount, for income."
        : "Income — click the sign, or type − in the amount, for expense.";

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    // The cap is admin-editable (issue #421 Wave 4) and OnFilesChanged is a synchronous handler, so it
    // is prefetched here. Seeded with the shipped fallback so a render that beats the fetch still
    // validates against a sane number rather than zero.
    private UploadLimitsDto _uploadLimits = UploadLimitsCache.Fallback;

    protected override async Task OnInitializedAsync()
    {
        _uploadLimits = await UploadLimits.GetAsync();
        if (!OperatingSystem.IsBrowser())
            return;

        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);

        _currencyCode = UserPreferences.DefaultCurrency ?? string.Empty;
        await Task.WhenAll(LoadAccounts(), LoadTransactionTags(), LoadContacts(), LoadCurrencies());

        if (Transaction is { } t)
        {
            _description = t.Description;
            _isExpense = t.Amount < 0;
            _amountText = Math.Abs(t.Amount).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            _timeStamp = t.TimeStamp;
            _status = t.Status;
            _statusComment = t.StatusComment;
            _externalId = t.ExternalId;
            _internalId = t.InternalId;
            _extraData = t.ExtraData;
            _currencyCode = t.CurrencyCode;
            _selectedAccount = _accounts.FirstOrDefault(a => a.AccountId == t.AccountId);
            _selectedTagIds = t.TransactionTags.Select(tag => tag.TransactionTagId.ToString()).ToList();
            _contactId = t.ContactId?.ToString() ?? t.Contact?.ContactId.ToString();
        }
        else if (DefaultAccountId is { } id)
        {
            _selectedAccount = _accounts.FirstOrDefault(a => a.AccountId == id);
            if (_selectedAccount is not null && !string.IsNullOrWhiteSpace(_selectedAccount.CurrencyCode))
                _currencyCode = _selectedAccount.CurrencyCode;
        }

        ResolveCurrencyFallback();
        StateHasChanged();
    }

    private async Task LoadAccounts()
    {
        var accounts = (await Accounts.ListAllAsync()).ItemsOrToast(Snackbar, "accounts");
        // Edit mode keeps every account selectable — the transaction's own account may already be
        // closed/archived and still needs to appear (and stay pickable) in the list.
        _accounts = (IsEdit ? accounts : accounts.Where(a => a.Closed is null && a.Archived is null))
            .OrderBy(a => a.Name)
            .ToList();
    }

    private async Task LoadTransactionTags()
    {
        var tags = await ReferenceData.TransactionTagsAsync();
        _tags = tags.Where(t => t.Archived is null).OrderBy(t => t.Name).ToList();

        // Edit mode: a tag the transaction already carries still needs to render its name even if it
        // has since been archived, so it doesn't collapse to a bare id in the multi-select.
        var byId = _tags.ToDictionary(t => t.TransactionTagId.ToString(), t => t.Name);
        if (Transaction is { } t2)
        {
            foreach (var tag in t2.TransactionTags)
                byId[tag.TransactionTagId.ToString()] = tag.Name;
        }
        _tagOptions = byId.Select(kv => new OdsOption(kv.Key, kv.Value)).OrderBy(o => o.Label).ToList();
    }

    private async Task LoadContacts()
    {
        var contacts = await ReferenceData.ContactsAsync();
        _contacts = contacts.Where(c => c.Archived is null).OrderBy(c => c.ResolvedDisplayName).ToList();
        _cpOptions = _contacts.Select(ToContactOption).ToList();

        // Edit mode: the transaction's own contact still needs an option to display its name
        // even if it has since been archived.
        if (Transaction?.Contact is { } existingCp && _cpOptions.All(o => o.Value != existingCp.ContactId.ToString()))
            _cpOptions.Add(ToContactOption(existingCp));
    }

    private static OdsOption ToContactOption(ExistingContact cp)
    {
        var meta = OdsTypeRegistries.ContactTypeOf(cp.Type.ToString());
        return new OdsOption(cp.ContactId.ToString(), cp.ResolvedDisplayName) { Icon = meta.Icon, IconColor = meta.Color };
    }

    private async Task LoadCurrencies()
    {
        _currencies = [.. await ReferenceData.ActiveCurrenciesAsync()];
        _currencyOptions = await ReferenceData.CurrencyOptionsAsync();
    }

    private void ResolveCurrencyFallback()
    {
        if (!string.IsNullOrEmpty(_currencyCode) && _currencies.Count > 0
            && _currencies.All(c => !string.Equals(c.CurrencyCode, _currencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            _currencyCode = _currencies[0].CurrencyCode;
        }
    }

    // ── Field handlers ──────────────────────────────────────────────────────────
    // OdsMoneyField has already sanitized the text (and AllowNegative="false" keeps the sign out of
    // it — the direction owns that).
    private void OnAmountChanged(string value)
    {
        _amountText = value;
        if (_amountError && TryParseAmount(out _))
            _amountError = false;
    }

    private void OnDirectionChanged(string direction) => _isExpense = direction == "expense";

    private void SelectAccount(ExistingAccount account)
    {
        _selectedAccount = account;
        _accountError = false;
        if (!string.IsNullOrWhiteSpace(account.CurrencyCode))
            _currencyCode = account.CurrencyCode;
    }

    // Arrow / Home / End move selection and DOM focus together across the status radio group.
    private async Task OnStatusKeyDown(KeyboardEventArgs e)
    {
        var index = Array.IndexOf(_statuses, _status);
        if (index < 0)
            return;

        var next = e.Key switch
        {
            "ArrowRight" or "ArrowDown" => (index + 1) % _statuses.Length,
            "ArrowLeft" or "ArrowUp" => (index - 1 + _statuses.Length) % _statuses.Length,
            "Home" => 0,
            "End" => _statuses.Length - 1,
            _ => -1,
        };
        if (next < 0)
            return;

        _status = _statuses[next];
        await _statusRefs[next].FocusAsync();
    }

    // Inline contact create (claim-gated) — mirrors the file-analysis merchant flow: return an
    // optimistic option with a temp id now, POST in the background, reconcile the temp id to the real
    // one when the server responds (and await any in-flight create before saving the transaction).
    private OdsOption? CreateContactOption(string text)
    {
        var name = (text ?? string.Empty).Trim();
        if (name.Length == 0)
            return null;
        if (name.Length > 128)
            name = name[..128];

        var tempId = Guid.NewGuid().ToString();
        var meta = OdsTypeRegistries.ContactTypeOf(nameof(ContactType.Organization));
        var option = new OdsOption(tempId, name) { Icon = meta.Icon, IconColor = meta.Color };
        _cpOptions = _cpOptions.Append(option).ToList();
        _createdTempIds.Add(tempId);
        _pendingCpCreates.Add(CreateContactAsync(name, tempId));
        return option;
    }

    private async Task CreateContactAsync(string name, string tempId)
    {
        try
        {
            // Quick-create defaults to an Organization with the typed text as its legal name (issue
            // #325 §13 — mirrors the migration's Organization-as-fallback for ambiguous legacy types).
            var body = new NewContact
            {
                Type = ContactType.Organization,
                Archived = false,
                OrganizationDetails = new OrganizationDetailsDto { LegalName = name },
            };
            var result = await Contacts.CreateAsync(body);
            if (result.IsSuccess)
            {
                // The session-wide contact cache is now stale — the next picker must re-fetch.
                ReferenceData.InvalidateContacts();
                if (result.CreatedId is { } id)
                    _cpReconcile[tempId] = id.ToString();
                return;
            }

            // A duplicate name (409) means the contact already exists — link the optimistic
            // option to the existing record by name instead of failing the whole transaction.
            if (result.Status == System.Net.HttpStatusCode.Conflict)
            {
                var existing = _contacts.FirstOrDefault(c => string.Equals(c.ResolvedDisplayName, name, StringComparison.OrdinalIgnoreCase))
                    ?? (await ReferenceData.ContactsAsync())
                        .FirstOrDefault(c => c.Archived is null && string.Equals(c.ResolvedDisplayName, name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    _cpReconcile[tempId] = existing.ContactId.ToString();
                    return;
                }
            }

            Snackbar.Add($"Couldn’t create “{name}”: {result.Error}", Severity.Error);
            RollbackCreatedContact(tempId);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Couldn’t create “{name}”: {ex.Message}", Severity.Error);
            RollbackCreatedContact(tempId);
        }
    }

    // Drop a failed optimistic contact: remove its option and clear the selection if it was picked,
    // so no phantom row lingers. The temp id stays in _createdTempIds so ResolveContactId still
    // treats any late reference as unresolved (never posts the bogus id).
    private void RollbackCreatedContact(string tempId)
    {
        _cpOptions = _cpOptions.Where(o => o.Value != tempId).ToList();
        if (_contactId == tempId)
            _contactId = null;
        StateHasChanged();
    }

    private Task OnCurrencyChanged(string value)
    {
        _currencyCode = value;
        return Task.CompletedTask;
    }

    // Controlled list — enforce the allow-list, per-file size cap and file-count cap.
    private void OnFilesChanged(IReadOnlyList<OdsUploadFile> files)
    {
        var kept = new List<OdsUploadFile>();
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            if (f.Source is not null && !AllowedExtensions.Contains(ext))
            {
                Snackbar.Add($"{f.Name}: unsupported type. Allowed: .pdf, .jpg, .jpeg, .png", Severity.Warning);
                continue;
            }
            if (f.SizeBytes > _uploadLimits.MaxUploadBytes)
            {
                Snackbar.Add($"{f.Name}: exceeds the {_uploadLimits.MaxUploadMegabytes} MB limit.", Severity.Warning);
                continue;
            }
            if (kept.Count >= MaxFileCount)
                break;
            kept.Add(f);
        }
        _pendingFiles = kept;
    }

    private bool TryParseAmount(out decimal magnitude)
    {
        var normalized = (_amountText ?? string.Empty).Replace(",", string.Empty);
        return decimal.TryParse(normalized, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out magnitude) && magnitude > 0;
    }

    // Resolve the selected contact id to a real Guid, mapping an optimistic temp id through the
    // reconcile table. Returns null when nothing is selected, or when an inline create was selected
    // but never reconciled (it failed) — so a failed create drops the contact rather than posting
    // a bogus id that the FK would reject and sink the whole transaction.
    private Guid? ResolveContactId()
    {
        if (string.IsNullOrEmpty(_contactId))
            return null;
        if (_cpReconcile.TryGetValue(_contactId, out var real))
            return Guid.TryParse(real, out var reconciled) ? reconciled : null;
        if (_createdTempIds.Contains(_contactId))
            return null;
        return Guid.TryParse(_contactId, out var parsed) ? parsed : null;
    }

    // ── Submit ───────────────────────────────────────────────────────────────────
    private Task CancelClicked() => OpenChanged.InvokeAsync(false);

    private NewTransaction BuildPayload(decimal magnitude)
    {
        var signed = _isExpense ? -Math.Abs(magnitude) : Math.Abs(magnitude);
        return new NewTransaction
        {
            Description = _description!.Trim(),
            Amount = decimal.Round(signed, 2),
            TimeStamp = _timeStamp,
            AccountId = _selectedAccount!.AccountId,
            TransactionTagIds = _selectedTagIds
                .Select(id => Guid.TryParse(id, out var tagId) ? tagId : (Guid?)null)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .ToList(),
            ContactId = ResolveContactId(),
            CurrencyCode = _currencyCode.Trim().ToUpperInvariant(),
            ExternalId = string.IsNullOrWhiteSpace(_externalId) ? null : _externalId.Trim(),
            InternalId = string.IsNullOrWhiteSpace(_internalId) ? null : _internalId.Trim(),
            ExtraData = string.IsNullOrWhiteSpace(_extraData) ? null : _extraData.Trim(),
            Status = _status,
            StatusComment = string.IsNullOrWhiteSpace(_statusComment) ? null : _statusComment.Trim(),
        };
    }

    private async Task SaveClicked()
    {
        if (_isSaving)
            return;

        _descError = string.IsNullOrWhiteSpace(_description);
        _amountError = !TryParseAmount(out var magnitude);
        _accountError = _selectedAccount is null;
        if (_descError || _amountError || _accountError)
            return;

        if (string.IsNullOrWhiteSpace(_currencyCode))
        {
            Snackbar.Add("Currency is required.", Severity.Error);
            return;
        }

        _isSaving = true;
        try
        {
            // Let any in-flight inline contact creates land so we post the real id, not a temp one.
            if (_pendingCpCreates.Count > 0)
                await Task.WhenAll(_pendingCpCreates);

            if (IsEdit)
            {
                var update = BuildPayload(magnitude);
                if ((await Transactions.UpdateAsync(Transaction!.TransactionId, update)).Toast(Snackbar, "Update failed", "Transaction updated."))
                {
                    await OnSaved.InvokeAsync();
                    await OpenChanged.InvokeAsync(false);
                }
                return;
            }

            var newTransaction = BuildPayload(magnitude);
            var created = await Transactions.CreateAsync(newTransaction);
            if (!created.IsSuccess)
            {
                Snackbar.Add($"Unable to add transaction. {created.Error}", Severity.Error);
                return;
            }

            // Attachments (two-step): the new transaction's ID comes back only in the
            // Location header (POST returns 201 with an empty body).
            if (_pendingFiles.Count > 0)
            {
                if (created.CreatedId is { } txnId)
                    await AttachPendingFilesAsync(txnId);
                else
                    Snackbar.Add("Transaction saved, but its files could not be attached (no ID returned).", Severity.Warning);
            }

            Snackbar.Add("Transaction added.", Severity.Success);
            await OnSaved.InvokeAsync();
            await OpenChanged.InvokeAsync(false);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Unable to {(IsEdit ? "update" : "add")} transaction. {ex.Message}", Severity.Error);
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task AttachPendingFilesAsync(Guid transactionId)
    {
        var failures = 0;
        foreach (var item in _pendingFiles)
        {
            if (item.Source is null)
                continue;
            try
            {
                var uploaded = await Files.UploadAsync(item.Source.ToApiUpload(_uploadLimits.MaxUploadBytes));
                var finalName = item.Name.Trim();
                if (!string.IsNullOrEmpty(finalName) && finalName != item.Source.Name)
                    await Files.UpdateMetadataAsync(uploaded.Id, null, finalName);
                await Files.AttachToTransactionAsync(transactionId, uploaded.Id, TypeOf(item));
            }
            catch
            {
                failures++;
            }
        }

        if (failures > 0)
            Snackbar.Add($"Transaction saved, but {failures} file(s) could not be attached.", Severity.Warning);
    }

    // ── Display helpers ───────────────────────────────────────────────────────────
    private static string GuessKind(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext == ".pdf" ? nameof(TransactionFileType.Invoice) : nameof(TransactionFileType.Receipt);
    }

    private static TransactionFileType TypeOf(OdsUploadFile f) =>
        Enum.TryParse<TransactionFileType>(f.Kind, out var t) ? t : TransactionFileType.Other;

    private static (string Icon, string Color, string Soft) StatusVisual(TransactionStatus status) => status switch
    {
        TransactionStatus.Approved => ("check_circle", "var(--finance-income)", "var(--finance-income-soft)"),
        TransactionStatus.Flagged => ("flag", "var(--finance-expense)", "var(--finance-expense-soft)"),
        _ => ("fiber_new", "var(--mud-palette-info)", "color-mix(in srgb, var(--mud-palette-info) 16%, transparent)"),
    };
}
