using Odyssey.Context;
using Odyssey.TestData.Catalog;
using TermLabel = Odyssey.Dtos.Finance.TermLabel;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic contracts and their parties (issue #174). Anchored to
/// <see cref="DemoDataDefaults.AnchorDate"/> so each contract's derived status is stable, and the set
/// deliberately exercises every <see cref="ContractType"/> — the original four (Employment, Service,
/// Rental, Other) and the four later ones (Insurance, Subscription, Purchase, Membership) — every
/// derived status (Active, Upcoming, Expired, Archived) and all three party kinds (an account, a
/// contact/"institution" and an insurance policy).
///
/// <para>
/// Since issue #140 the set also covers the fifth derived status,
/// <b>Paused</b>: one contract carries a pause stamp <i>and</i> an in-force periodic fee, so the demo
/// shows both halves of the feature at once — the amber status, and the run rate and next-charges
/// list it is absent from. Seeding a paused contract with no price on file would leave the exclusion
/// invisible.
/// </para>
///
/// <para>
/// It also seeds the contract <b>fee terms</b> the page-header roll-up is computed from: the monthly
/// and yearly run rate and the derived "next charges". Without at least one in-force periodic fee on
/// an Active contract both read empty, so the demo would show a working feature as an absent one. One
/// fee is priced in a non-base currency on purpose, so the converted total has something to convert;
/// one is a <c>OneTime</c> fee, which carries no cadence and must therefore appear in neither figure.
/// </para>
///
/// Since issue #121 every party also carries a <see cref="ContractPartyRole"/> and an optional term.
/// The set covers all five named v1 roles (Employee, Employer, Buyer, Seller, ServiceProvider), leaves
/// at least one party <see cref="ContractPartyRole.Unspecified"/> — the state the migration backfills
/// every pre-#121 row to, and the one the tile's kind-word fallback renders — and gives at least one
/// party a non-default term. Roles are deliberately unconstrained by party kind: an account may carry
/// any role.
///
/// Parties link to the existing accounts, contacts and insurance policies by their stable
/// deterministic ids; no such record is created here. No files are attached — the demo dataset has no
/// file library to reference.
/// </summary>
public static class ContractGenerator
{
    private enum PartyKind { Account, Contact }

    /// <param name="FromDate">Offset in months from the contract's own start; null is the default term.</param>
    /// <param name="ToDate">As above, for the end of the term.</param>
    private sealed record PartySpec(
        PartyKind Kind,
        string TargetName,
        ContractPartyRole Role,
        int? FromDate = null,
        int? ToDate = null);

    private sealed record ContractSpec(
        string Name,
        ContractType Type,
        string? Description,
        DateTime StartDate,
        DateTime? EndDate,
        bool Archived,
        IReadOnlyList<PartySpec> Parties,
        /// <summary>Offset in months from the anchor at which this contract was paused; null is not paused.</summary>
        int? PausedMonths = null);

    /// <summary>
    /// One priced series on a contract. <paramref name="Interval"/> null is a fee with no cadence —
    /// a one-off — which is the case the run rate and the charge projection both exclude.
    /// </summary>
    private sealed record ContractTermSpec(
        string ContractName,
        string Label,
        decimal Value,
        string Currency,
        Interval? Interval,
        int? Count,
        int EffectiveFromMonths,
        int? AnchorDays = null,
        string? Note = null);

    public static Guid IdFor(string name) => DeterministicGuid.From($"contract::{name}");

    /// <summary>
    /// The id of one seeded contract term. The label is part of the key because it is part of the
    /// series key — two fees on one contract can share a kind and a date.
    /// </summary>
    public static Guid TermIdFor(string contractName, string label, DateTime effectiveFrom) =>
        DeterministicGuid.From($"contract-term::{contractName}::{TermLabel.Key(label) ?? ""}@{effectiveFrom:yyyy-MM-dd}");

    private static Guid PartyIdFor(string contractName, int index) =>
        DeterministicGuid.From($"contract-party::{contractName}#{index}");

    public static (List<Contract> Contracts, List<ContractParty> Parties, List<Term> Terms) Build(DateTime anchor)
    {
        var specs = new List<ContractSpec>
        {
            // Employment — open-ended, started two years ago → Active. Parties: the employer
            // (contact) and the account the salary is paid into.
            new(
                "Employment Agreement — Globex", ContractType.Employment,
                "Full-time permanent employment contract.",
                anchor.AddYears(-2), null, false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.Globex, ContractPartyRole.Employer),
                    // The salary account, in the role the contract gives it. A non-default term: the
                    // payment destination changed six months in, which is the case the term exists for.
                    new(PartyKind.Account, Catalog.Accounts.EverydayChecking, ContractPartyRole.Employee, FromDate: 6),
                ]),

            // Rental — fixed term spanning the anchor → Active. Party: the landlord (contact).
            new(
                "Apartment Lease", ContractType.Rental,
                "12-month residential tenancy agreement.",
                anchor.AddMonths(-6), anchor.AddMonths(6), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.Landlord, ContractPartyRole.Seller),
                ]),

            // Service — starts in the future → Upcoming. Party: the utility provider (contact).
            new(
                "Utilities Supply Contract", ContractType.Service,
                "Combined power and water supply agreement (starts next quarter).",
                anchor.AddMonths(2), anchor.AddYears(1).AddMonths(2), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.CityPowerWater, ContractPartyRole.ServiceProvider),
                ]),

            // Other — links the household's insurer to the mortgaged property account. Active
            // (started a year ago, open-ended). Exercises a two-party contract across both kinds.
            new(
                "Mortgage Insurance Mandate", ContractType.Other,
                "Standing mandate tying the home insurance cover to the mortgage account.",
                anchor.AddYears(-1), null, false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.Globex, ContractPartyRole.Buyer),
                    // Left Unspecified on purpose: the state every pre-#121 row was backfilled to, and
                    // the one whose tile falls back to the kind word rather than rendering a sentinel.
                    new(PartyKind.Account, Catalog.Accounts.HomeMortgage, ContractPartyRole.Unspecified),
                ]),

            // Rental, ending inside the 45-day window → the header signal's "Ending soon" group, and
            // the run rate's largest recurring line.
            new(
                "Harbor Point Parking — Space 14", ContractType.Rental,
                "Twelve-month parking licence on space 14. Renews only by a fresh agreement — give notice 30 days before the end date.",
                anchor.AddMonths(-11).AddDays(-18), anchor.AddDays(30), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.CityPowerWater, ContractPartyRole.ServiceProvider),
                ]),

            // Service, starting inside the same window → the mirror group, "Starting soon". Its fee is
            // in force but its contract has not begun, so its first charge lands on the start date.
            new(
                "Northwind Energy — Fixed Tariff", ContractType.Service,
                "Twelve-month fixed electricity tariff. The standing charge and unit rate are fixed for the term.",
                anchor.AddDays(26), anchor.AddYears(1).AddDays(25), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.CityPowerWater, ContractPartyRole.ServiceProvider),
                ]),

            // Insurance — the policy held as an agreement in its own right. Active, open-ended.
            new(
                "Home Insurance — Policy Agreement", ContractType.Insurance,
                "The contract of insurance itself, kept alongside the policy record it prices.",
                anchor.AddMonths(-8), null, false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.StateFarm, ContractPartyRole.ServiceProvider),
                ]),

            // Subscription — a recurring supply agreement, quarterly rather than monthly so the
            // cadence multiplier (IntervalCount) is exercised by the run rate rather than assumed 1.
            new(
                "Cloud Storage — Business Plan", ContractType.Subscription,
                "Recurring storage and backup plan, billed quarterly.",
                anchor.AddMonths(-14), anchor.AddMonths(10), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.Globex, ContractPartyRole.ServiceProvider),
                ]),

            // Purchase — a one-off recorded by its completion date, so it carries no term at all. Its
            // one fee is OneTime, which has no cadence: it must appear in NEITHER the run rate nor the
            // charges, which is exactly the exclusion worth demonstrating.
            new(
                "Maple St Residence — Purchase", ContractType.Purchase,
                "Purchase of the Maple St property — a one-off agreement recorded by its completion date, not a term.",
                anchor.AddYears(-5), null, false,
                [
                    new(PartyKind.Account, Catalog.Accounts.HomeMortgage, ContractPartyRole.Buyer),
                    new(PartyKind.Contact, Catalog.Contacts.FirstNationalBank, ContractPartyRole.Seller),
                ]),

            // Membership — annual, and priced in a non-base currency so the run rate's conversion (and
            // its unconverted-currency reporting, if the rate is ever missing) has a real case.
            new(
                "FitZone — Membership", ContractType.Membership,
                "Annual gym membership at the new branch.",
                anchor.AddMonths(-3), anchor.AddMonths(9), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.Globex, ContractPartyRole.ServiceProvider),
                ]),

            // Paused (issue #140) — a live subscription frozen for the season. Active-shaped on its
            // dates (started, not yet ended, not archived) so the pause is what the derivation
            // reports, and it carries an in-force monthly fee so the run rate and the next-charges
            // list have something to visibly NOT count. Resuming it is one write.
            new(
                "Meal Kit Delivery — Weekly Box", ContractType.Subscription,
                "Weekly recipe-box subscription, frozen over the summer. The price stays on file and comes back in force on resume.",
                anchor.AddMonths(-9), anchor.AddMonths(15), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.Globex, ContractPartyRole.ServiceProvider),
                ],
                PausedMonths: -1),

            // Archived — an expired prior service contract, retained for reference (hidden by default).
            new(
                "Previous Broadband Contract", ContractType.Service,
                "Superseded broadband contract, kept for records.",
                anchor.AddYears(-3), anchor.AddYears(-1), true,
                [
                    // A closed term: this provider left the role a year before the contract expired.
                    new(PartyKind.Contact, Catalog.Contacts.CityPowerWater, ContractPartyRole.ServiceProvider,
                        FromDate: 0, ToDate: 12),
                ]),
        };

        var contracts = new List<Contract>();
        var parties = new List<ContractParty>();
        var createdAt = anchor.AddYears(-2);

        foreach (var spec in specs)
        {
            var contractId = IdFor(spec.Name);
            contracts.Add(new Contract
            {
                ContractId = contractId,
                Name = spec.Name,
                Type = spec.Type,
                Description = spec.Description,
                StartDate = spec.StartDate,
                EndDate = spec.EndDate,
                Archived = spec.Archived ? createdAt.AddYears(1) : null,
                // Derived from the anchor, never from the wall clock, so the seed stays deterministic
                // and re-running it stays idempotent.
                Paused = spec.PausedMonths is { } months ? anchor.AddMonths(months) : null,
                CreatedAtUtc = createdAt,
            });

            for (var i = 0; i < spec.Parties.Count; i++)
            {
                var party = spec.Parties[i];
                parties.Add(new ContractParty
                {
                    ContractPartyId = PartyIdFor(spec.Name, i),
                    ContractId = contractId,
                    AccountId = party.Kind == PartyKind.Account
                        ? Catalog.Accounts.IdFor(party.TargetName) : null,
                    ContactId = party.Kind == PartyKind.Contact
                        ? Catalog.Contacts.IdFor(party.TargetName) : null,
                    Role = party.Role,
                    // Offsets are taken from the contract's own start, so no seeded term can begin
                    // before the contract did — the one tie the server validates (issue #121 §8 rule 3).
                    FromDate = party.FromDate is { } from ? spec.StartDate.AddMonths(from) : null,
                    ToDate = party.ToDate is { } to ? spec.StartDate.AddMonths(to) : null,
                });
            }
        }

        return (contracts, parties, BuildTerms(anchor, contracts));
    }

    /// <summary>
    /// The contracts' fee terms — what the roll-up's run rate sums and what its "next charges" project
    /// forward. Every one is a <see cref="TermKind.Fee"/> in <see cref="TermValueUnit.Amount"/>, since
    /// a rate term carries no amount and a percentage fee carries no due figure.
    ///
    /// <para>
    /// The set covers the three things the projection has to get right: a periodic fee with a
    /// multiplier (quarterly = Monthly × 3), a fee whose <c>AnchorDate</c> differs from its
    /// <c>EffectiveFrom</c>, and a supersession — the storage plan's price changed, so only the later
    /// entry is in force and the run rate must not count both.
    /// </para>
    /// </summary>
    private static List<Term> BuildTerms(DateTime anchor, List<Contract> contracts)
    {
        var specs = new List<ContractTermSpec>
        {
            // The parking licence: the largest recurring line, monthly, due on the 1st. Its anchor is
            // deliberately a different date from its effective date.
            new("Harbor Point Parking — Space 14", "Space licence", 165.00m, Currencies.Usd,
                Interval.Monthly, 1, -11, AnchorDays: 3, Note: "Due on the 1st."),
            // A one-off on the same contract: no cadence, so it appears in NEITHER the run rate nor
            // the charges, however close its date.
            new("Harbor Point Parking — Space 14", "Access fob", 40.00m, Currencies.Usd,
                null, null, -11),

            // The tariff that has not started yet. In force as a term, but its contract is Upcoming, so
            // its first charge cannot fall before the start date.
            new("Northwind Energy — Fixed Tariff", "Standing charge", 28.50m, Currencies.Usd,
                Interval.Monthly, 1, 0, Note: "Fixed for the term."),

            new("Home Insurance — Policy Agreement", "Premium", 92.40m, Currencies.Usd,
                Interval.Monthly, 1, -8),

            // Quarterly — Monthly with a count of 3, which is what makes the run rate's ÷ IntervalCount
            // observable rather than assumed.
            new("Cloud Storage — Business Plan", "Plan fee", 270.00m, Currencies.Usd,
                Interval.Monthly, 3, -14, Note: "Introductory quarterly rate."),
            // …superseded a year in. Same series, later date, so only this one is in force.
            new("Cloud Storage — Business Plan", "Plan fee", 315.00m, Currencies.Usd,
                Interval.Monthly, 3, -2, Note: "Renewal quarterly rate."),

            // Priced in EUR, so the converted total has something to convert and the base-currency
            // reader sees a figure that is not simply a sum of face values.
            new("FitZone — Membership", "Annual membership", 540.00m, Currencies.Eur,
                Interval.Annually, 1, -3),

            // The lease's rent — the household's other large monthly line.
            new("Apartment Lease", "Monthly rent", 1850.00m, Currencies.Usd,
                Interval.Monthly, 1, -6, Note: "Due on the 1st."),

            // The paused subscription's fee. In force, periodic and on an Active-shaped contract — so
            // the ONLY reason it is absent from the run rate and the charges is the pause stamp, which
            // is what makes the exclusion legible in the demo rather than merely asserted.
            new("Meal Kit Delivery — Weekly Box", "Weekly box", 78.00m, Currencies.Usd,
                Interval.Monthly, 1, -9, Note: "Suspended while the subscription is paused."),

            // An archived contract still carries its history; it must contribute to neither figure.
            new("Previous Broadband Contract", "Line rental", 45.00m, Currencies.Usd,
                Interval.Monthly, 1, -36),
        };

        var startById = contracts.ToDictionary(c => c.ContractId, c => c.StartDate ?? anchor);
        var terms = new List<Term>();

        foreach (var spec in specs)
        {
            var contractId = IdFor(spec.ContractName);
            // Offsets are measured from the CONTRACT's own start, so no seeded term takes effect before
            // the agreement it prices — the same tie the seeded party terms respect.
            var effectiveFrom = startById[contractId].AddMonths(spec.EffectiveFromMonths);

            terms.Add(new Term
            {
                TermId = TermIdFor(spec.ContractName, spec.Label, effectiveFrom),
                ContractId = contractId,
                AccountId = null,
                TermKind = TermKind.Fee,
                Label = spec.Label,
                LabelKey = TermLabel.Key(spec.Label),
                ValueUnit = TermValueUnit.Amount,
                Value = spec.Value,
                CurrencyCode = spec.Currency,
                Interval = spec.Interval,
                // Non-null IFF the interval is periodic — a meaningless 1 on a one-time fee would be
                // indistinguishable from a deliberate one on the next read-modify-write round trip.
                IntervalCount = spec.Interval is { } interval && interval.IsPeriodic() ? spec.Count ?? 1 : null,
                AnchorDate = spec.AnchorDays is { } days ? effectiveFrom.AddDays(days) : null,
                EffectiveFrom = effectiveFrom,
                Note = spec.Note,
                CreatedAtUtc = effectiveFrom,
            });
        }

        return terms;
    }
}
