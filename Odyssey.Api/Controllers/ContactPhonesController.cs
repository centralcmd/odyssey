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
/// A contact's phone numbers. Split out of <c>ContactController</c> along the Property precedent, one controller per
/// sub-resource under the same route prefix; the routes and route names are unchanged (issue #287 M2).
/// </summary>
[ApiController]
[Route("api/contacts")]
public sealed class ContactPhonesController : ControllerBase
{
    private readonly ContactService contactService;

    public ContactPhonesController(
        ContactService contactService)
    {
        this.contactService = contactService;
    }

    // ── Phone numbers (issue #325 §7) ─────────────────────────────────────────

    [HttpGet("{contactId}/phones", Name = "GetContactPhones")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingPhoneNumber>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> GetPhones([FromRoute] Guid contactId, CancellationToken cancellationToken = default)
    {
        var phones = await contactService.GetPhones(contactId, cancellationToken);
        return phones is null ? this.NotFoundProblem($"Contact ID {contactId} not found.") : Ok(phones);
    }

    [HttpPost("{contactId}/phones", Name = "PostContactPhone")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingPhoneNumber))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PostPhone([FromRoute] Guid contactId, [FromBody] NewPhoneNumber request, CancellationToken cancellationToken = default)
    {
        var created = await contactService.CreatePhone(contactId, request, cancellationToken);
        return created is null
            ? this.NotFoundProblem($"Contact ID {contactId} not found.")
            : CreatedAtRoute("GetContactPhones", new { contactId }, created);
    }

    [HttpPut("{contactId}/phones/{phoneId}", Name = "PutContactPhone")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PutPhone([FromRoute] Guid contactId, [FromRoute] Guid phoneId, [FromBody] NewPhoneNumber request, CancellationToken cancellationToken = default)
    {
        var updated = await contactService.UpdatePhone(contactId, phoneId, request, cancellationToken);
        return updated ? NoContent() : this.NotFoundProblem($"Phone ID {phoneId} is not attached to contact ID {contactId}.");
    }

    [HttpDelete("{contactId}/phones/{phoneId}", Name = "DeleteContactPhone")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> DeletePhone([FromRoute] Guid contactId, [FromRoute] Guid phoneId, CancellationToken cancellationToken = default)
    {
        var deleted = await contactService.DeletePhone(contactId, phoneId, cancellationToken);
        return deleted ? NoContent() : this.NotFoundProblem($"Phone ID {phoneId} is not attached to contact ID {contactId}.");
    }
}
