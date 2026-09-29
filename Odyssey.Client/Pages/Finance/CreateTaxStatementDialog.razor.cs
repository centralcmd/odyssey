using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class CreateTaxStatementDialog
{
    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>When set, the dialog edits this statement. Null = create mode.</summary>
    [Parameter] public ExistingTaxStatement? Statement { get; set; }

    private bool IsEdit => Statement is not null;

    private string? _name;
    private int _fiscalYear = DateTime.UtcNow.Year - 1; // most recent completed tax year
    private DateTime? _startDate;
    private DateTime? _endDate;
    private string _baseCurrencyCode = string.Empty;

    // The declared figures are held as TEXT, not decimal?, because OdsMoneyField is a string control:
    // round-tripping through decimal? on every keystroke would rewrite a partial entry ("1250.") the
    // moment it was typed. They are parsed once, on submit.
    private string? _totalAssetsText;
    private string? _totalLiabilitiesText;
    private string? _totalIncomeText;
    private string? _assessedTaxText;
    private string? _netWorthText;
    private string? _settlementAmountText;
    private DateTime? _settledAtUtc;
    private DateTime? _filedAtUtc;
    private DateTime? _taxOfficeApprovedAtUtc;
    private string? _notes;
    private IReadOnlyCollection<string> _taxTags = [];
    private IReadOnlyCollection<string> _incomeTags = [];
    private IReadOnlyCollection<string> _settlementTags = [];
    private DateTime? _settlementStartDate;
    private DateTime? _settlementEndDate;
    private bool _rangeTouched;
    private string? _rangeAnnouncement;
    private OdsDatePicker? _settlementStartPicker;
    private bool _settlementRangeError;
    private string? _settlementTagsError;
    private IReadOnlyList<OdsOption> _tagOptions = [];
    private bool _canCreateTag;

    // Tags created inline from either derivation field, ahead of the reference-cache refresh, so both
    // fields see the new tag at once.
    private readonly List<OdsOption> _createdTags = [];

    private IReadOnlyList<OdsOption> _tagOpts => _createdTags.Count == 0
        ? _tagOptions
        : [.. _createdTags.Where(c => _tagOptions.All(o => o.Value != c.Value)), .. _tagOptions];

    private OdsOption? CreateTagOption(string text, string? kind)
    {
        var option = TagCreator.Begin(text);
        if (option is not null)
            _createdTags.Add(option);
        return option;
    }

    private void OnTagCreateFailed(string tempId)
    {
        _createdTags.RemoveAll(o => o.Value == tempId);
        _taxTags = [.. _taxTags.Where(id => id != tempId)];
        _incomeTags = [.. _incomeTags.Where(id => id != tempId)];
        _settlementTags = [.. _settlementTags.Where(id => id != tempId)];
        StateHasChanged();
    }

    private List<ExistingCurrency> _currencies = [];
    private IReadOnlyList<OdsOption> _currencyOptions = [];
    private bool _nameError;
    private bool _dateError;

    protected override async Task OnInitializedAsync()
    {
        TagCreator.OnCreateFailed = OnTagCreateFailed;
        if (OperatingSystem.IsBrowser())
        {
            var user = await AuthenticationStateProvider.GetUserAsync();
            _canCreateTag = user.HasPermission(PermissionClaims.TransactionTagsCreate);
        }

        if (Statement is { } statement)
        {
            _name = statement.Name;
            _fiscalYear = statement.FiscalYear;
            _startDate = statement.StartDate;
            _endDate = statement.EndDate;
            _baseCurrencyCode = statement.BaseCurrencyCode;
            _totalAssetsText = MoneyText(statement.DeclaredTotalAssets);
            _totalLiabilitiesText = MoneyText(statement.DeclaredTotalLiabilities);
            _netWorthText = MoneyText(statement.DeclaredNetWorth);
            _totalIncomeText = MoneyText(statement.DeclaredTotalIncome);
            _assessedTaxText = MoneyText(statement.AssessedTax);
            _settlementAmountText = MoneyText(statement.SettlementAmount);
            _settledAtUtc = statement.SettledAtUtc;
            _filedAtUtc = statement.FiledAtUtc;
            _taxOfficeApprovedAtUtc = statement.TaxOfficeApprovedAtUtc;
            _notes = statement.Notes;
            _taxTags = [.. statement.TaxTagIds.Select(id => id.ToString())];
            _incomeTags = [.. statement.IncomeTagIds.Select(id => id.ToString())];
            _settlementTags = [.. statement.SettlementTagIds.Select(id => id.ToString())];
            _settlementStartDate = statement.SettlementStartDate;
            _settlementEndDate = statement.SettlementEndDate;
            _rangeTouched = statement.SettlementRangeCustom;
        }
        else
        {
            _name = $"Tax year {_fiscalYear}";
            _startDate = new DateTime(_fiscalYear, 1, 1);
            _endDate = new DateTime(_fiscalYear, 12, 31);
        }

        if (!OperatingSystem.IsBrowser())
            return;

        if (IsEdit)
        {
            await Task.WhenAll(LoadCurrencies(), LoadTags());
        }
        else
        {
            _baseCurrencyCode = UserPreferences.DefaultCurrency ?? string.Empty;
            await LoadCurrencies();
        }
    }

    /// <summary>Renders a stored figure for the money editor — invariant, so it round-trips through
    /// <see cref="ParseMoney"/> unchanged whatever the browser's culture.</summary>
    private static string MoneyText(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static decimal? ParseMoney(string? text) => OdsMoneyText.Parse(text);

    private async Task LoadTags()
    {
        var tags = await ReferenceData.TransactionTagsAsync();
        _tagOptions = [.. tags.Where(t => t.Archived is null)
            .OrderBy(t => t.Name, StringComparer.CurrentCulture)
            .Select(t => new OdsOption(t.TransactionTagId.ToString(), t.Name))];
    }

    private async Task LoadCurrencies()
    {
        _currencies = [.. await ReferenceData.ActiveCurrenciesAsync()];
        _currencyOptions = await ReferenceData.CurrencyOptionsAsync();

        if (!string.IsNullOrEmpty(_baseCurrencyCode) && _currencies.Count > 0
            && _currencies.All(c => !string.Equals(c.CurrencyCode, _baseCurrencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            _baseCurrencyCode = _currencies[0].CurrencyCode;
        }
    }

    // Keep the name + period in step with the fiscal year (until the user edits the
    // name themselves), mirroring the design-system create dialog. Edit mode never
    // cascades — the year just updates on its own, matching the DS's editing branch.
    private void OnYearChanged(decimal? value)
    {
        var year = value is null ? _fiscalYear : (int)value.Value;

        if (IsEdit)
        {
            _fiscalYear = year;
            return;
        }

        if (string.Equals(_name, $"Tax year {_fiscalYear}", StringComparison.Ordinal))
            _name = $"Tax year {year}";

        if (year is >= 1900 and <= 2200)
        {
            _startDate = new DateTime(year, 1, 1);
            _endDate = new DateTime(year, 12, 31);
        }
        _fiscalYear = year;
    }

    // An untouched settlement range tracks the period, +1 year — the shared server default.
    private void FollowPeriod()
    {
        if (_rangeTouched || _startDate is null || _endDate is null)
            return;
        (_settlementStartDate, _settlementEndDate) = TaxSettlementRange.Default(_startDate.Value, _endDate.Value);
    }

    // The picker can raise ValueChanged without a user edit (it normalises the bound value on first
    // render), so a range becomes custom only when it now differs from the default. It never turns
    // back into a following one here — only an explicit Reset does that, so a stored custom range
    // that happens to equal the default keeps its SettlementRangeCustom state.
    private void TouchRange()
    {
        _settlementRangeError = false;
        if (_rangeTouched)
            return;
        if (_startDate is { } periodStart && _endDate is { } periodEnd)
        {
            var (start, end) = TaxSettlementRange.Default(periodStart, periodEnd);
            if (_settlementStartDate?.Date == start.Date && _settlementEndDate?.Date == end.Date)
                return;
        }
        _rangeTouched = true;
        _rangeAnnouncement = "Settlement range set to a custom range.";
    }

    // Reset removes the focused button itself, so focus moves to the range it just reset (WCAG 2.4.3).
    private async Task ResetRange()
    {
        _rangeTouched = false;
        _settlementRangeError = false;
        FollowPeriod();
        _rangeAnnouncement = "Settlement range follows the period, +1 year.";
        if (_settlementStartPicker is not null)
            await _settlementStartPicker.FocusAsync();
    }

    // A settlement-overlap error on show is re-checked as the tax-payment tags change, so removing
    // the conflicting tag there clears it without another save attempt.
    private void OnTaxTagsChanged(IReadOnlyCollection<string> value)
    {
        _taxTags = value;
        if (_settlementTagsError is not null)
            _settlementTagsError = SettlementOverlapError();
    }

    private void OnSettlementTagsChanged(IReadOnlyCollection<string> value)
    {
        _settlementTags = value;
        _settlementTagsError = null;
    }

    private string? SettlementOverlapError()
    {
        var overlap = TaxSettlementRange.Overlap(_taxTags, _settlementTags);
        if (overlap.Count == 0)
            return null;
        var names = overlap.Select(id => _tagOpts.FirstOrDefault(o => o.Value == id)?.Label ?? id);
        return $"{string.Join(", ", names)} {(overlap.Count == 1 ? "is" : "are")} also a tax-payment tag — it would be counted twice.";
    }

    private IEnumerable<Guid> ResolveTagIds(IReadOnlyCollection<string> ids) => ids
        .Select(id => Guid.TryParse(TagCreator.Resolve(id), out var tagId) ? tagId : (Guid?)null)
        .Where(id => id is not null)
        .Select(id => id!.Value);

    private async Task<bool> SaveAsync()
    {
        _nameError = string.IsNullOrWhiteSpace(_name);
        _dateError = _startDate is not null && _endDate is not null && _endDate.Value.Date < _startDate.Value.Date;
        _settlementRangeError = IsEdit && _rangeTouched && _settlementStartDate is not null && _settlementEndDate is not null
            && _settlementEndDate.Value.Date < _settlementStartDate.Value.Date;
        _settlementTagsError = IsEdit ? SettlementOverlapError() : null;
        if (_nameError || _dateError || _settlementRangeError || _settlementTagsError is not null)
            return false;
        if (IsEdit && _rangeTouched && (_settlementStartDate is null || _settlementEndDate is null))
        {
            Snackbar.Add("Set both settlement dates, or reset the range to follow the period.", Severity.Error);
            return false;
        }

        if (_fiscalYear is < 1900 or > 2200)
        {
            Snackbar.Add("Enter a valid year (1900–2200).", Severity.Error);
            return false;
        }
        if (_startDate is null || _endDate is null)
        {
            Snackbar.Add("Period start and end are required.", Severity.Error);
            return false;
        }
        if (string.IsNullOrWhiteSpace(_baseCurrencyCode))
        {
            Snackbar.Add("Base currency is required.", Severity.Error);
            return false;
        }

        if (IsEdit)
        {
            var update = new UpdateTaxStatement
            {
                Name = _name!.Trim(),
                FiscalYear = _fiscalYear,
                StartDate = _startDate.Value,
                EndDate = _endDate.Value,
                BaseCurrencyCode = _baseCurrencyCode.Trim().ToUpperInvariant(),
                DeclaredTotalAssets = ParseMoney(_totalAssetsText),
                DeclaredTotalLiabilities = ParseMoney(_totalLiabilitiesText),
                DeclaredNetWorth = ParseMoney(_netWorthText),
                DeclaredTotalIncome = ParseMoney(_totalIncomeText),
                AssessedTax = ParseMoney(_assessedTaxText),
                SettlementAmount = ParseMoney(_settlementAmountText),
                SettledAtUtc = _settledAtUtc,
                SettlementStartDate = _rangeTouched ? _settlementStartDate : null,
                SettlementEndDate = _rangeTouched ? _settlementEndDate : null,
                FiledAtUtc = _filedAtUtc,
                TaxOfficeApprovedAtUtc = _taxOfficeApprovedAtUtc,
                Notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes!.Trim(),
                // Archive/restore stays a separate row action — preserve whatever the record already has.
                Archived = Statement!.Archived is not null,
            };

            if (!(await TaxStatements.UpdateAsync(Statement.TaxStatementId, update)).Toast(Snackbar, "Update failed"))
                return false;

            // Let any in-flight inline tag create land, then map staged ids to the real ones; a create
            // that failed resolves to null and is dropped rather than posted.
            await TagCreator.WhenSettledAsync();
            var tags = new UpdateTaxStatementTags
            {
                TaxTagIds = [.. ResolveTagIds(_taxTags)],
                IncomeTagIds = [.. ResolveTagIds(_incomeTags)],
                SettlementTagIds = [.. ResolveTagIds(_settlementTags)],
            };
            if (!(await TaxStatements.UpdateTagsAsync(Statement.TaxStatementId, tags)).Toast(Snackbar, "Tag update failed"))
                return false;

            Snackbar.Add("Tax statement updated.", Severity.Success);
            return true;
        }

        var newStatement = new NewTaxStatement
        {
            Name = _name!.Trim(),
            FiscalYear = _fiscalYear,
            StartDate = _startDate.Value,
            EndDate = _endDate.Value,
            BaseCurrencyCode = _baseCurrencyCode.Trim().ToUpperInvariant(),
            DeclaredTotalAssets = ParseMoney(_totalAssetsText),
            DeclaredTotalLiabilities = ParseMoney(_totalLiabilitiesText),
            DeclaredNetWorth = ParseMoney(_netWorthText),
            DeclaredTotalIncome = ParseMoney(_totalIncomeText),
            AssessedTax = ParseMoney(_assessedTaxText),
            SettlementAmount = ParseMoney(_settlementAmountText),
            SettledAtUtc = _settledAtUtc,
            FiledAtUtc = _filedAtUtc,
            TaxOfficeApprovedAtUtc = _taxOfficeApprovedAtUtc,
            Notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes!.Trim(),
        };

        return (await TaxStatements.CreateAsync(newStatement)).Toast(Snackbar, "Unable to create tax statement", "Tax statement created.");
    }
}
