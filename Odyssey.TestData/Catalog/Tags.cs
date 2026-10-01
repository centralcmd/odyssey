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

    // Most tags carry an icon (issue #279); a few deliberately do not, so both the iconned and the
    // default path are visible in the dev stack.
    private static readonly (string Name, string Description, string? Icon)[] Definitions =
    [
        (Groceries, "Supermarket and grocery spending", "shopping_cart"),
        (DiningOut, "Restaurants, cafes and takeaway", "restaurant"),
        (Utilities, "Electricity, water, gas and internet", "bolt"),
        (Housing, "Rent or mortgage payments", "home"),
        (Transportation, "Public transport and rideshare", "directions_bus"),
        (Fuel, "Petrol and charging", "local_gas_station"),
        (Healthcare, "Medical, dental and pharmacy", "medical_services"),
        (Insurance, "Home, health and vehicle insurance", "shield"),
        (Entertainment, "Leisure, events and hobbies", "movie"),
        (Subscriptions, "Streaming and recurring services", "subscriptions"),
        (Travel, "Flights, hotels and trips", "flight"),
        (Clothing, "Apparel and accessories", "checkroom"),
        (PersonalCare, "Haircuts, cosmetics and wellbeing", null),
        (HomeMaintenance, "Repairs and household upkeep", "build"),
        (Education, "Courses, books and tuition", "school"),
        (GiftsDonations, "Presents and charitable giving", "card_giftcard"),
        (FeesCharges, "Bank fees and service charges", null),
        (Taxes, "Income and property taxes", "request_quote"),
        (LoanRepayment, "Loan and credit repayments", null),
        (Savings, "Transfers to savings", "savings"),
        (Investments, "Contributions to investments", "trending_up"),
        (Salary, "Employment income", "payments"),
        (Bonus, "Performance and annual bonuses", null),
        (Dividends, "Investment dividend income", null),
        (InterestIncome, "Interest earned on deposits", null),
        (RentalIncome, "Income from rented property", null),
        (Refunds, "Refunds and reimbursements", null),
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
                Icon = definition.Icon,
            })
            .ToList();
}
