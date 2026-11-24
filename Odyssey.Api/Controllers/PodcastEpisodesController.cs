using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Odyssey.Core.Internet.News.Podcasts;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;

namespace Odyssey.Api.Controllers;

/// <summary>
/// Podcast episodes (issue #556 §5.2, §5.3). Reads are <c>news.read</c>; a server download is
/// <c>podcast-audio.create</c> and removing stored audio <c>podcast-audio.delete</c>. No endpoint takes or returns a URL,
/// a path, a file name or a stored-object id. An episode of a feed that is not a podcast, or is pending deletion, is a
/// <c>404</c>.
/// </summary>
[ApiController]
[Route("api/podcast-episodes")]
public sealed class PodcastEpisodesController(PodcastEpisodeService episodes, NewsSearchLimiter searchLimiter, IServiceScopeFactory scopes) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PodcastEpisodePage))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [EndpointSummary("One page of episodes of podcast feeds, newest or oldest first; continue with nextCursor.")]
    public async Task<IActionResult> List([FromQuery] PodcastEpisodesQueryParams query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Search))
        {
            return Ok(await episodes.ListAsync(query, cancellationToken));
        }

        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        // A search runs the news full-text query, so it takes the news search limiter.
        using var lease = searchLimiter.TryAcquire(userId, out var retryAfter);
        if (lease is null)
        {
            Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: ReasonPhrases.GetReasonPhrase(StatusCodes.Status429TooManyRequests),
                detail: "Too many searches right now; try again shortly.",
                extensions: new Dictionary<string, object?> { ["code"] = NewsErrorCodes.SearchLimited });
        }

        return Ok(await episodes.ListAsync(query, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PodcastEpisodeDetail))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [EndpointSummary("One episode with its description, show notes and chapters.")]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        await episodes.GetAsync(id, cancellationToken) is { } episode ? Ok(episode) : NotFound(id);

    /// <remarks>
    /// The order is the spec's (§5.3 #8): authorise, load the audio row with its episode and feed, 404 unless it is
    /// stored, and only then open the file — so a 503 is answered only after every 404. The last-played stamp is written
    /// only after one response delivered the whole file to a <c>news.update</c> holder.
    /// </remarks>
    [HttpGet("{id:guid}/audio/content")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [EndpointSummary("An episode's stored audio as an attachment, with nosniff, CORP same-site, a strong ETag and range processing.")]
    public Task<IActionResult> AudioContent([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        ServeAudioAsync(id, inline: false, cancellationToken);

    /// <remarks>
    /// In-browser playback (issue #593 §5 #2): the same stored file, the same order, ETag and ranges as
    /// <see cref="AudioContent"/>, but inline, under a sandboxing CSP, and it NEVER stamps — playing a file to its end
    /// is not saving it to a device. GET only: the &lt;audio&gt; element needs no HEAD.
    /// </remarks>
    [HttpGet("{id:guid}/audio/stream")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status416RangeNotSatisfiable)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [EndpointSummary("An episode's stored audio for in-browser playback: inline, ranged, never counted as saved to a device.")]
    public Task<IActionResult> AudioStream([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        ServeAudioAsync(id, inline: true, cancellationToken);

    /// <summary>
    /// Saves the household's shared playback position, or marks the listen finished (issue #593 §5 #3). A position save
    /// right after a finish is ignored (still <c>204</c>); a finish for audio that is not stored is a <c>409</c>.
    /// </summary>
    [HttpPut("{id:guid}/playback")]
    [Authorize(Policy = PermissionClaims.NewsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ValidationProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [EndpointSummary("Save the shared playback position of an episode, or mark an in-browser listen finished.")]
    public async Task<IActionResult> SavePlayback([FromRoute] Guid id, [FromBody] PodcastPlaybackUpdate update, CancellationToken cancellationToken = default) =>
        await episodes.SavePlaybackAsync(id, update, cancellationToken) == PodcastPlaybackOutcome.NotFound ? NotFound(id) : NoContent();

    /// <summary>
    /// The one path both audio endpoints share, so their headers, ETag and 304 cannot drift: authorise (the attribute),
    /// 404 unless stored, the conditional GET, then open. <paramref name="inline"/> is the stream: inline disposition,
    /// a sandboxing CSP and no completion callback at all.
    /// </summary>
    private async Task<IActionResult> ServeAudioAsync(Guid id, bool inline, CancellationToken cancellationToken)
    {
        if (await episodes.AudioAsync(id, cancellationToken) is not { } descriptor)
        {
            return AudioNotFound(id);
        }

        SetFileHeaders();
        if (inline)
        {
            // Explicit: File() with no download name emits no Content-Disposition at all.
            Response.Headers.ContentDisposition = "inline";
            Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        }

        var etag = new EntityTagHeaderValue($"\"{descriptor.Sha256Hex}\"");
        if (Request.GetTypedHeaders().IfNoneMatch is { Count: > 0 } ifNoneMatch && ifNoneMatch.Any(t => t.Compare(etag, useStrongComparison: false)))
        {
            Response.Headers.ETag = etag.ToString();
            return StatusCode(StatusCodes.Status304NotModified);
        }

        if (await episodes.OpenAsync(id, descriptor, cancellationToken) is not { } content)
        {
            return AudioNotFound(id);
        }

        if (inline)
        {
            return File(content, descriptor.ContentType, fileDownloadName: null, lastModified: null, entityTag: etag, enableRangeProcessing: true);
        }

        var stream = new CountingStream(content);
        if (HttpMethods.IsGet(Request.Method) && User.HasPermission(PermissionClaims.NewsUpdate))
        {
            var length = descriptor.SizeBytes;
            // A Range the server ignored (a multi-range, an If-Range that no longer matches) still gets a 200 with the
            // whole body, but it is not a whole-file download the caller asked for, so it never stamps.
            var rangeRequested = Request.Headers.Range.Count > 0;
            Response.OnCompleted(async () =>
            {
                if (DeliveredWhole(length, stream.BytesRead, rangeRequested))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<PodcastEpisodeService>().StampPlayedAsync(id, CancellationToken.None);
                }
            });
        }

        return File(stream, descriptor.ContentType, descriptor.DownloadName, lastModified: null, entityTag: etag, enableRangeProcessing: true);
    }

    /// <summary>
    /// A full <c>200</c> to a request without a <c>Range</c>, or a <c>206</c> whose single range is exactly
    /// <c>0 … length−1</c>, written to the end without the client aborting (§5.3 #8). A tail range, any other partial range, a <c>HEAD</c> or a <c>304</c> never gets here or
    /// fails this test.
    /// </summary>
    private bool DeliveredWhole(long length, long bytesRead, bool rangeRequested)
    {
        if (HttpContext.RequestAborted.IsCancellationRequested || bytesRead != length)
        {
            return false;
        }

        return Response.StatusCode switch
        {
            StatusCodes.Status200OK => !rangeRequested,
            StatusCodes.Status206PartialContent => Response.GetTypedHeaders().ContentRange is { From: 0, To: { } to, Length: { } total }
                && total == length && to == length - 1,
            _ => false,
        };
    }

    [HttpPost("{id:guid}/audio")]
    [Authorize(Policy = PermissionClaims.PodcastAudioCreate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PodcastEpisodeAudioInfo))]
    [ProducesResponseType(StatusCodes.Status202Accepted, Type = typeof(PodcastEpisodeAudioInfo))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status507InsufficientStorage, Type = typeof(ProblemDetails))]
    [EndpointSummary("Queue a server download of the episode's audio. No body; already stored is a 200.")]
    public async Task<IActionResult> RequestAudio([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        if (await episodes.RequestAsync(id, userId, cancellationToken) is not { } outcome)
        {
            return NotFound(id);
        }

        return outcome.AlreadyStored ? Ok(outcome.Audio) : StatusCode(StatusCodes.Status202Accepted, outcome.Audio);
    }

    [HttpDelete("{id:guid}/audio")]
    [Authorize(Policy = PermissionClaims.PodcastAudioDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [EndpointSummary("Remove the episode's stored audio, or cancel its pending download. Idempotent.")]
    public async Task<IActionResult> RemoveAudio([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await episodes.RemoveAsync(id, userId, cancellationToken) ? NoContent() : NotFound(id);
    }

    private void SetFileHeaders()
    {
        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        // same-site, not same-origin: CORP compares the port too, and the dev stack's client and API differ in it.
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-site";
    }

    private ObjectResult NotFound(Guid id) => this.NotFoundProblem($"Podcast episode {id} was not found.");

    private ObjectResult AudioNotFound(Guid id) => this.NotFoundProblem($"No stored audio for podcast episode {id}.");

    /// <summary>Counts what the response read from the file, so the completion check can tell a whole transfer.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
