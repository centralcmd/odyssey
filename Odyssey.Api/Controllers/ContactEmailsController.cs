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
/// A contact's email addresses. Split out of <c>ContactController</c> along the Property precedent, one controller per
/// sub-resource under the same route prefix; the routes and route names are unchanged (issue #287 M2).
/// </summary>
[ApiController]
[Route("api/contacts")]
public sealed class ContactEmailsController : ControllerBase
{
    private readonly ContactService contactService;

    public ContactEmailsController(
        ContactService contactService)
    {
        this.contactService = contactService;
    }

    // ── Emails (issue #325 §7) ────────────────────────────────────────────────

    [HttpGet("{contactId}/emails", Name = "GetContactEmails")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingEmailAddress>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> GetEmails([FromRoute] Guid contactId, CancellationToken cancellationToken = default)
    {
        var emails = await contactService.GetEmails(contactId, cancellationToken);
        return emails is null ? this.NotFoundProblem($"Contact ID {contactId} not found.") : Ok(emails);
    }

    [HttpPost("{contactId}/emails", Name = "PostContactEmail")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingEmailAddress))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PostEmail([FromRoute] Guid contactId, [FromBody] NewEmailAddress request, CancellationToken cancellationToken = default)
    {
        var created = await contactService.CreateEmail(contactId, request, cancellationToken);
        return created is null
            ? this.NotFoundProblem($"Contact ID {contactId} not found.")
            : CreatedAtRoute("GetContactEmails", new { contactId }, created);
    }

    [HttpPut("{contactId}/emails/{emailId}", Name = "PutContactEmail")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PutEmail([FromRoute] Guid contactId, [FromRoute] Guid emailId, [FromBody] NewEmailAddress request, CancellationToken cancellationToken = default)
    {
        var updated = await contactService.UpdateEmail(contactId, emailId, request, cancellationToken);
        return updated ? NoContent() : this.NotFoundProblem($"Email ID {emailId} is not attached to contact ID {contactId}.");
    }

    [HttpDelete("{contactId}/emails/{emailId}", Name = "DeleteContactEmail")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> DeleteEmail([FromRoute] Guid contactId, [FromRoute] Guid emailId, CancellationToken cancellationToken = default)
    {
        var deleted = await contactService.DeleteEmail(contactId, emailId, cancellationToken);
        return deleted ? NoContent() : this.NotFoundProblem($"Email ID {emailId} is not attached to contact ID {contactId}.");
    }
}
