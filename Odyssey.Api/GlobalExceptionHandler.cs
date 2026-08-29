using Odyssey.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Core.Finance;

namespace Odyssey.Api;

/// <summary>
/// Single place where unhandled exceptions become HTTP responses. Controllers no longer wrap their
/// actions in try/catch; they let exceptions bubble to here so the exception-to-status mapping and
/// the logging happen once, consistently. Every response is an RFC 7807 <see cref="ProblemDetails"/>
/// body (<c>application/problem+json</c>), matching the handled errors that controllers return:
/// <list type="bullet">
/// <item><see cref="DomainException"/> → its declared <see cref="DomainException.StatusCode"/> (the
/// message is curated and safe to surface).</item>
/// <item>a cancellation caused by the client going away (an <see cref="OperationCanceledException"/>
/// with <see cref="HttpContext.RequestAborted"/> signalled) → nothing written and an informational log
/// line, because the socket is already gone and a disconnect is not an incident. A cancellation from
/// any other source (a server-side timeout, say) still takes the 500 arm.</item>
/// <item>A unique-constraint violation (a <see cref="DbUpdateException"/> wrapping a duplicate-key
/// <see cref="MySqlException"/> — the async save path EF wraps these in, which the old per-controller
/// <c>catch (MySqlException)</c> blocks never actually caught) → <c>409 Conflict</c> with a generic
/// message, so the raw driver text is not leaked.</item>
/// <item>anything else → <c>500</c> with a correlation id (logged alongside the exception and exposed
/// as an <c>errorId</c> problem extension) so a reported id can be traced back to the stack trace.</item>
/// </list>
/// </summary>
public static class GlobalExceptionHandler
{
    private const string DefaultMessage = "Something went wrong. Internal server error.";
    private const string ConflictMessage = "The request conflicts with existing data.";

    public static Task HandleAsync(HttpContext context)
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(GlobalExceptionHandler));

        switch (error)
        {
            case FeatureDisabledException disabled:
                logger.LogInformation(disabled,
                    "Feature disabled on {Method} {Path}: {Message}",
                    context.Request.Method, context.Request.Path, disabled.Message);
                return WriteAsync(context, disabled.StatusCode, disabled.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = disabled.Code });

            case DomainException domain:
                logger.LogInformation(domain,
                    "Domain error on {Method} {Path}: {Message}",
                    context.Request.Method, context.Request.Path, domain.Message);
                return WriteAsync(context, domain.StatusCode, domain.Message,
                    extensions: DomainExtensions(domain));

            case DbUpdateException dbUpdate
                when dbUpdate.GetBaseException() is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry }:
                logger.LogWarning(dbUpdate,
                    "Duplicate-key conflict on {Method} {Path}.",
                    context.Request.Method, context.Request.Path);
                return WriteAsync(context, StatusCodes.Status409Conflict, ConflictMessage);

            // Deleting a row still referenced by a RESTRICT foreign key (e.g. an in-use tag under a
            // service pre-check race). A referential conflict is a 409, not a 500; the driver text is
            // not leaked. Services should still pre-check for the common case (a clearer message).
            case DbUpdateException dbUpdate
                when dbUpdate.GetBaseException() is MySqlException
                { ErrorCode: MySqlErrorCode.RowIsReferenced or MySqlErrorCode.RowIsReferenced2 }:
                logger.LogWarning(dbUpdate,
                    "Referential conflict on {Method} {Path}.",
                    context.Request.Method, context.Request.Path);
                return WriteAsync(context, StatusCodes.Status409Conflict, ConflictMessage);

            // A client disconnect (browser navigated away, download aborted) cancels RequestAborted and
            // surfaces here as an OperationCanceledException. Logging it as an unhandled 500 pollutes the
            // error dashboards on exactly the long-running paths where it happens most — the ICS/vCard
            // exports, the data export and file analysis. There is nobody left to write a response to.
            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                logger.LogInformation("Request cancelled by client on {Method} {Path}.",
                    context.Request.Method, context.Request.Path);
                return Task.CompletedTask;

            default:
                var errorId = Guid.NewGuid();
                logger.LogError(error,
                    "Unhandled exception on {Method} {Path}. Error ID {ErrorId}.",
                    context.Request.Method, context.Request.Path, errorId);
                return WriteAsync(context, StatusCodes.Status500InternalServerError, DefaultMessage,
                    extensions: new Dictionary<string, object?> { ["errorId"] = errorId.ToString() });
        }
    }

    /// <summary>
    /// The problem-details extensions a domain error contributes: its <c>code</c> discriminator, and —
    /// for a field-attributable validation failure — the standard <c>errors</c> dictionary, so a form
    /// can render the message on the offending control instead of only in a toast (issue #421 Wave 0b).
    /// Returns null when the error carries neither, keeping the response byte-identical to before.
    /// </summary>
    private static IDictionary<string, object?>? DomainExtensions(DomainException domain)
    {
        var errors = domain is DomainValidationException { Errors: { } fieldErrors } ? fieldErrors : null;

        if (domain.Code is null && errors is null)
        {
            return null;
        }

        var extensions = new Dictionary<string, object?>();
        if (domain.Code is { } code)
        {
            extensions["code"] = code;
        }

        if (errors is not null)
        {
            extensions["errors"] = errors;
        }

        return extensions;
    }

    private static Task WriteAsync(HttpContext context, int statusCode, string detail,
        IDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = ReasonPhrases.GetReasonPhrase(statusCode),
            Detail = detail,
        };

        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}
