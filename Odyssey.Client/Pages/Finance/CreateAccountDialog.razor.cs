using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

public partial class CreateAccountDialog
{
    /// <summary>Controls visibility. Bindable via @bind-Open.</summary>
    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>When set, the dialog edits this account. Null = create mode.</summary>
    [Parameter] public ExistingAccount? Account { get; set; }

    private bool IsEdit => Account is not null;

    private string? _description;
    private string? _name;
    private string? _accountNumber;
    private AccountType _accountType = AccountType.Unknown;
    private DateTime? _opened = DateTime.UtcNow;
    private DateTime? _closed;
    private string _currencyCode = string.Empty;
    private List<ExistingCurrency> _currencies = [];
    private IReadOnlyList<OdsOption> _currencyOptions = [];

    private string? _custodianId;
    private List<ExistingContact> _contacts = [];
    private bool _contactsLoading;
    private string? _custodianError;
    private bool _canCreateContact;

    private string CustodianHelp => _canCreateContact
        ? "The bank, broker, or provider that holds this account — add it here if it isn't listed."
        : "The bank, broker, or provider that holds this account.";

    private bool _nameError;
    private bool _typeError;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        if (Account is { } account)
        {
            _name          = account.Name;
            _description   = account.Description;
            _accountNumber = account.AccountNumber;
            _accountType   = account.AccountType;
            _opened        = account.Opened;
            _closed        = account.Closed;
            _currencyCode  = account.CurrencyCode;
            _custodianId   = account.CustodianId?.ToString();
        }
        else
        {
            _currencyCode = UserPreferences.DefaultCurrency ?? string.Empty;
        }

        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);
        ContactCreator.OnCreateFailed = OnContactCreateFailed;

        await Task.WhenAll(LoadCurrencies(), LoadContacts());
    }

    // The picker hands its option back synchronously; the shared creator POSTs behind it and the id is
    // reconciled at save. The staged contact joins the list so it stays resolvable meanwhile.
    private OdsOption? CreateContactOption(string text, string kind)
    {
        var option = ContactCreator.Begin(text, kind);
        if (option is not null && Guid.TryParse(option.Value, out var tempId))
        {
            _contacts = [.. _contacts, new ExistingContact
            {
                ContactId = tempId,
                ResolvedDisplayName = option.Label,
                NormalizedName = option.Label.ToUpperInvariant(),
                ExternalUid = string.Empty,
                Type = kind == nameof(ContactType.Person) ? ContactType.Person : ContactType.Organization,
            }];
        }
        return option;
    }

    private void OnContactCreateFailed(string tempId)
    {
        _contacts = _contacts.Where(c => c.ContactId.ToString() != tempId).ToList();
        if (_custodianId == tempId)
            _custodianId = null;
        StateHasChanged();
    }

    private async Task LoadContacts()
    {
        _contactsLoading = true;
        try
        {
            _contacts = [.. await ReferenceData.ContactsAsync()];
        }
        finally
        {
            _contactsLoading = false;
        }
    }

    private void OnCustodianChanged(string? value)
    {
        _custodianId = string.IsNullOrWhiteSpace(value) ? null : value;
        _custodianError = null;
    }

    private void OnAccountTypeChanged(AccountType type)
    {
        _accountType = type;
        _typeError = false;
    }

    private async Task LoadCurrencies()
    {
        _currencies = [.. await ReferenceData.ActiveCurrenciesAsync()];
        _currencyOptions = await ReferenceData.CurrencyOptionsAsync();

        if (!string.IsNullOrEmpty(_currencyCode) && _currencies.Count > 0
            && _currencies.All(currency => !string.Equals(currency.CurrencyCode, _currencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            _currencyCode = _currencies[0].CurrencyCode;
        }
    }

    private Task OnCurrencyChanged(string value)
    {
        _currencyCode = value;
        return Task.CompletedTask;
    }

    private async Task<bool> SaveAsync()
    {
        _nameError = string.IsNullOrWhiteSpace(_name);
        _typeError = _accountType == AccountType.Unknown;
        _custodianError = null;
        if (_nameError || _typeError)
            return false;

        if (string.IsNullOrWhiteSpace(_currencyCode))
        {
            Snackbar.Add("Currency is required.", Severity.Error);
            return false;
        }

        // Let any in-flight inline contact create land so the real id is posted, not a temp one.
        await ContactCreator.WhenSettledAsync();

        var newAccount = new NewAccount
        {
            Description   = (_description ?? string.Empty).Trim(),
            Name          = _name!.Trim(),
            AccountNumber = string.IsNullOrWhiteSpace(_accountNumber) ? null : _accountNumber.Trim(),
            AccountType   = _accountType,
            Opened        = _opened,
            Closed        = IsEdit ? _closed : null,
            CurrencyCode  = _currencyCode.Trim().ToUpperInvariant(),
            // Archive/restore is a separate row action — preserve whatever the record already has.
            Archived      = IsEdit && Account!.Archived is not null,
            // A temp id from an inline create maps to the id the server issued; a create that failed
            // maps to null, so a bogus id is never posted.
            CustodianId   = Guid.TryParse(ContactCreator.Resolve(_custodianId), out var custodian) ? custodian : null,
        };

        var result = IsEdit
            ? await Accounts.UpdateAsync(Account!.AccountId, newAccount)
            : await Accounts.CreateAsync(newAccount);
        if (result.IsSuccess)
        {
            Snackbar.Add(IsEdit ? "Account updated." : "Account created.", Severity.Success);
            return true;
        }

        // A custodian-validation 400 (contact not found / archived) is shown inline on the
        // picker and keeps the dialog open (A11Y-9); anything else falls back to a toast.
        if (result.Status == System.Net.HttpStatusCode.BadRequest && _custodianId is not null
            && (result.Problem?.Detail?.Contains("contact", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            _custodianError = string.IsNullOrWhiteSpace(result.Problem?.Detail)
                ? "That contact can't be used as a custodian — pick an active one."
                : result.Problem!.Detail;
            return false;
        }

        Snackbar.Add($"Unable to {(IsEdit ? "update" : "create")} account: {result.Error}", Severity.Error);
        return false;
    }
}
