using Odyssey.Context;
using Odyssey.TestData.Catalog;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic contracts and their parties (issue #174). Anchored to
/// <see cref="DemoDataDefaults.AnchorDate"/> so each contract's derived status is stable, and the set
/// deliberately exercises every <see cref="ContractType"/> (Employment, Service, Rental, Other), every
/// derived status (Active, Upcoming, Expired, Archived) and all three party kinds (an account, a
/// contact/"institution" and an insurance policy).
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
        IReadOnlyList<PartySpec> Parties);

    public static Guid IdFor(string name) => DeterministicGuid.From($"contract::{name}");

    private static Guid PartyIdFor(string contractName, int index) =>
        DeterministicGuid.From($"contract-party::{contractName}#{index}");

    public static (List<Contract> Contracts, List<ContractParty> Parties) Build(DateTime anchor)
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

        return (contracts, parties);
    }
}
