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
/// A contact's postal addresses. Split out of <c>ContactsController</c> along the Property precedent, one controller per
/// sub-resource under the same route prefix; the routes and route names are unchanged (issue #287 M2).
/// </summary>
[ApiController]
[Route("api/contacts")]
public sealed class ContactAddressesController : ControllerBase
{
    private readonly ContactService contactService;

    public ContactAddressesController(
        ContactService contactService)
    {
        this.contactService = contactService;
    }

    // ── Addresses (issue #325 §7) ─────────────────────────────────────────────

    [HttpGet("{contactId}/addresses", Name = "GetContactAddresses")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingAddress>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> GetAddresses([FromRoute] Guid contactId, CancellationToken cancellationToken = default)
    {
        var addresses = await contactService.GetAddresses(contactId, cancellationToken, User.LocationClaims());
        return addresses is null ? this.NotFoundProblem($"Contact ID {contactId} not found.") : Ok(addresses);
    }

    [HttpPost("{contactId}/addresses", Name = "PostContactAddress")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingAddress))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PostAddress([FromRoute] Guid contactId, [FromBody] NewAddress request, CancellationToken cancellationToken = default)
    {
        // An address is a link to a place (issue #380 §5.3): by id under locations.read, or by fields under
        // locations.create — decided by the location rules against the caller's claims.
        var created = await contactService.CreateAddress(
            contactId, request, cancellationToken, User.LocationClaims(), User.ActingUserId());
        return created is null
            ? this.NotFoundProblem($"Contact ID {contactId} not found.")
            : CreatedAtRoute("GetContactAddresses", new { contactId }, created);
    }

    [HttpPut("{contactId}/addresses/{addressId}", Name = "PutContactAddress")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PutAddress([FromRoute] Guid contactId, [FromRoute] Guid addressId, [FromBody] NewAddress request, CancellationToken cancellationToken = default)
    {
        // Changed fields need applyTo (issue #380 §5.3): ThisRecord moves this link, AllUses edits the shared
        // place in place under locations.update. The acting user is attributed and logged.
        var updated = await contactService.UpdateAddress(
            contactId, addressId, request, cancellationToken, User.LocationClaims(), User.ActingUserId());
        return updated ? NoContent() : this.NotFoundProblem($"Address ID {addressId} is not attached to contact ID {contactId}.");
    }

    [HttpDelete("{contactId}/addresses/{addressId}", Name = "DeleteContactAddress")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> DeleteAddress([FromRoute] Guid contactId, [FromRoute] Guid addressId, CancellationToken cancellationToken = default)
    {
        var deleted = await contactService.DeleteAddress(contactId, addressId, cancellationToken);
        return deleted ? NoContent() : this.NotFoundProblem($"Address ID {addressId} is not attached to contact ID {contactId}.");
    }
}
