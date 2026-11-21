// Linked (not referenced) into the test projects that stub an HttpClient's transport, the way TestTierGate.cs is.

using System.Net;
using System.Text;

namespace Odyssey.Testing.Http;

/// <summary>
/// The terminal <see cref="HttpMessageHandler"/> for a test: it records every request (its body read into a string
/// before the caller can dispose the content) and answers each one from, in order of precedence, the
/// <see cref="Enqueue">queued</see> responses, the responder passed to the constructor, a fixed
/// <see cref="Response"/>, or the canned <see cref="Status"/>/<see cref="Body"/>/<see cref="MediaType"/>, which builds
/// a fresh response per request so a client that disposes one cannot break the next.
/// </summary>
public sealed class RecordingHttpHandler : HttpMessageHandler
{
    public const string JsonMediaType = "application/json";

    public const string ProblemMediaType = "application/problem+json";

    private readonly Lock gate = new();
    private readonly List<RecordedRequest> requests = [];
    private readonly Queue<HttpResponseMessage> queued = new();
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder;

    /// <summary>Answers <c>200 OK</c> with no content until told otherwise.</summary>
    public RecordingHttpHandler()
    {
    }

    /// <summary>Answers every request with <paramref name="body"/>.</summary>
    public RecordingHttpHandler(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = JsonMediaType)
    {
        Status = status;
        Body = body;
        MediaType = mediaType;
    }

    /// <summary>Answers every request with <paramref name="status"/>, carrying <paramref name="body"/> when it is not null.</summary>
    public RecordingHttpHandler(HttpStatusCode status, string? body = null, string mediaType = JsonMediaType)
    {
        Status = status;
        Body = body;
        MediaType = mediaType;
    }

    /// <summary>Answers each request with what <paramref name="respond"/> returns, or throws.</summary>
    public RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        responder = (request, _) => Task.FromResult(respond(request));

    /// <summary>Answers each request asynchronously; the token is the one the client sent with it.</summary>
    public RecordingHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        responder = respond;

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>The canned response's body; null sends no content at all.</summary>
    public string? Body { get; set; }

    public string MediaType { get; set; } = JsonMediaType;

    /// <summary>One response instance returned to every request, overriding the canned one.</summary>
    public HttpResponseMessage? Response { get; set; }

    /// <summary>
    /// Answers <c>/api/antiforgery/token</c> with a token without recording it, so a POST through the API client's
    /// antiforgery handler reaches the request under test.
    /// </summary>
    public bool AnswersAntiforgeryToken { get; init; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    public RecordedRequest? Last
    {
        get
        {
            lock (gate)
            {
                return requests.Count == 0 ? null : requests[^1];
            }
        }
    }

    public HttpRequestMessage? LastRequest => Last?.Message;

    public string? LastBody => Last?.Body;

    public string? LastPath => Last?.Path;

    public HttpMethod? LastMethod => Last?.Method;

    /// <summary>The media types of the last multipart request's parts, snapshotted before the client disposes them.</summary>
    public IReadOnlyList<string?> LastPartMediaTypes => Last?.PartMediaTypes ?? [];

    /// <summary>Queues a response for the next unanswered request, ahead of every other source.</summary>
    public RecordingHttpHandler Enqueue(HttpResponseMessage response)
    {
        lock (gate)
        {
            queued.Enqueue(response);
        }

        return this;
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, JsonMediaType) };

    public static HttpResponseMessage Problem(string body, HttpStatusCode status) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, ProblemMediaType) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (AnswersAntiforgeryToken
            && request.RequestUri?.AbsolutePath.EndsWith("/api/antiforgery/token", StringComparison.Ordinal) == true)
        {
            return Json("""{"token":"token"}""");
        }

        var partMediaTypes = request.Content is MultipartContent multipart
            ? multipart.Select(part => part.Headers.ContentType?.MediaType).ToList()
            : [];
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        HttpResponseMessage? next = null;
        lock (gate)
        {
            requests.Add(new RecordedRequest(request, body, partMediaTypes));
            if (queued.Count > 0)
            {
                next = queued.Dequeue();
            }
        }

        if (next is not null)
        {
            return next;
        }

        if (responder is not null)
        {
            return await responder(request, cancellationToken);
        }

        if (Response is not null)
        {
            return Response;
        }

        var response = new HttpResponseMessage(Status);
        if (Body is not null)
        {
            response.Content = new StringContent(Body, Encoding.UTF8, MediaType);
        }

        return response;
    }
}

/// <summary>One request as the handler saw it, with its body already read.</summary>
public sealed record RecordedRequest(HttpRequestMessage Message, string? Body, IReadOnlyList<string?> PartMediaTypes)
{
    public HttpMethod Method => Message.Method;

    public Uri? Uri => Message.RequestUri;

    public string? Path => Message.RequestUri?.AbsolutePath;
}
