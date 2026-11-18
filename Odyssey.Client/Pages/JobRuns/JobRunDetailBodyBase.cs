using Microsoft.AspNetCore.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages.JobRuns;

/// <summary>
/// What every run-detail body shares (issue #410 §3.6): the run, whether the caller may read its job's own fields, and
/// the words for a missing value. A field the server nulled under the cross-claim rule reads
/// "Not shown — needs {area} access"; one the run genuinely has none of reads "Not recorded" (F-N5). The two are told
/// apart by the job's <see cref="BackgroundJobSummary.CanReadDetail"/> on the overview.
/// </summary>
public abstract class JobRunDetailBodyBase : ComponentBase
{
    [Parameter, EditorRequired] public JobRunDetail Run { get; set; } = default!;

    /// <summary>The caller holds this job's detail-read claim (true while the overview is unknown).</summary>
    [Parameter] public bool CanReadDetail { get; set; } = true;

    protected string? Area => BackgroundJobPresentation.JobOf(Run.JobType).Area;

    /// <summary>The words for a value the run does not have.</summary>
    protected string Missing => CanReadDetail || Area is null ? "Not recorded" : $"Not shown — needs {Area} access";

    protected string N(int? value) => value is { } v ? BackgroundJobPresentation.Count(v) : Missing;

    /// <summary>A worded value that may be absent: "None" when the run had none, the claim words when it was withheld.</summary>
    protected string Words(string? words) => words ?? (CanReadDetail || Area is null ? "None" : Missing);

    protected string DurationText => Run.DurationMs is { } ms
        ? BackgroundJobPresentation.Duration(ms)
        : Run.Status == JobRunStatus.Running ? "Running" : "Ongoing";

    protected string ProblemText => Run.FailureCategory is { } category
        ? BackgroundJobPresentation.FailureOf(category)
        : Run.Status == JobRunStatus.Held && BackgroundJobPresentation.HeldOf(Run.HeldReason) is { } held
            ? Capitalize(held)
            : "None";

    protected string StartedText => RunTime.Full(Run.StartedAtUtc);

    protected string FinishedText => Run.FinishedAtUtc is { } done
        ? RunTime.Full(done)
        : Run.Status == JobRunStatus.Running ? "Still running" : "Ongoing";

    protected string TriggerText => BackgroundJobPresentation.TriggerOf(Run.Trigger);

    protected static RenderFragment Text(string value) => builder => builder.AddContent(0, value);

    internal static string Capitalize(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
}
