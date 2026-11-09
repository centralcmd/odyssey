using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using Odyssey.Core.Finance.Instruments.Sync;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Api.Controllers;

/// <summary>
/// Price-source administration (issue #446 §5 endpoints 16–18). A binding decides what leaves the deployment, so the
/// whole surface is system-settings gated; it makes no outbound call. Run-now and history are the background-jobs
/// endpoints.
/// </summary>
[ApiController]
[Route("api/system-settings/instrument-price-sources")]
[Authorize(Policy = PermissionClaims.SystemSettingsRead)]
public sealed class InstrumentPriceSourcesController(InstrumentPriceSourceAdminService admin) : ControllerBase
{
    [HttpGet(Name = "GetInstrumentPriceSources")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(InstrumentPriceSourcesStatus))]
    [SwaggerOperation(Summary = "The registered price sources, their state and the sync status. Empty while no source is installed.")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken = default) =>
        Ok(await admin.GetStatusAsync(cancellationToken));

    [HttpGet("bindings/{instrumentId:guid}", Name = "GetInstrumentPriceBindings")]
    [Authorize(Policy = PermissionClaims.InstrumentsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingInstrumentPriceBinding>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> GetBindings(Guid instrumentId, CancellationToken cancellationToken = default) =>
        await admin.GetBindingsAsync(instrumentId, cancellationToken) is { } bindings
            ? Ok(bindings)
            : this.NotFoundProblem($"Instrument {instrumentId} not found.");

    [HttpPut("bindings/{instrumentId:guid}", Name = "PutInstrumentPriceBindings")]
    [Authorize(Policy = PermissionClaims.SystemSettingsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingInstrumentPriceBinding>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace an instrument's whole binding list; priority is position.")]
    public async Task<IActionResult> PutBindings(
        Guid instrumentId, [FromBody] InstrumentPriceBindingsUpdate update, CancellationToken cancellationToken = default) =>
        await admin.ReplaceBindingsAsync(instrumentId, update, User.ActingUserId(), cancellationToken) is { } bindings
            ? Ok(bindings)
            : this.NotFoundProblem($"Instrument {instrumentId} not found.");
}
