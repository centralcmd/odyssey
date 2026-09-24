using System.Globalization;
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
/// <para>
/// Since issue #159 the employment contract carries both DIRECTIONS at once — an incoming salary and
/// an outgoing union fee, anchored to different days of the month. That is what makes the incoming
/// run rate, the net and the second movement list non-empty in the demo, and it is the one shape a
/// single flag on the contract could not express.
/// </para>
///
/// <para>
/// Since issue #145 it also covers the two <b>signature</b> states: one <b>Draft</b> (no stamps, a
/// future start date, and a fully priced fee — so the money gate is visible rather than asserted) and
/// one <b>Ready</b> (a ready stamp, never signed, with a term that has since run out, which reads
/// Ready and not Expired). Every other contract is seeded SIGNED, because the signature layer sits
/// above the whole date chain: leaving them unsigned would collapse every status this set exists to
/// demonstrate into Draft.
/// </para>
///
/// Since issue #121 every party also carries a <see cref="ContractPartyRole"/> and an optional term,
/// and since issue #157 the legal roles are decided by the contract's TYPE
/// (<c>ContractPartyRoleMatrix</c>). <b>Every seeded party is a legal cell</b> — asserted by a seeder
/// test rather than by inspection, because a violation here would be demo data the API itself would
/// refuse. The set covers both tiers of the matrix: suggested roles on every type, plus the
/// <c>Guarantor</c> and <c>Other</c> allowed-but-not-suggested cases, and at least one party carries a
/// non-default term. Since issue #169 it also seeds the two type-specific OBJECT roles —
/// <c>Property</c> on the house purchase and <c>Collateral</c> on the car loan. Since issue #187 it
/// seeds a fixed-term <c>Deposit</c> with both of its suggested roles, <c>Depositor</c> and
/// <c>Custodian</c>, and an incoming interest term. That is not decoration:
/// the browser and API E2E tiers read seeded data, so a role absent from this set is a role no
/// full-stack test ever renders or round-trips. Roles stay unconstrained by party KIND — an account may
/// hold any role its type permits.
///
/// Parties link to the existing accounts, contacts and insurance policies by their stable
/// deterministic ids; no such record is created here. No files are attached — the demo dataset has no
/// file library to reference.
/// </summary>
public static class ContractGenerator
{
    /// <summary>
    /// The counterparty's reference number on part of the set (issue #181), keyed by contract name so
    /// the seed stays deterministic. Both halves are deliberate: rows WITH a number give the search
    /// and sort paths data in the dev stack and both E2E tiers, and rows WITHOUT one exercise the
    /// nulls-last sort and the healthy absent state. One value is non-Latin on purpose, and two share
    /// a prefix so a substring search has more than one hit to rank.
    /// </summary>
    private static readonly Dictionary<string, string> ReferenceNumbers = new(StringComparer.Ordinal)
    {
        ["Apartment Lease"] = "AGR-2025/114-B.2",
        ["Harbor Point Parking — Space 14"] = "HP-S14/25",
        ["Northwind Energy — Fixed Tariff"] = "NWE-Ω-2026.№114",
        ["Home Insurance — Policy Agreement"] = "PHI-BC-2026-0098812",
        ["Cloud Storage — Business Plan"] = "CUST-5508217",
        ["Maple St Residence — Purchase"] = "Title WA-2021-118804",
        ["Car Loan (Volvo XC60) — 60 Month"] = "CAL 7730 1142 09",
    };

    private enum PartyKind { Account, Contact }

    /// <summary>
    /// Where a seeded contract sits in the signature lifecycle (issue #145). Every pre-#145 spec is
    /// <see cref="Signed"/> so its documented derived status is unchanged — the signature layer sits
    /// ABOVE the whole date chain, so an unsigned contract would read Draft/Ready whatever its dates
    /// say and every status this set was built to demonstrate would collapse into two.
    /// </summary>
    private enum SignatureState
    {
        /// <summary>Marked ready and signed — the date chain decides the status.</summary>
        Signed,

        /// <summary>Neither stamp — derives as <c>Draft</c>.</summary>
        Draft,

        /// <summary>A ready stamp and no signature — derives as <c>Ready</c>.</summary>
        Ready,
    }

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
        int? PausedMonths = null,
        /// <summary>Where this contract sits in the signature lifecycle (issue #145).</summary>
        SignatureState Signature = SignatureState.Signed);

    /// <summary>
    /// One entry in a contract's event log (issue #138).
    /// <paramref name="OccurredMonths"/> is an offset from the anchor and is always negative: an
    /// event records what has HAPPENED, and the server refuses a future one outright.
    /// <paramref name="AuthorRole"/> null seeds an event whose author has since been deleted, which
    /// is the <c>SET NULL</c> state the read path renders as "Unknown user".
    /// </summary>
    private sealed record ContractEventSpec(
        string ContractName,
        ContractEventType Type,
        string Title,
        string? Description,
        string? Notes,
        int OccurredMonths,
        string? AuthorRole = "Owner",
        ContractEventSource Source = ContractEventSource.User);

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
        string? Note = null,
        TermDirection Direction = TermDirection.Outgoing);

    public static Guid IdFor(string name) => DeterministicGuid.From($"contract::{name}");

    /// <summary>
    /// The id of one seeded contract term. The label is part of the key because it is part of the
    /// series key — two fees on one contract can share a kind and a date.
    /// </summary>
    public static Guid TermIdFor(string contractName, string label, DateTime effectiveFrom) =>
        DeterministicGuid.From($"contract-term::{contractName}::{TermLabel.Key(label) ?? ""}@{effectiveFrom:yyyy-MM-dd}");

    private static Guid PartyIdFor(string contractName, int index) =>
        DeterministicGuid.From($"contract-party::{contractName}#{index}");

    /// <summary>
    /// The id of one seeded event. The title is part of the key because nothing else distinguishes two
    /// entries — an event has no natural key and the same type may occur many times on one contract.
    /// </summary>
    public static Guid EventIdFor(string contractName, string title) =>
        DeterministicGuid.From($"contract-event::{contractName}::{title}");

    public static (List<Contract> Contracts, List<ContractParty> Parties, List<Term> Terms, List<ContractEvent> Events) Build(DateTime anchor)
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
                    new(PartyKind.Contact, Catalog.Contacts.Landlord, ContractPartyRole.Landlord),
                ]),

            // Service — starts in the future → Upcoming. Party: the utility provider (contact).
            new(
                "Utilities Supply Contract", ContractType.Service,
                "Combined power and water supply agreement (starts next quarter).",
                anchor.AddMonths(2), anchor.AddYears(1).AddMonths(2), false,
                [
                    // Seller, not the retired ServiceProvider: its documented meaning — the party
                    // disposing under this agreement — already covers supplying a service, which is
                    // why the migration moved every such row here.
                    new(PartyKind.Contact, Catalog.Contacts.CityPowerWater, ContractPartyRole.Seller),
                ]),

            // Other — links the household's insurer to the mortgaged property account. Active
            // (started a year ago, open-ended). Exercises a two-party contract across both kinds.
            new(
                "Mortgage Insurance Mandate", ContractType.Other,
                "Standing mandate tying the home insurance cover to the mortgage account.",
                anchor.AddYears(-1), null, false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.Globex, ContractPartyRole.Buyer),
                    // A deliberate Other — the account is a party to the mandate but plays neither
                    // side of it. Also the type's one SUGGESTED role: a contract filed as "none of the
                    // above" has no domain vocabulary to offer.
                    new(PartyKind.Account, Catalog.Accounts.HomeMortgage, ContractPartyRole.Other),
                ]),

            // Rental, ending inside the 45-day window → the header signal's "Ending soon" group, and
            // the run rate's largest recurring line.
            new(
                "Harbor Point Parking — Space 14", ContractType.Rental,
                "Twelve-month parking licence on space 14. Renews only by a fresh agreement — give notice 30 days before the end date.",
                anchor.AddMonths(-11).AddDays(-18), anchor.AddDays(30), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.CityPowerWater, ContractPartyRole.Landlord),
                ]),

            // Service, starting inside the same window → the mirror group, "Starting soon". Its fee is
            // in force but its contract has not begun, so its first charge lands on the start date.
            new(
                "Northwind Energy — Fixed Tariff", ContractType.Service,
                "Twelve-month fixed electricity tariff. The standing charge and unit rate are fixed for the term.",
                anchor.AddDays(26), anchor.AddYears(1).AddDays(25), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.CityPowerWater, ContractPartyRole.Seller),
                ]),

            // Insurance — the policy held as an agreement in its own right. Active, open-ended.
            new(
                "Home Insurance — Policy Agreement", ContractType.Insurance,
                "The contract of insurance itself, kept alongside the policy record it prices.",
                anchor.AddMonths(-8), null, false,
                [
                    // The only type with four suggested roles, mirroring an insurance policy's four
                    // link collections — and the only place the seed records who insures, who holds
                    // the policy, what is covered and who receives, as four distinct facts.
                    new(PartyKind.Contact, Catalog.Contacts.StateFarm, ContractPartyRole.Insurer),
                    new(PartyKind.Contact, Catalog.Contacts.PolicyHolder, ContractPartyRole.Policyholder),
                    new(PartyKind.Account, Catalog.Accounts.PrimaryResidence, ContractPartyRole.Insured),
                    // The one role that BLOCKS deletion of its contact (issue #157 §7.4). Seeded so
                    // the blocked delete and its detach valve have a real case in the demo data.
                    new(PartyKind.Contact, Catalog.Contacts.Spouse, ContractPartyRole.Beneficiary),
                ]),

            // Subscription — a recurring supply agreement, quarterly rather than monthly so the
            // cadence multiplier (IntervalCount) is exercised by the run rate rather than assumed 1.
            new(
                "Cloud Storage — Business Plan", ContractType.Subscription,
                "Recurring storage and backup plan, billed quarterly.",
                anchor.AddMonths(-14), anchor.AddMonths(10), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.Globex, ContractPartyRole.Seller),
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
                    // The thing bought, in the role that says so (issue #169). Before Property existed
                    // this link had nowhere to go but the catch-all Other, which lost what it meant.
                    new(PartyKind.Account, Catalog.Accounts.PrimaryResidence, ContractPartyRole.Property),
                ]),

            // Membership — annual, and priced in a non-base currency so the run rate's conversion (and
            // its unconverted-currency reporting, if the rate is ever missing) has a real case.
            new(
                "FitZone — Membership", ContractType.Membership,
                "Annual gym membership at the new branch.",
                anchor.AddMonths(-3), anchor.AddMonths(9), false,
                [
                    // Membership is expressed as Buyer/Seller rather than a Member role — see the
                    // roles considered and dropped in issue #157 §4.5.
                    new(PartyKind.Contact, Catalog.Contacts.Globex, ContractPartyRole.Seller),
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
                    new(PartyKind.Contact, Catalog.Contacts.Globex, ContractPartyRole.Seller),
                ],
                PausedMonths: -1),

            // DRAFT (issue #145) — recorded while it is still being negotiated. Note the start date is
            // in the FUTURE and the status is still Draft, not Upcoming: the dates describe a term
            // nobody has agreed to, and reporting Upcoming would assert a commitment that does not
            // exist. It carries a fully priced monthly fee (below), which is what makes the money gate
            // legible in the demo rather than merely asserted — the price shows in the term history
            // and in nothing else: not the run rate, not the by-type cost split, not the next charges.
            new(
                "Beacon Home Services — Cleaning", ContractType.Service,
                "Fortnightly whole-house clean. Quote received; terms still under discussion — nothing has been marked ready for signature yet.",
                anchor.AddMonths(2), anchor.AddYears(1).AddMonths(2), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.CityPowerWater, ContractPartyRole.Seller),
                ],
                Signature: SignatureState.Draft),

            // READY (issue #145) — marked ready for signature and never signed, with a term that has
            // since run out. It reads Ready, NOT Expired: a term cannot lapse before it begins, and
            // the thing to act on is an abandoned negotiation rather than a retired agreement. It is
            // archivable under the widened rule despite having an end date in the past, and it is the
            // row the header's "Awaiting signature" group is built to surface.
            new(
                "Westbrook Tutoring — Weekly Sessions", ContractType.Service,
                "Weekly maths tuition over the school year. Sent for signature and never returned — the term it describes has since run out.",
                anchor.AddMonths(-12), anchor.AddMonths(-3), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.Globex, ContractPartyRole.Seller),
                ],
                Signature: SignatureState.Ready),

            // LOAN (issue #157) — the new contract type, and the reason it exists: before this member
            // a car loan was filed as a Purchase with a Buyer and a Seller. Active, fixed term. Its
            // three parties cover both tiers of the Loan column: the two suggested roles plus the
            // allowed-but-not-suggested Guarantor.
            new(
                "Car Loan (Volvo XC60) — 60 Month", ContractType.Loan,
                "Fixed-rate 60-month loan against the family car. Monthly repayment by direct debit; early settlement permitted without penalty.",
                anchor.AddYears(-2), anchor.AddYears(3), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.FirstNationalBank, ContractPartyRole.Lender),
                    new(PartyKind.Account, Catalog.Accounts.CarLoanVolvo, ContractPartyRole.Borrower),
                    new(PartyKind.Contact, Catalog.Contacts.PolicyHolder, ContractPartyRole.Guarantor),
                    // The security pledged against the loan — Norwegian pant (issue #169). The car is
                    // the asset; CarLoanVolvo above is the liability that finances it, which is why
                    // the two are different accounts in different roles on one contract.
                    new(PartyKind.Account, Catalog.Accounts.FamilyCar, ContractPartyRole.Collateral),
                ]),

            // DEPOSIT (issue #187) — the mirror of the loan above: money the household PLACES with a
            // bank rather than borrows from one. Before this type existed it was filed as Other, or as
            // a Loan with its roles read backwards. Active, fixed term. Both suggested roles are
            // seeded — the account the money came from and the bank holding it — and its monthly
            // interest term (below) is INCOMING, so the roll-up's incoming split has a Deposit row.
            new(
                "Fixed-term Deposit — 12 Months", ContractType.Deposit,
                "Twelve-month fixed-rate deposit of USD 25,000 at 4.2% p.a. Interest is credited monthly; the principal is returned at maturity.",
                anchor.AddMonths(-4), anchor.AddMonths(8), false,
                [
                    new(PartyKind.Account, Catalog.Accounts.HighYieldSavings, ContractPartyRole.Depositor),
                    new(PartyKind.Contact, Catalog.Contacts.FirstNationalBank, ContractPartyRole.Custodian),
                ]),

            // Archived — an expired prior service contract, retained for reference (hidden by default).
            new(
                "Previous Broadband Contract", ContractType.Service,
                "Superseded broadband contract, kept for records.",
                anchor.AddYears(-3), anchor.AddYears(-1), true,
                [
                    // A closed term: this provider left the role a year before the contract expired.
                    new(PartyKind.Contact, Catalog.Contacts.CityPowerWater, ContractPartyRole.Seller,
                        FromDate: 0, ToDate: 12),
                ]),
        };

        var contracts = new List<Contract>();
        var parties = new List<ContractParty>();
        var createdAt = anchor.AddYears(-2);

        foreach (var spec in specs)
        {
            var contractId = IdFor(spec.Name);
            var (ready, signed) = SignatureStamps(spec, createdAt);
            contracts.Add(new Contract
            {
                ContractId = contractId,
                Name = spec.Name,
                Type = spec.Type,
                Description = spec.Description,
                ReferenceNumber = ReferenceNumbers.GetValueOrDefault(spec.Name),
                StartDate = spec.StartDate,
                EndDate = spec.EndDate,
                Archived = spec.Archived ? createdAt.AddYears(1) : null,
                // Derived from the anchor, never from the wall clock, so the seed stays deterministic
                // and re-running it stays idempotent.
                Paused = spec.PausedMonths is { } months ? anchor.AddMonths(months) : null,
                Ready = ready,
                Signed = signed,
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

        return (contracts, parties, BuildTerms(anchor, contracts), BuildEvents(anchor, contracts));
    }

    /// <summary>
    /// The two signature stamps for one spec (issue #145), derived from dates the spec already has so
    /// the seed stays deterministic and never reads the wall clock.
    ///
    /// <para>
    /// The signed moment is <c>min(StartDate, CreatedAtUtc)</c> — the same shape as the migration's
    /// <c>LEAST(COALESCE(…), CreatedAtUtc)</c> clamp, and for the same reason: a contract that starts
    /// in the future must not be stamped as signed in the future, which the server refuses outright
    /// (<c>contract_signature_date_in_future</c>) and which would leave a seeded row no later write
    /// could save. Ready sits a week earlier, so <c>Signed &gt;= Ready</c> holds by construction.
    /// </para>
    /// </summary>
    private static (DateTime? Ready, DateTime? Signed) SignatureStamps(ContractSpec spec, DateTime createdAt)
    {
        var signedAt = spec.StartDate <= createdAt ? spec.StartDate : createdAt;
        return spec.Signature switch
        {
            SignatureState.Draft => (null, null),
            SignatureState.Ready => (signedAt.AddDays(-7), null),
            _ => (signedAt.AddDays(-7), signedAt),
        };
    }

    /// <summary>
    /// The contracts' fee terms — what the roll-up's run rate sums and what its "next charges" project
    /// forward. Every one is in <see cref="TermValueUnit.Amount"/>, since a
    /// percentage term carries no due figure.
    ///
    /// <para>
    /// The set covers the three things the projection has to get right: a periodic fee with a
    /// multiplier (quarterly = Monthly × 3), a fee whose <c>AnchorDate</c> differs from its
    /// <c>EffectiveFrom</c>, and a supersession — the storage plan's price changed, so only the later
    /// entry is in force and the run rate must not count both. Every one of them is
    /// <see cref="TermDirection.Outgoing"/> bar the salary, which is the point: the incoming side is
    /// the exception a file records, not the rule.
    /// </para>
    /// </summary>
    private static List<Term> BuildTerms(DateTime anchor, List<Contract> contracts)
    {
        var specs = new List<ContractTermSpec>
        {
            // The employment contract's two sides (issue #159), which is the whole reason direction
            // exists: a salary that ARRIVES and a union fee that LEAVES, on one agreement, neither
            // cancelling the other. Without an incoming term seeded, every figure the incoming half of
            // the roll-up produces would be null in the demo and the split would read as dead weight.
            new("Employment Agreement — Globex", "Base salary", 6200.00m, Currencies.Usd,
                Interval.Monthly, 1, 0, AnchorDays: 24, Note: "Paid on the 25th.",
                Direction: TermDirection.Incoming),
            // Deducted on the 1st — a DIFFERENT day from the salary, so the per-(contract, direction)
            // collapse of the next movements has something to show: one row in each list for one
            // contract, where collapsing on the contract alone would discard whichever fell later.
            new("Employment Agreement — Globex", "Union membership", 55.00m, Currencies.Usd,
                Interval.Monthly, 1, 0, AnchorDays: 0, Note: "Deducted from the monthly pay."),

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

            // The DRAFT's fee (issue #145) — fully priced, in force by its own effective date, on a
            // contract that is neither archived nor paused. The ONLY reason it is absent from the run
            // rate, the by-type cost split and the next charges is that nobody has signed the
            // agreement: a price nobody agreed to is a quote. Seeding a draft with no price on file
            // would leave that exclusion invisible, exactly as it would for the paused one above.
            new("Beacon Home Services — Cleaning", "Cleaning", 180.00m, Currencies.Usd,
                Interval.Monthly, 1, 0, Note: "Quoted rate — not agreed until the contract is signed."),

            // The deposit's interest (issue #187) — 25,000 at 4.2% p.a. is 87.50 a month, and it ARRIVES.
            // The principal is deliberately not a term: it is placed once and returned once, which is
            // not a rate the roll-up can project.
            new("Fixed-term Deposit — 12 Months", "Interest", 87.50m, Currencies.Usd,
                Interval.Monthly, 1, 0, AnchorDays: 27, Note: "Credited at month end.",
                Direction: TermDirection.Incoming),

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
                Label = spec.Label,
                LabelKey = TermLabel.Key(spec.Label),
                ValueUnit = TermValueUnit.Amount,
                Direction = spec.Direction,
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

    /// <summary>
    /// The contracts' event logs (issue #138) — what has HAPPENED to each agreement, as opposed to
    /// what it is. Three contracts carry one, so the demo shows the rail populated, the empty state on
    /// the others, and the year markers that only appear once a log spans one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The set covers the states the surface has to draw differently: an entry with all three
    /// free-text fields and one with a title alone (both are complete events — §8.2); an entry from a
    /// deleted author, which reads "Unknown user" rather than a blank or an id; and a
    /// <see cref="ContractEventType.Terminated"/> event on a contract that stays Active, which is
    /// §4.2's rule made visible — an event never moves the derived status.
    /// </para>
    /// <para>
    /// Every occurrence is in the past relative to the anchor, never to the wall clock, so the seed
    /// stays deterministic and re-running it stays idempotent — and so no seeded row could ever be one
    /// the API itself would refuse.
    /// </para>
    /// </remarks>
    private static List<ContractEvent> BuildEvents(DateTime anchor, List<Contract> contracts)
    {
        var specs = new List<ContractEventSpec>
        {
            // The lease: the fullest log, spanning two calendar years so the rail draws a year marker.
            new("Apartment Lease", ContractEventType.Signed,
                "Tenancy agreement signed",
                "Both counterparts signed at the letting office and the deposit was protected the same day.",
                null, -6),
            new("Apartment Lease", ContractEventType.EmailSent,
                "Emailed the landlord about the damp",
                "Photos of the back bedroom attached. Asked for a contractor visit inside two weeks.",
                "Keep the photos — they are dated.", -4),
            // An entry whose author has since left. SET NULL keeps the record and drops the name.
            new("Apartment Lease", ContractEventType.Amended,
                "Pets permitted by amendment",
                "One cat. An extra deposit was agreed.",
                null, -3, AuthorRole: null),
            new("Apartment Lease", ContractEventType.PriceChanged,
                "Rent renegotiated",
                "Agreed by phone with the letting agent, then confirmed in writing.",
                "Check last year's letter before the next review.", -2),
            // A title alone — an ordinary, complete event (§8.2), and the case the rail must not
            // render an empty second line for.
            new("Apartment Lease", ContractEventType.NoticeGiven,
                "Notice to leave served", null, null, -1),

            // The employment contract: a short log by a different author, so the demo shows two names
            // on one page of the same feature.
            new("Employment Agreement — Globex", ContractEventType.Signed,
                "Contract of employment signed", "Counter-signed by HR on the same day.", null, -24,
                AuthorRole: "Admin"),
            new("Employment Agreement — Globex", ContractEventType.Amended,
                "Salary review applied",
                "Annual review; the new figure takes effect from the next pay run.",
                "Ask about the pension match at the next review.", -12, AuthorRole: "Admin"),

            // §4.2 made visible: a Terminated event on a contract whose derived status stays Active.
            // A reader who expects the event to expire the contract is meant to meet this and see that
            // it does not — a free-form log entry is never authoritative over derived state.
            new("Mortgage Insurance Mandate", ContractEventType.Terminated,
                "Told the broker we intend to end the mandate",
                "A note of the conversation only — the mandate itself still runs until its own dates say otherwise.",
                null, -1),

            // ── System-recorded lines (issue #154) ──────────────────────────────────────
            //
            // Seeded so the frontend has BOTH kinds of row to render — the provenance affordance and
            // the source filter are otherwise unexercised until someone clicks through the app first.
            // The text is the catalogue's own wording, and carries no contact or account name (§7.3);
            // Notes is null, as it always is on a system event.
            //
            // Seeded as data rather than produced by replaying transitions through ContractService:
            // the seeder writes rows directly, and a demo database must stay deterministic and
            // idempotent, which a real write path with a wall clock would not be.
            new("Apartment Lease", ContractEventType.Ready,
                "Marked ready for signature", null, null, -6,
                Source: ContractEventSource.System),
            new("Apartment Lease", ContractEventType.PartyAdded,
                "Landlord added as a party", null, null, -6,
                Source: ContractEventSource.System),
            new("Employment Agreement — Globex", ContractEventType.PartyAdded,
                "Employer added as a party", null, null, -24,
                AuthorRole: "Admin", Source: ContractEventSource.System),
            // A system row whose author has since been deleted: the line survives and reads
            // "Unknown user", which is the SET NULL rule on a row nobody typed.
            new("Mortgage Insurance Mandate", ContractEventType.Paused,
                "Contract paused", null, null, -2,
                AuthorRole: null, Source: ContractEventSource.System),
            new("Mortgage Insurance Mandate", ContractEventType.Unpaused,
                "Contract resumed", null, null, -1,
                Source: ContractEventSource.System),
        };

        var byName = contracts.ToDictionary(contract => contract.Name, contract => contract.ContractId, StringComparer.Ordinal);
        var events = new List<ContractEvent>();

        foreach (var spec in specs)
        {
            if (!byName.TryGetValue(spec.ContractName, out var contractId))
            {
                continue;
            }

            var occurredAt = anchor.AddMonths(spec.OccurredMonths);
            // A system event's description is generated prose that names its own moment, so a seeded
            // one has to be written from the same offset rather than hardcoded — the anchor moves.
            var description = spec.Description ?? SeededSystemDescription(spec.Type, occurredAt);
            events.Add(new ContractEvent
            {
                ContractEventId = EventIdFor(spec.ContractName, spec.Title),
                ContractId = contractId,
                Type = spec.Type,
                Title = spec.Title,
                Description = description,
                Notes = spec.Notes,
                OccurredAt = occurredAt,
                Source = spec.Source,
                CreatedByUserId = spec.AuthorRole is { } role ? UserId(role) : null,
                // Recorded shortly after it happened, which is what a log looks like — and never the
                // same instant, so the two dates on the row are visibly different facts.
                CreatedAtUtc = occurredAt.AddHours(3),
            });
        }

        return events;
    }

    /// <summary>
    /// The description a seeded <b>stamp</b> system event carries, phrased as
    /// <c>ContractEventCatalogue</c> phrases it and dated from the same anchor offset the row occurs
    /// at. Party events carry none at all, matching the catalogue.
    /// </summary>
    /// <remarks>
    /// <b>This is demo prose, not a second implementation of the rule.</b> The catalogue in
    /// <c>Odyssey.Core</c> is the authority and nothing reads this at runtime; it is restated here
    /// because <c>Odyssey.TestData</c> deliberately references <c>Odyssey.Context</c> alone — adding
    /// <c>Odyssey.Core</c> would drag EF, Mapster and the HTTP stack into <c>Odyssey.E2ETests</c>,
    /// which references this project and nothing else. A drift between the two is cosmetic in a demo
    /// database, which is what makes the trade acceptable here and nowhere on a write path.
    /// </remarks>
    private static string? SeededSystemDescription(ContractEventType type, DateTime occurredAt)
    {
        var date = occurredAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        return type switch
        {
            ContractEventType.Paused => $"Suspended on {date}.",
            ContractEventType.Unpaused => $"Resumed on {date}.",
            ContractEventType.Ready => $"Ready for signature as of {date}.",
            ContractEventType.Unready => $"Withdrawn on {date}.",
            ContractEventType.Unsigned => $"Cleared on {date}.",
            ContractEventType.Archived => $"Archived on {date}.",
            ContractEventType.Unarchived => $"Restored on {date}.",
            _ => null,
        };
    }

    private static string UserId(string role) => DemoUsers.All.First(user => user.Role == role).Id;
}
