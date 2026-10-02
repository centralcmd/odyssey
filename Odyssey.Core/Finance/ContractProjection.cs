using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using ContextContractType = Odyssey.Context.ContractType;
using ContextContractFileType = Odyssey.Context.ContractFileType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using DtoContractFileType = Odyssey.Dtos.Finance.ContractFileType;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Microsoft.Extensions.Logging;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextTermDirection = Odyssey.Context.TermDirection;
using DtoInterval = Odyssey.Dtos.Finance.Interval;
using DtoContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;

namespace Odyssey.Core.Finance;

/// <summary>
/// The contract party and file read projections, shared by <see cref="ContractService"/> (the detail read
/// embeds both) and by <see cref="ContractPartyService"/> and <see cref="ContractFileService"/> (issue #287 M1).
/// </summary>
internal static class ContractProjection
{
    // Explicit member mapping (never a permissive Adapt) so a future field added to Account, Contact or
    // Property cannot silently re-leak into this cross-claim projection (§9/§10 #2, issue #208 §7.3).
    // Each branch tests its OWN column: a bare trailing else would classify every property party as an
    // Institution (issue #208 §8).
    internal static ExistingContractParty ToPartyDto(ContractParty party, IReadOnlyDictionary<Guid, ContactRef> contacts)
    {
        var dto = new ExistingContractParty
        {
            ContractPartyId = party.ContractPartyId,
            ContractId = party.ContractId,
            // A top-level field on the party, so it survives an unresolved target reference.
            Role = party.Role.Adapt<DtoContractPartyRole>(),
            FromDate = party.FromDate,
            ToDate = party.ToDate,
        };

        if (party.AccountId is not null)
        {
            dto.Kind = ContractPartyKind.Account;
            dto.Account = party.Account is null ? null : new ContractAccountReference
            {
                AccountId = party.Account.AccountId,
                Name = party.Account.Name,
                Type = party.Account.AccountType.Adapt<DtoAccountType>(),
            };
        }
        else if (party.ContactId is { } contactId)
        {
            // Resolve via the batched lookup. An unresolved link nulls the reference, as the read path
            // does for any missing link.
            var contact = contacts.GetValueOrDefault(contactId);
            dto.Kind = ContractPartyKind.Institution;
            dto.Institution = contact is null ? null : new ContractContactReference
            {
                ContactId = contact.ContactId,
                Name = contact.Name,
                // No .Adapt here (unlike Account): ContactRef already declares Type as the Dtos
                // ContactType, so this is a same-type assignment.
                Type = contact.Type,
            };
        }
        else if (party.PropertyId is not null)
        {
            dto.Kind = ContractPartyKind.Property;
            dto.Property = party.Property is null ? null : new ContractPropertyReference
            {
                PropertyId = party.Property.PropertyId,
                Name = party.Property.Name,
                Type = party.Property.Type,
            };
        }
        else
        {
            // Unreachable under CK_ContractParties_ExactlyOneTarget; refusing beats guessing a kind.
            throw new InvalidOperationException(
                $"Contract party {party.ContractPartyId} names no target.");
        }

        return dto;
    }

    internal static ExistingContractFile ToFileDto(ContractFile file) => new()
    {
        ContractFileId = file.ContractFileId,
        ContractId = file.ContractId,
        FileMetadata = file.FileMetadata!.Adapt<ExistingFileMetadata>(),
        FileType = file.FileType.Adapt<DtoContractFileType>(),
        AttachedByUserId = file.AttachedByUserId,
        AttachedAtUtc = file.AttachedAtUtc,
        ValidFrom = file.ValidFrom,
        ValidTo = file.ValidTo,
        IssuedAt = file.IssuedAt,
        IssuedBy = file.IssuedBy,
    };
}
