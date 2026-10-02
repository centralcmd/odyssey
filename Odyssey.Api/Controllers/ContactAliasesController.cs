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
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Avatar;

namespace Odyssey.Api.Controllers;

/// <summary>
/// A contact's alternative names. Split out of <c>ContactsController</c> along the Property precedent, one controller per
/// sub-resource under the same route prefix; the routes and route names are unchanged (issue #287 M2).
/// </summary>
[ApiController]
[Route("api/contacts")]
public sealed class ContactAliasesController : ControllerBase
{
    private readonly ContactService contactService;
    private readonly ContactAuditLog audit;

    public ContactAliasesController(
        ContactService contactService,
        ContactAuditLog audit)
    {
        this.contactService = contactService;
        this.audit = audit;
    }

    // ── Aliases (issue #48 §7) ────────────────────────────────────────────────
    // Every child collection below (aliases, addresses, emails, phones) is read under contacts.read
    // and written — add, replace AND remove — under contacts.update, like every other sub-resource:
    // editing a contact's phone number is an update to the contact, not the creation or deletion of
    // one (issue #287 M3). No new claim, so no RolePermissions change and no forced sign-out/sign-in.
    //
    // Containment holds on all four verbs: the service resolves an aliasId SCOPED to contactId, so an
    // alias belonging to another contact is a 404 — not a 403, which would confirm the row exists
    // under a different parent (ASVS V4.1.5, WSTG-ATHZ-04).

    [HttpGet("{contactId}/aliases", Name = "GetContactAliases")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingContactAlias>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List a contact's alternative names.")]
    public async Task<IActionResult> GetAliases([FromRoute] Guid contactId, CancellationToken cancellationToken = default)
    {
        var aliases = await contactService.GetAliases(contactId, cancellationToken);
        return aliases is null ? this.NotFoundProblem($"Contact ID {contactId} not found.") : Ok(aliases);
    }

    [HttpPost("{contactId}/aliases", Name = "PostContactAlias")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingContactAlias))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Add one alternative name to a contact.",
        Description = @"409 when the contact already carries that value (compared case- AND
accent-insensitively, on the value alone — two labels cannot smuggle in a second ""Hansen""); 422 when
the contact is already at its 32-alias cap. Both name the field `value` in the problem-details
`errors` dictionary, and neither echoes the submitted value or label.")]
    public async Task<IActionResult> PostAlias(
        [FromRoute] Guid contactId, [FromBody] NewContactAlias request, CancellationToken cancellationToken = default)
    {
        var created = await contactService.CreateAlias(contactId, request, cancellationToken);
        if (created is null)
        {
            return this.NotFoundProblem($"Contact ID {contactId} not found.");
        }

        audit.ContactChanged(User, contactId, "alias.created");
        // CreatedAtRoute points at the collection: the siblings expose no per-item GET either.
        return CreatedAtRoute("GetContactAliases", new { contactId }, created);
    }

    [HttpPut("{contactId}/aliases/{aliasId}", Name = "PutContactAlias")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace one of a contact's alternative names.",
        Description = @"A replace, not a patch: an omitted or blank `label` CLEARS a previously-set
one, matching the sibling sub-resources. There is deliberately no 422 here — a replace cannot grow the
collection, so the cap is unreachable on this verb.")]
    public async Task<IActionResult> PutAlias(
        [FromRoute] Guid contactId, [FromRoute] Guid aliasId, [FromBody] NewContactAlias request,
        CancellationToken cancellationToken = default)
    {
        var updated = await contactService.UpdateAlias(contactId, aliasId, request, cancellationToken);
        if (!updated)
        {
            return this.NotFoundProblem($"Alias ID {aliasId} is not attached to contact ID {contactId}.");
        }

        audit.ContactChanged(User, contactId, "alias.updated");
        return NoContent();
    }

    [HttpDelete("{contactId}/aliases/{aliasId}", Name = "DeleteContactAlias")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> DeleteAlias(
        [FromRoute] Guid contactId, [FromRoute] Guid aliasId, CancellationToken cancellationToken = default)
    {
        var deleted = await contactService.DeleteAlias(contactId, aliasId, cancellationToken);
        if (!deleted)
        {
            return this.NotFoundProblem($"Alias ID {aliasId} is not attached to contact ID {contactId}.");
        }

        audit.ContactChanged(User, contactId, "alias.deleted");
        return NoContent();
    }
}
