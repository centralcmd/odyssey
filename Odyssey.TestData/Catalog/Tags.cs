using Odyssey.Context;

namespace Odyssey.TestData.Catalog;

/// <summary>
/// Deterministic transaction-tag (category) catalog (spec §3.8). Tags are everyday
/// categories with fixed names; their id is derived from the name so references from
/// budgets and transactions stay stable across re-seeds.
/// </summary>
public static class Tags
{
    // Expense categories.
    public const string Groceries = "Groceries";
    public const string DiningOut = "Dining Out";
    public const string Utilities = "Utilities";
    public const string Housing = "Housing";
    public const string Transportation = "Transportation";
    public const string Fuel = "Fuel";
    public const string Healthcare = "Healthcare";
    public const string Insurance = "Insurance";
    public const string Entertainment = "Entertainment";
    public const string Subscriptions = "Subscriptions";
    public const string Travel = "Travel";
    public const string Clothing = "Clothing";
    public const string PersonalCare = "Personal Care";
    public const string HomeMaintenance = "Home Maintenance";
    public const string Education = "Education";
    public const string GiftsDonations = "Gifts & Donations";
    public const string FeesCharges = "Fees & Charges";
    public const string Taxes = "Taxes";
    public const string LoanRepayment = "Loan Repayment";
    public const string Savings = "Savings";
    public const string Investments = "Investments";

    // Income categories.
    public const string Salary = "Salary";
    public const string Bonus = "Bonus";
    public const string Dividends = "Dividends";
    public const string InterestIncome = "Interest Income";
    public const string RentalIncome = "Rental Income";
    public const string Refunds = "Refunds";

    private static readonly (string Name, string Description)[] Definitions =
    [
        (Groceries, "Supermarket and grocery spending"),
        (DiningOut, "Restaurants, cafes and takeaway"),
        (Utilities, "Electricity, water, gas and internet"),
        (Housing, "Rent or mortgage payments"),
        (Transportation, "Public transport and rideshare"),
        (Fuel, "Petrol and charging"),
        (Healthcare, "Medical, dental and pharmacy"),
        (Insurance, "Home, health and vehicle insurance"),
        (Entertainment, "Leisure, events and hobbies"),
        (Subscriptions, "Streaming and recurring services"),
        (Travel, "Flights, hotels and trips"),
        (Clothing, "Apparel and accessories"),
        (PersonalCare, "Haircuts, cosmetics and wellbeing"),
        (HomeMaintenance, "Repairs and household upkeep"),
        (Education, "Courses, books and tuition"),
        (GiftsDonations, "Presents and charitable giving"),
        (FeesCharges, "Bank fees and service charges"),
        (Taxes, "Income and property taxes"),
        (LoanRepayment, "Loan and credit repayments"),
        (Savings, "Transfers to savings"),
        (Investments, "Contributions to investments"),
        (Salary, "Employment income"),
        (Bonus, "Performance and annual bonuses"),
        (Dividends, "Investment dividend income"),
        (InterestIncome, "Interest earned on deposits"),
        (RentalIncome, "Income from rented property"),
        (Refunds, "Refunds and reimbursements"),
    ];

    public static Guid IdFor(string name) => DeterministicGuid.From($"tag::{name}");

    public static List<TransactionTag> Build() =>
        Definitions
            .Select(definition => new TransactionTag
            {
                TransactionTagId = IdFor(definition.Name),
                Name = definition.Name,
                Description = definition.Description,
                Archived = null,
            })
            .ToList();
}
