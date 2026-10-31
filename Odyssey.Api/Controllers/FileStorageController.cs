using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Core.FileStorage;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// File-volume telemetry (issue #346 §5). There is deliberately no generic upload or download endpoint over stored
/// objects: bytes are reachable only through a consuming feature's own endpoint, gated on that feature's claim, so
/// nothing here takes an object id or a path.
/// </summary>
[ApiController]
[Route("api/file-storage")]
[Authorize(Policy = PermissionClaims.FileStorageRead)]
public sealed class FileStorageController(FileStorageStatusService status) : ControllerBase
{
    [HttpGet("volumes")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(IReadOnlyList<FileStorageVolumeStatus>))]
    [SwaggerOperation(
        Summary = "Each file volume's state, usage and bindings, as this API process sees them.",
        Description = "A volume degraded only in the worker shows in the worker's log, not here. The maintenance fields are "
            + "instance-wide and repeated on every entry. Empty when file storage is not set up. Never returns a path, a file "
            + "name or an object id.")]
    public async Task<ActionResult<IReadOnlyList<FileStorageVolumeStatus>>> GetVolumes(CancellationToken cancellationToken) =>
        Ok(await status.GetVolumesAsync(cancellationToken));
}
