using System.Globalization;
using System.Security.Claims;
using System.Text;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Swashbuckle.AspNetCore.Annotations;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Avatar;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/contacts")]
public sealed class ContactsController : ControllerBase
{
    private readonly ILogger<ContactsController> logger;
    private readonly ContactService contactService;
    private readonly IContactReferenceGuard referenceGuard;
    private readonly ContactAuditLog audit;

    public ContactsController(
        ILogger<ContactsController> logger,
        ContactService contactService,
        IContactReferenceGuard referenceGuard,
        ContactAuditLog audit)
    {
        this.logger = logger;
        this.contactService = contactService;
        this.referenceGuard = referenceGuard;
        this.audit = audit;
    }

    [HttpGet(Name = "GetContacts")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingContact>))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List contacts.", Description = @"List contacts with search, filtering, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] ContactsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await contactService.ListAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id}", Name = "GetContact")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingContact))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the contact to get.")] Guid id, CancellationToken cancellationToken = default)
    {
        var contact = await contactService.Get(id, cancellationToken);
        if (contact is null)
        {
            return this.NotFoundProblem($"Contact ID {id} not found.");
        }

        return Ok(contact);
    }

    [HttpPost(Name = "PostContact")]
    [Authorize(Policy = PermissionClaims.ContactsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Post(
        [FromBody] [SwaggerParameter("NewContact", Required = true,
            Description = @"The new contact to create.")] NewContact newContact, CancellationToken cancellationToken = default)
    {
        var contact = await contactService.Create(newContact, cancellationToken);
        if (contact.PersonDetails?.DateOfDeath is not null)
        {
            audit.ContactChanged(User, contact.ContactId, "dateOfDeath.set");
        }

        return CreatedAtRoute("GetContact", new { id = contact.ContactId }, "");
    }

    [HttpPut("{id}", Name = "PutContact")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update the details for a contact.",
        Description = @"Update the details for a contact. Not an upsert: an unknown ID is a 404 and
                        nothing is created. Use POST to create. A changed Type converts the contact
                        with the supplied details; 409 (code contact-type-change-blocked) while the
                        contact is tagged as a person on a photo (to Organization) or is a property's
                        homeowner association (to Person). 422 when a contact-method label is out of
                        scope for the type.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the contact to update.")] Guid id,
        [FromBody] [SwaggerParameter("NewContact", Required = true,
            Description = @"The contact with the updated values.")] NewContact newContact, CancellationToken cancellationToken = default)
    {
        // Read BEFORE the write: the audit event distinguishes recording a death from clearing one,
        // and only the prior value can say which happened (§10.9). Value-free either way — the event
        // names the transition, never the date.
        var beforeContact = await contactService.Get(id, cancellationToken);
        var before = beforeContact?.PersonDetails?.DateOfDeath;

        ExistingContact? contact;
        try
        {
            contact = await contactService.Update(id, newContact, cancellationToken);
        }
        catch (ContactTypeChangeBlockedException blocked)
        {
            return TypeChangeBlocked(id, blocked);
        }

        if (contact is null)
        {
            return this.NotFoundProblem($"Contact ID {id} not found.");
        }

        var after = contact.PersonDetails?.DateOfDeath;
        if (before != after)
        {
            audit.ContactChanged(User, id, after is null ? "dateOfDeath.cleared" : "dateOfDeath.set");
        }

        if (beforeContact is not null && beforeContact.Type != contact.Type)
        {
            audit.ContactChanged(User, id, "type.converted");
        }

        return NoContent();
    }

    [HttpGet("{id}/conversion-preview", Name = "GetContactConversionPreview")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ContactConversionPreview))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Preview converting a contact between Person and Organization.",
        Description = @"A read-only dry run (issue #335): the derived target details, the fields that
would be lost or carried over, the contact-method labels that would clamp to Other, and any blockers.
Writes nothing. Echo updatedAt as expectedUpdatedAt on the conversion. Needs contacts.read and
contacts.update; blocker photo ids need photos.read and property names properties.read.")]
    public async Task<IActionResult> PreviewConversion(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery] ContactConversionPreviewQuery query,
        CancellationToken cancellationToken = default)
    {
        var preview = await contactService.PreviewConversionAsync(
            id, query.TargetType!.Value, query.CarryLifecycleDates, cancellationToken);
        if (preview is null)
        {
            return this.NotFoundProblem($"Contact ID {id} not found.");
        }

        preview.Blockers = VisibleBlockers(preview.Blockers);
        return Ok(preview);
    }

    [HttpPost("{id}/conversion", Name = "PostContactConversion")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingContact))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Convert a contact between Person and Organization.",
        Description = @"Converts the contact in one transaction, deriving the target details from the
stored row (issue #335); its id and its links are kept. 409 with code contact-conversion-stale when
expectedUpdatedAt no longer matches, or contact-type-change-blocked while a photo person tag (to
Organization) or a homeowner-association link (to Person) exists. 422 for the current type, or for a
derived name that needs an override.")]
    public async Task<IActionResult> Convert(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] ContactConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ContactConversionResult? result;
        try
        {
            result = await contactService.ConvertAsync(id, request, cancellationToken);
        }
        catch (ContactTypeChangeBlockedException blocked)
        {
            return TypeChangeBlocked(id, blocked);
        }

        if (result is null)
        {
            return this.NotFoundProblem($"Contact ID {id} not found.");
        }

        audit.ContactChanged(User, id, "type.converted");
        return Ok(result.Contact);
    }

    /// <summary>
    /// The blocked type change's <c>409</c>, shaped from the record the refusal was decided on — the same
    /// snapshot, never a pre-flight (issue #335 §3.1). The counts are unconditional; the ids follow the
    /// read claim of the records they name, as the contact-delete <c>409</c> does for contract names.
    /// </summary>
    private ObjectResult TypeChangeBlocked(Guid id, ContactTypeChangeBlockedException blocked)
    {
        var visible = VisibleBlockers(blocked.Blockers);
        audit.ContactTypeChangeBlocked(User, id, visible.PhotoPersonTags.Count, visible.HomeownerAssociations.Count);

        return this.ConflictProblem(blocked.Message, new Dictionary<string, object?>
        {
            // ConflictProblem adds no code of its own; set here so this matches the handler's fallback.
            ["code"] = blocked.Code,
            ["photoPersonTags"] = visible.PhotoPersonTags,
            ["homeownerAssociations"] = visible.HomeownerAssociations,
        });
    }

    private ContactTypeChangeBlockers VisibleBlockers(ContactTypeChangeBlockers blockers) => new()
    {
        PhotoPersonTags = User.HasPermission(PermissionClaims.PhotosRead)
            ? blockers.PhotoPersonTags
            : blockers.PhotoPersonTags with { PhotoIds = [] },
        HomeownerAssociations = User.HasPermission(PermissionClaims.PropertiesRead)
            ? blockers.HomeownerAssociations
            : blockers.HomeownerAssociations with { Properties = [] },
    };

    [HttpDelete("{id}", Name = "DeleteContact")]
    [Authorize(Policy = PermissionClaims.ContactsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DetachedContactLinks))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a contact.",
        Description = @"409 when the contact is named as a Beneficiary party on any contract. With
detachBlockingLinks=true those link rows are removed and the contact deleted in ONE transaction; that
needs contracts.update — when that class is actually present — in addition to contacts.delete, and the
response is then 200 with a summary of what was destroyed.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the contact to delete.")] Guid id,
        [FromQuery] [SwaggerParameter(Description = @"Remove the contact's blocking link rows —
Beneficiary contract parties — and delete it in one transaction, instead of refusing with a 409.
Requires contracts.update when that class is actually present.")] bool detachBlockingLinks = false,
        CancellationToken cancellationToken = default)
    {
        if (detachBlockingLinks)
        {
            // Composed from existing claims rather than a new one — no RolePermissions change, no
            // role-claim reconciliation, no sign-out/sign-in.
            //
            // What the caller MAY destroy travels into the service; which classes are actually PRESENT
            // is determined there, inside the delete's own transaction, and the two are compared
            // against that one snapshot (issue #157 §7.3). A pre-flight here would be a second
            // snapshot, and a link inserted between them would be destroyed by a caller never asked to
            // prove the claim for it. A caller missing a needed claim gets a 403 — never a silent
            // downgrade to the refused delete.
            var permitted = new HashSet<ContactDeleteBlockerClass>();
            if (User.HasPermission(PermissionClaims.ContractsUpdate)) permitted.Add(ContactDeleteBlockerClass.ContractBeneficiary);

            var detached = await contactService.Delete(id, detachBlockingLinks: true, permitted, cancellationToken);
            if (detached is null)
            {
                // The contact did not exist; nothing was detached and nothing was deleted.
                return NoContent();
            }

            // Ids only, never names — the caller asked to erase a contact. This one line exists because
            // the detach path's blast radius (every link across every contract, in one request) is
            // materially larger than an ordinary per-contract edit; it is NOT an audit trail and
            // §10 #12 does not claim it is.
            logger.LogInformation(
                "Detached {ContractLinkCount} contract beneficiary row(s) across {ContractCount} "
                + "contract(s) for contact {ContactId} and deleted the contact.",
                detached.ContractBeneficiaryLinks,
                detached.AffectedContractIds.Count,
                id);

            return Ok(detached);
        }

        // The claim conditional lives HERE, not in the service: DomainConflictException carries a
        // message and nothing else, and the domain service has no ClaimsPrincipal — so neither it nor
        // GlobalExceptionHandler could shape a claim-conditional payload. The service keeps its own
        // unconditional guard as defence-in-depth for non-HTTP callers.
        var blockers = await referenceGuard.GetDeleteBlockersAsync(id, cancellationToken);
        if (blockers.Any)
        {
            // The COUNT is unconditional and still makes the 409 actionable — it says how many links
            // must go, and the detach valve does not require the caller to name them — while the
            // contract NAMES need contracts.read. The boundary costs nothing today (every shipped role
            // holding contacts.delete also holds contracts.read, asserted by a guard test) and is kept
            // for a future role.
            var canReadContracts = User.HasPermission(PermissionClaims.ContractsRead);
            var extensions = new Dictionary<string, object?>
            {
                ["contractBeneficiaries"] = new ContactContractBeneficiaryBlockers
                {
                    TotalLinks = blockers.ContractBeneficiaryLinks,
                    ContractCount = blockers.Contracts.Count,
                    Contracts = canReadContracts ? [.. blockers.Contracts] : [],
                },
            };

            return this.ConflictProblem(
                DescribeDeleteBlockers(blockers)
                + " Retry with detachBlockingLinks=true to remove those links and delete it in one "
                + "transaction, or remove it from those records first.",
                extensions);
        }

        await contactService.Delete(id, detachBlockingLinks: false, null, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The lead sentence of the blocked-delete 409. Class name only — never a contract name, which
    /// the claim-gated payload above owns.
    /// </summary>
    private static string DescribeDeleteBlockers(ContactDeleteBlockers blockers) =>
        "This contact is named as a beneficiary on one or more contracts and cannot be deleted.";
}
