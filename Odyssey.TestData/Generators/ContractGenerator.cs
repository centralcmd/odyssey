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
/// Parties link to the existing accounts, contacts and insurance policies by their stable
/// deterministic ids; no such record is created here. No files are attached — the demo dataset has no
/// file library to reference.
/// </summary>
public static class ContractGenerator
{
    private enum PartyKind { Account, Contact, InsurancePolicy }

    private sealed record PartySpec(PartyKind Kind, string TargetName);

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
                    new(PartyKind.Contact, Catalog.Contacts.Globex),
                    new(PartyKind.Account, Catalog.Accounts.EverydayChecking),
                ]),

            // Rental — fixed term spanning the anchor → Active. Party: the landlord (contact).
            new(
                "Apartment Lease", ContractType.Rental,
                "12-month residential tenancy agreement.",
                anchor.AddMonths(-6), anchor.AddMonths(6), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.Landlord),
                ]),

            // Service — starts in the future → Upcoming. Party: the utility provider (contact).
            new(
                "Utilities Supply Contract", ContractType.Service,
                "Combined power and water supply agreement (starts next quarter).",
                anchor.AddMonths(2), anchor.AddYears(1).AddMonths(2), false,
                [
                    new(PartyKind.Contact, Catalog.Contacts.CityPowerWater),
                ]),

            // Other — links the household to an insurance policy and the mortgaged property account.
            // Active (started a year ago, open-ended). Exercises the InsurancePolicy party kind.
            new(
                "Mortgage Insurance Mandate", ContractType.Other,
                "Standing mandate tying the home insurance policy to the mortgage account.",
                anchor.AddYears(-1), null, false,
                [
                    new(PartyKind.InsurancePolicy, "Home Insurance"),
                    new(PartyKind.Account, Catalog.Accounts.HomeMortgage),
                ]),

            // Archived — an expired prior service contract, retained for reference (hidden by default).
            new(
                "Previous Broadband Contract", ContractType.Service,
                "Superseded broadband contract, kept for records.",
                anchor.AddYears(-3), anchor.AddYears(-1), true,
                [
                    new(PartyKind.Contact, Catalog.Contacts.CityPowerWater),
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
                    InsurancePolicyId = party.Kind == PartyKind.InsurancePolicy
                        ? InsurancePolicyGenerator.IdFor(party.TargetName) : null,
                });
            }
        }

        return (contracts, parties);
    }
}
