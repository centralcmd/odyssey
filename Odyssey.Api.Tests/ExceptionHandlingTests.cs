using System.Net;
using System.Text.Json;
using Odyssey.Api;
using Odyssey.Core.Finance;
using Odyssey.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Xunit;

namespace Odyssey.Api.Tests;

public class ExceptionHandlingTests
{
    private static DefaultHttpContext ContextFor(Exception error, out MemoryStream body)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = error });
        body = new MemoryStream();
        context.Response.Body = body;
        return context;
    }

    private static async Task<ProblemDetails> ReadProblemAsync(MemoryStream body)
    {
        body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<ProblemDetails>(body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    [Fact]
    public async Task UnhandledException_ReturnsInternalServerError_WithTraceableErrorId()
    {
        var context = ContextFor(new InvalidOperationException("Boom"), out var body);

        await GlobalExceptionHandler.HandleAsync(context);

        Assert.Equal(HttpStatusCode.InternalServerError, (HttpStatusCode)context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        var problem = await ReadProblemAsync(body);
        Assert.Equal("Something went wrong. Internal server error.", problem.Detail);
        Assert.True(Guid.TryParse(problem.Extensions["errorId"]!.ToString(), out _));
    }

    [Fact]
    public async Task DomainException_MapsToItsDeclaredStatusCodeAndMessage()
    {
        var context = ContextFor(new DomainNotFoundException("Account ID 42 not found."), out var body);

        await GlobalExceptionHandler.HandleAsync(context);

        Assert.Equal(HttpStatusCode.NotFound, (HttpStatusCode)context.Response.StatusCode);
        var problem = await ReadProblemAsync(body);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Equal("Account ID 42 not found.", problem.Detail);
    }

    [Fact]
    public async Task FeatureDisabledException_MapsTo503_WithFeatureDisabledCode()
    {
        // A concrete FeatureDisabledException (the base is abstract) must take the dedicated arm —
        // which is ordered before the general DomainException arm — and surface the machine-readable
        // code as a problem extension.
        var context = ContextFor(new FileAnalysisDisabledException(), out var body);

        await GlobalExceptionHandler.HandleAsync(context);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (HttpStatusCode)context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        var problem = await ReadProblemAsync(body);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.Status);
        Assert.Equal("File analysis is disabled by configuration.", problem.Detail);
        Assert.Equal("feature_disabled", problem.Extensions["code"]!.ToString());
    }

    /// <summary>
    /// The configuration-unavailable variant shares the <c>FeatureDisabledException</c> arm and its
    /// <c>503</c>, but carries its own code (issue #439 §11) — so a client can tell "an administrator
    /// turned this off" from "the server has a configuration problem", and so the existing
    /// <c>feature_disabled</c> assertions above stay exact rather than being loosened to match both.
    /// </summary>
    [Fact]
    public async Task FileAnalysisUnavailableException_MapsTo503_WithADistinctCode_AndStaticDetail()
    {
        var context = ContextFor(new FileAnalysisUnavailableException(), out var body);

        await GlobalExceptionHandler.HandleAsync(context);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (HttpStatusCode)context.Response.StatusCode);
        var problem = await ReadProblemAsync(body);
        Assert.Equal("configuration_unavailable", problem.Extensions["code"]!.ToString());
        Assert.NotEqual(FeatureDisabledException.FeatureCode, problem.Extensions["code"]!.ToString());

        // Static text: it names no stored value, no host and no parse error. The diagnosis is in the
        // server log, exactly as FileAnalysisSettingsLookup already does for the privacy-notice URL.
        Assert.Equal(
            "Document analysis is temporarily unavailable while the server recovers a configuration problem.",
            problem.Detail);
    }

    /// <summary>
    /// The stale-disclosure refusal (issue #439 §5.3c) is a <c>409</c> with its own code, taken by the
    /// general <c>DomainException</c> arm. Not an error state in the UI: the dialog stays open and
    /// re-prompts, which is why it needs a code rather than being just another conflict.
    /// </summary>
    [Fact]
    public async Task FileAnalysisDisclosureChangedException_MapsTo409_WithDisclosureChangedCode()
    {
        var context = ContextFor(new FileAnalysisDisclosureChangedException(), out var body);

        await GlobalExceptionHandler.HandleAsync(context);

        Assert.Equal(HttpStatusCode.Conflict, (HttpStatusCode)context.Response.StatusCode);
        var problem = await ReadProblemAsync(body);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Equal("disclosure_changed", problem.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task ClientDisconnect_WritesNothing_AndIsNotLoggedAsAnError()
    {
        var context = ContextFor(new OperationCanceledException(), out var body);
        context.RequestAborted = new CancellationToken(canceled: true);
        context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;

        await GlobalExceptionHandler.HandleAsync(context);

        // No problem body, and the status the pipeline already settled on is left alone — the socket
        // is gone, so there is nothing to say and no errorId worth minting.
        Assert.Equal(0, body.Length);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task CancellationWithoutClientDisconnect_StillReturnsInternalServerError()
    {
        // A server-side timeout (FileAnalysis's 120s budget, for instance) cancels without the client
        // going away. That is a real failure and must keep its traceable 500.
        var context = ContextFor(new OperationCanceledException(), out var body);

        await GlobalExceptionHandler.HandleAsync(context);

        Assert.Equal(HttpStatusCode.InternalServerError, (HttpStatusCode)context.Response.StatusCode);
        var problem = await ReadProblemAsync(body);
        Assert.True(Guid.TryParse(problem.Extensions["errorId"]!.ToString(), out _));
    }

    // MySqlException has no public constructor; the (MySqlErrorCode, string) overload is internal.
    private static MySqlException DuplicateKey(string message) =>
        (MySqlException)Activator.CreateInstance(
            typeof(MySqlException),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: new object[] { MySqlErrorCode.DuplicateKeyEntry, message },
            culture: null)!;

    [Fact]
    public async Task DuplicateKeyDbUpdateException_MapsToConflict_WithoutLeakingDriverMessage()
    {
        var mySqlException = DuplicateKey("Duplicate entry 'x' for key 'IX_secret'");
        var context = ContextFor(new DbUpdateException("save failed", mySqlException), out var body);

        await GlobalExceptionHandler.HandleAsync(context);

        Assert.Equal(HttpStatusCode.Conflict, (HttpStatusCode)context.Response.StatusCode);
        var problem = await ReadProblemAsync(body);
        Assert.Equal("The request conflicts with existing data.", problem.Detail);
        Assert.DoesNotContain("IX_secret", JsonSerializer.Serialize(problem));
    }
}
