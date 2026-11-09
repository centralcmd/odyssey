using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using Odyssey.Core.Finance.Instruments;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The instrument watchlist, its dashboard read, series and purge (issue #446 §5). Every price read requires
/// <c>instrument-prices.read</c>, which <c>AuthorizationPolicyTests</c> pins to imply <c>instruments.read</c>.
/// </summary>
[ApiController]
[Route("api/instruments")]
public sealed class InstrumentsController(
    InstrumentService instruments,
    InstrumentPriceService prices,
    InstrumentWatchlistService watchlist) : ControllerBase
{
    private bool CanReadPrices => User.HasPermission(PermissionClaims.InstrumentPricesRead);

    [HttpGet(Name = "GetInstruments")]
    [Authorize(Policy = PermissionClaims.InstrumentsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingInstrument>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List instruments.", Description = "Price fields are null, and sorting by the latest price date is refused, without instrument-prices.read.")]
    public async Task<IActionResult> Get([FromQuery] InstrumentsQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await instruments.ListAsync(query, CanReadPrices, cancellationToken));

    [HttpGet("summary", Name = "GetInstrumentSummary")]
    [Authorize(Policy = PermissionClaims.InstrumentsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(InstrumentSummary))]
    [SwaggerOperation(Summary = "Counts by type, followed and archived; the newest price date and attributions for price readers.")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken = default) =>
        Ok(await instruments.GetSummaryAsync(CanReadPrices, cancellationToken));

    [HttpGet("watchlist", Name = "GetInstrumentWatchlist")]
    [Authorize(Policy = PermissionClaims.InstrumentPricesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(InstrumentWatchlist))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The dashboard watchlist: followed instruments' latest prices and change in the display currency.")]
    public async Task<IActionResult> GetWatchlist(
        [FromQuery(Name = "mainCurrency")] [StringLength(3)] [RegularExpression("^[A-Za-z]{3}$")] string? mainCurrency,
        [FromQuery(Name = "sparklineDays")] [Range(0, InstrumentWatchlistService.MaxSparklineDays)] int sparklineDays = 0,
        CancellationToken cancellationToken = default) =>
        Ok(await watchlist.GetAsync(string.IsNullOrWhiteSpace(mainCurrency) ? "NOK" : mainCurrency, sparklineDays, cancellationToken));

    [HttpGet("{id:guid}", Name = "GetInstrument")]
    [Authorize(Policy = PermissionClaims.InstrumentsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingInstrument))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default) =>
        await instruments.GetAsync(id, CanReadPrices, cancellationToken) is { } instrument
            ? Ok(instrument)
            : this.NotFoundProblem($"Instrument {id} not found.");

    [HttpPost(Name = "PostInstrument")]
    [Authorize(Policy = PermissionClaims.InstrumentsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingInstrument))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Post([FromBody] NewInstrument request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var created = await instruments.CreateAsync(request, userId, cancellationToken);
        return CreatedAtRoute("GetInstrument", new { id = created.InstrumentId }, created);
    }

    [HttpPut("{id:guid}", Name = "PutInstrument")]
    [Authorize(Policy = PermissionClaims.InstrumentsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingInstrument))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update an instrument. Not an upsert; identity fields are immutable once prices or sources exist.")]
    public async Task<IActionResult> Put(Guid id, [FromBody] NewInstrument request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await instruments.UpdateAsync(id, request, userId, cancellationToken) is { } updated
            ? Ok(updated)
            : this.NotFoundProblem($"Instrument {id} not found.");
    }

    [HttpPost("{id:guid}/archive", Name = "ArchiveInstrument")]
    [Authorize(Policy = PermissionClaims.InstrumentsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken = default) => SetArchived(id, true, cancellationToken);

    [HttpPost("{id:guid}/unarchive", Name = "UnarchiveInstrument")]
    [Authorize(Policy = PermissionClaims.InstrumentsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public Task<IActionResult> Unarchive(Guid id, CancellationToken cancellationToken = default) => SetArchived(id, false, cancellationToken);

    [HttpDelete("{id:guid}", Name = "DeleteInstrument")]
    [Authorize(Policy = PermissionClaims.InstrumentsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default) =>
        await instruments.DeleteAsync(id, User.ActingUserId(), cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Instrument {id} not found.");

    [HttpGet("{id:guid}/prices/series", Name = "GetInstrumentPriceSeries")]
    [Authorize(Policy = PermissionClaims.InstrumentPricesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(InstrumentPriceSeries))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "One closing price per date for a chart; at most 3 660 days.")]
    public async Task<IActionResult> GetSeries(
        Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken = default) =>
        await prices.GetSeriesAsync(id, from, to, cancellationToken) is { } series
            ? Ok(series)
            : this.NotFoundProblem($"Instrument {id} not found.");

    [HttpDelete("{id:guid}/prices", Name = "PurgeInstrumentPrices")]
    [Authorize(Policy = PermissionClaims.InstrumentPricesDelete)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(InstrumentPricePurgeResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Delete an instrument's prices in one scope.",
        Description = "source and expectedCount are required; a stale count is a 409 that deletes nothing. Fetched rows also need system-settings.update.")]
    public async Task<IActionResult> Purge(
        Guid id,
        [FromQuery(Name = "source")] [Required] InstrumentPricePurgeScope? source,
        [FromQuery(Name = "expectedCount")] [Required] [Range(0, int.MaxValue)] int? expectedCount,
        CancellationToken cancellationToken = default)
    {
        if (source is InstrumentPricePurgeScope.Fetched or InstrumentPricePurgeScope.All
            && !User.HasPermission(PermissionClaims.SystemSettingsUpdate))
        {
            return this.ForbiddenProblem("Deleting fetched prices requires permission to manage price sources.");
        }

        var deleted = await prices.PurgeAsync(id, source!.Value, expectedCount!.Value, User.ActingUserId(), cancellationToken);
        return Ok(new InstrumentPricePurgeResult { Deleted = deleted });
    }

    private async Task<IActionResult> SetArchived(Guid id, bool archived, CancellationToken cancellationToken) =>
        await instruments.SetArchivedAsync(id, archived, User.ActingUserId(), cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Instrument {id} not found.");
}
