using Odyssey.Context;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Catalog;

/// <summary>
/// The fully-enumerated shared account portfolio (spec §3.7): one dataset visible to
/// all demo users, covering every non-sentinel <see cref="AccountType"/>, spanning
/// ~10 years with a few closed accounts. Account names double as stable keys.
/// </summary>
public static class Accounts
{
    // Keys referenced by transaction streams (§3.11).
    public const string EverydayChecking = "Everyday Checking";
    public const string EmergencyFund = "Emergency Fund";
    public const string BrokerageAccount = "Brokerage Account";
    public const string WorkplacePension = "Workplace Pension";
    public const string HomeMortgage = "Home Mortgage";
    public const string TravelRewardsCard = "Travel Rewards Card";
    public const string StocksPortfolio = "Stocks Portfolio";
    public const string CarLoanVolvo = "Car Loan (Volvo XC60)";

    // Keys referenced by the estimate/term streams (issues #182, #172).
    public const string CollectiblesAndArt = "Collectibles & Art";
    public const string PrimaryResidence = "Primary Residence";
    public const string FamilyCar = "Family Car (Volvo XC60)";
    public const string HighYieldSavings = "High-Yield Savings";
    public const string RenovationPersonalLoan = "Renovation Personal Loan";

    private static DateTime D(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Row(
        string Name,
        string? AccountNumber,
        AccountType Type,
        string Currency,
        DateTime Opened,
        DateTime? Closed,
        string Description,
        // The contact that holds the account (its custodian), by Contacts key, or null
        // when no custodian applies (physical assets, informal loans) — demonstrates both states.
        string? Custodian = null);

    private static readonly Row[] Definitions =
    [
        new(EverydayChecking, "1002 0034 8150 1042", AccountType.CheckingAccount, Currencies.Usd, D(2016, 4, 1), null, "Primary day-to-day current account", Contacts.FirstNationalBank),
        new(EmergencyFund, "1002 0034 8150 3318", AccountType.SavingsAccount, Currencies.Usd, D(2016, 4, 15), null, "Rainy-day savings buffer", Contacts.FirstNationalBank),
        new(BrokerageAccount, "1002 0034 8150 7790", AccountType.InvestmentAccount, Currencies.Usd, D(2016, 7, 1), null, "Long-term investment brokerage", Contacts.Vanguard),
        new(WorkplacePension, "1002 0034 8150 0026", AccountType.PensionAccount, Currencies.Usd, D(2016, 2, 10), null, "Employer pension scheme", Contacts.Vanguard),
        new(HomeMortgage, "ML-2017-88431", AccountType.Mortgage, Currencies.Usd, D(2017, 9, 1), null, "Mortgage on the primary residence", Contacts.FirstNationalBank),
        new(TravelRewardsCard, "4539 8821 0042 4821", AccountType.CreditCard, Currencies.Usd, D(2018, 5, 20), null, "Rewards credit card", Contacts.FirstNationalBank),
        new(StocksPortfolio, "SE35 5000 0000 0549 0000 4407", AccountType.InvestmentAccount, Currencies.Sek, D(2019, 11, 5), null, "Swedish equities portfolio", Contacts.Vanguard),
        new(CollectiblesAndArt, null, AccountType.OtherAsset, Currencies.Usd, D(2020, 11, 1), null, "Art and collectibles valuation"),
        new("Cash Wallet", null, AccountType.Cash, Currencies.Usd, D(2016, 4, 1), null, "Physical cash on hand"),
        new(PrimaryResidence, null, AccountType.Property, Currencies.Usd, D(2017, 9, 1), null, "Family home"),
        new(FamilyCar, null, AccountType.Vehicle, Currencies.Usd, D(2023, 2, 15), null, "Household vehicle"),
        new(CarLoanVolvo, "CL-2023-10567", AccountType.CarLoan, Currencies.Usd, D(2023, 2, 15), null, "Financing for the family car", Contacts.FirstNationalBank),
        new("Everyday Credit Card", "5412 7534 8890 9912", AccountType.CreditCard, Currencies.Eur, D(2023, 7, 12), null, "Eurozone everyday credit card", Contacts.FirstNationalBank),
        new(RenovationPersonalLoan, "PL-2024-77120", AccountType.PersonalLoan, Currencies.Eur, D(2024, 9, 1), null, "Home renovation loan", Contacts.FirstNationalBank),
        new("Family Loan (owed)", null, AccountType.OtherLiability, Currencies.Gbp, D(2024, 12, 1), null, "Informal loan owed to family"),
        new(HighYieldSavings, "DE89 3704 0044 0590 0490 04", AccountType.SavingsAccount, Currencies.Eur, D(2025, 11, 1), null, "High-interest savings account", Contacts.FirstNationalBank),
        new("Outstanding Tax Bill", "TX-2025-0098", AccountType.TaxDebt, Currencies.Eur, D(2025, 11, 15), null, "Assessed tax liability", Contacts.Irs),
        new("Crypto Brokerage", "1002 0034 8150 5521", AccountType.InvestmentAccount, Currencies.Usd, D(2026, 2, 3), null, "Cryptocurrency brokerage"),
        new("Student Checking (old)", "GB29 NWBK 6016 1300 0022 10", AccountType.CheckingAccount, Currencies.Gbp, D(2016, 2, 1), D(2019, 8, 15), "Closed student current account"),
        new("Student Loan (paid off)", "SL-2016-44210", AccountType.StudentLoan, Currencies.Usd, D(2016, 2, 1), D(2022, 6, 30), "Repaid student loan"),
        new("Old Car Loan (paid off)", "CL-2018-22107", AccountType.CarLoan, Currencies.Usd, D(2018, 3, 1), D(2022, 1, 15), "Repaid car loan"),
    ];

    public static Guid IdFor(string name) => DeterministicGuid.From($"account::{name}");

    public static List<Account> Build() =>
        Definitions
            .Select(definition => new Account
            {
                AccountId = IdFor(definition.Name),
                Name = definition.Name,
                Description = definition.Description,
                AccountNumber = definition.AccountNumber,
                AccountType = definition.Type,
                CurrencyCode = definition.Currency,
                Opened = definition.Opened,
                Closed = definition.Closed,
                Archived = null,
                CustodianId = definition.Custodian is null ? null : Contacts.IdFor(definition.Custodian),
            })
            .ToList();
}
