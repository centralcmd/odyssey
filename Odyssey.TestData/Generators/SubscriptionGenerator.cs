using Odyssey.Context;
using Odyssey.TestData.Catalog;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic manually tracked subscriptions (issue #293). Anchored to
/// <see cref="DemoDataDefaults.AnchorDate"/> so the derived billing anchor of each row is stable, and
/// the set deliberately exercises every state the list/read surface can report: each
/// <see cref="BillingInterval"/>, a linked-company row and an unlinked one, an <c>ExternalId</c>, a
/// paused row and an archived row. Currencies stay within the demo FX matrix (USD/EUR/GBP).
///
/// Rows link to existing contacts by their stable deterministic ids; no contact is created
/// here.
/// </summary>
public static class SubscriptionGenerator
{
    private sealed record SubscriptionSpec(
        string Name,
        string? ExternalId,
        string? ContactName,
        DateOnly StartDate,
        DateOnly? EndDate,
        decimal Amount,
        string CurrencyCode,
        BillingInterval Interval,
        int IntervalCount,
        DateOnly FirstBillingDate,
        string? Notes,
        bool Paused,
        bool Archived);

    public static Guid IdFor(string name) => DeterministicGuid.From($"subscription::{name}");

    public static List<Subscription> Build(DateTime anchor)
    {
        var year = anchor.Year;

        var specs = new List<SubscriptionSpec>
        {
            // Plain monthly (every month).
            new(
                "Netflix", "MBR-4471-2093", Catalog.Contacts.Netflix,
                DO(year - 2, 3, 15), null, 15.49m, Currencies.Usd, BillingInterval.Monthly, 1,
                DO(year - 2, 3, 15), "Standard with ads plan.", Paused: false, Archived: false),

            new(
                "Spotify Family", "SPOT-88213", Catalog.Contacts.Spotify,
                DO(year - 3, 1, 5), null, 16.99m, Currencies.Usd, BillingInterval.Monthly, 1,
                DO(year - 3, 1, 5), "Family plan, six seats.", Paused: false, Archived: false),

            // Quarterly cadence (every 3 months) — exercises IntervalCount > 1. Paused (still visible).
            new(
                "City Gym", "GYM-2025-771", null,
                DO(year - 1, 9, 1), null, 39.00m, Currencies.Gbp, BillingInterval.Monthly, 3,
                DO(year - 1, 9, 1), "Billed quarterly; paused over the summer.", Paused: true, Archived: false),

            // Yearly cadence with a first-billing anchor that drives a month+day label.
            new(
                "Domain Renewal", "DN-odyssey.example", null,
                DO(year - 4, 11, 20), null, 18.00m, Currencies.Eur, BillingInterval.Yearly, 1,
                DO(year - 4, 11, 20), null, Paused: false, Archived: false),

            // Bi-weekly cadence (every 2 weeks) — a meal-kit box.
            new(
                "Weekly Meal Kit", null, null,
                DO(year, 2, 3), null, 59.90m, Currencies.Usd, BillingInterval.Weekly, 2,
                DO(year, 2, 3), "Delivered every other Tuesday.", Paused: false, Archived: false),

            // Ended — a lapsed annual trial (endDate in the past) kept on the list, not archived. Its
            // derived status reads "Ended" (supersedes Paused) and it drops out of the run-rate/renewals.
            new(
                "Streamly Annual Trial", "TRIAL-3390", null,
                DO(year - 1, 6, 1), DO(year, 6, 1), 89.00m, Currencies.Usd, BillingInterval.Yearly, 1,
                DO(year - 1, 6, 1), "One-year promo — term lapsed, kept for the record.", Paused: false, Archived: false),

            // Archived (hidden from the default list) — a lapsed news subscription with an end date.
            new(
                "Daily News", "NEWS-55120", null,
                DO(year - 2, 6, 1), DO(year - 1, 6, 1), 9.99m, Currencies.Usd, BillingInterval.Daily, 1,
                DO(year - 2, 6, 1), "Cancelled after the first year.", Paused: false, Archived: true),
        };

        var createdAt = anchor.AddYears(-1);

        return specs
            .Select(spec => new Subscription
            {
                SubscriptionId = IdFor(spec.Name),
                Name = spec.Name,
                ExternalId = spec.ExternalId,
                ContactId = spec.ContactName is null ? null : Catalog.Contacts.IdFor(spec.ContactName),
                StartDate = spec.StartDate,
                EndDate = spec.EndDate,
                Amount = spec.Amount,
                CurrencyCode = spec.CurrencyCode,
                Interval = spec.Interval,
                IntervalCount = spec.IntervalCount,
                FirstBillingDate = spec.FirstBillingDate,
                Notes = spec.Notes,
                Paused = spec.Paused ? createdAt : null,
                Archived = spec.Archived ? createdAt : null,
                CreatedAtUtc = createdAt,
            })
            .ToList();
    }

    private static DateOnly DO(int year, int month, int day) => new(year, month, day);
}
