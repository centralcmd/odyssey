using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The news reader's storage status (issue #470 §5.5 item 26), Admin-only through <c>file-storage.read</c>. It always
/// answers <c>200</c>: the volume's state is part of the body, never the status code. Every role holding
/// <c>file-storage.read</c> also holds <c>news.read</c>, so the feed titles it lists reach no one who could not read them.
/// </summary>
[ApiController]
[Route("api/news-status")]
public sealed class NewsStatusController(NewsStatusService status) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionClaims.FileStorageRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(NewsStatus))]
    [SwaggerOperation(Summary = "News storage state, totals against the resolved quotas, and the feeds using the most image bytes.")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken = default) =>
        Ok(await status.GetAsync(cancellationToken));
}
