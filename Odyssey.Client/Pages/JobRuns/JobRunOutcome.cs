using System.Net;
using System.Text.Json;
using Odyssey.ApiClient;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages.JobRuns;

/// <summary>
/// What a Run item's response means to the user (issue #410 §3.5): one toast and one announcement with the same
/// text. Success is polite; every failure is assertive. Copy is fixed client text keyed by the status and the
/// <c>409</c>'s <c>code</c>; the server's own text is never rendered (FS2).
/// </summary>
/// <param name="Success">A <c>202</c> or <c>200</c>.</param>
/// <param name="Text">The toast's and the announcement's text.</param>
/// <param name="FollowRunId">The run to follow — only after a <c>202</c>.</param>
public sealed record JobRunOutcome(bool Success, string Text, Guid? FollowRunId)
{
    public static JobRunOutcome Describe(JobType job, ApiResult<JobRunStarted> result)
    {
        var p = BackgroundJobPresentation.JobOf(job);
        if (result.IsSuccess)
        {
            return result.Status == HttpStatusCode.Accepted
                ? new(true, $"{p.Name} run started", result.Value?.Id)
                : new(true, $"{p.Name}: nothing to do", null);
        }

        var problem = result.Problem;
        return result.Status switch
        {
            HttpStatusCode.Conflict when problem?.Code == JobRunConflictCodes.AlreadyRunning =>
                new(false, $"A {p.Lower} run is already in progress.", null),
            HttpStatusCode.Conflict when problem?.Code == JobRunConflictCodes.Held =>
                new(false, HeldText(p, problem), null),
            HttpStatusCode.Conflict => new(false, $"{p.Name} couldn’t be started.", null),
            HttpStatusCode.TooManyRequests => new(false, ExtensionTime(problem, "retryAfterUtc") is { } at
                ? $"Try again at {RunTime.Clock(at)}."
                : "Try again in a minute.", null),
            HttpStatusCode.Forbidden => new(false,
                $"You don’t have permission to run {p.Lower}. If your role just changed, try again in a minute.", null),
            _ => new(false, $"{p.Name} couldn’t be started. Try again.", null),
        };
    }

    // "{Job} is held: {words}[ until {time}]." — the reason only by exact name, the "until" only when sent (S-N2, F-N3).
    private static string HeldText(JobVisual p, ApiProblem problem)
    {
        var name = ExtensionString(problem, "heldReason");
        var reason = name is not null && Enum.GetNames<JobHeldReason>().Contains(name, StringComparer.Ordinal)
            ? BackgroundJobPresentation.HeldOf(Enum.Parse<JobHeldReason>(name))
            : null;
        if (reason is null)
        {
            return $"{p.Name} is held right now and can’t be started.";
        }

        var until = ExtensionTime(problem, "resumesAtUtc") is { } at ? $" until {RunTime.Clock(at)}" : string.Empty;
        return $"{p.Name} is held: {reason}{until}.";
    }

    private static string? ExtensionString(ApiProblem? problem, string name) =>
        problem?.Extensions is { } extensions && extensions.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? ExtensionTime(ApiProblem? problem, string name) =>
        problem?.Extensions is { } extensions && extensions.TryGetValue(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var at)
            ? at.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : at.ToUniversalTime()
            : null;
}
