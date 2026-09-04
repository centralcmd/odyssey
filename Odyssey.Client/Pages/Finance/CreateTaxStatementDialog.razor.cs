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
    private IReadOnlyList<OdsOption> _tagOptions = [];

    private List<ExistingCurrency> _currencies = [];
    private IReadOnlyList<OdsOption> _currencyOptions = [];
    private bool _nameError;
    private bool _dateError;

    protected override async Task OnInitializedAsync()
    {
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

    private async Task<bool> SaveAsync()
    {
        _nameError = string.IsNullOrWhiteSpace(_name);
        _dateError = _startDate is not null && _endDate is not null && _endDate.Value.Date < _startDate.Value.Date;
        if (_nameError || _dateError)
            return false;

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
                FiledAtUtc = _filedAtUtc,
                TaxOfficeApprovedAtUtc = _taxOfficeApprovedAtUtc,
                Notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes!.Trim(),
                // Archive/restore stays a separate row action — preserve whatever the record already has.
                Archived = Statement!.Archived is not null,
            };

            if (!(await TaxStatements.UpdateAsync(Statement.TaxStatementId, update)).Toast(Snackbar, "Update failed"))
                return false;

            var tags = new UpdateTaxStatementTags
            {
                TaxTagIds = [.. _taxTags.Select(Guid.Parse)],
                IncomeTagIds = [.. _incomeTags.Select(Guid.Parse)],
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
