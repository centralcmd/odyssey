using System.Text.RegularExpressions;
using Odyssey.Client.Models;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The queued analysis's pure rules (issue #445): the poll timing, the terminal copy keyed on the code, the sent
/// line, the Files row matrix — and the source-lint that keeps server text out of the dialog.
/// </summary>
public class FileAnalysisQueuedModelTests
{
    // ── Poll timing ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 2)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(0, 1)]
    [InlineData(60, 10)]
    public void TheInterval_IsRetryAfterClampedToOneToTenSeconds_ElseTwo(int? seconds, int expected)
    {
        var delta = seconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        Assert.Equal(TimeSpan.FromSeconds(expected), FileAnalysisPollPolicy.Interval(delta));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 10)]
    [InlineData(9, 10)]
    public void TheBackoff_Is2_4_8_ThenTen(int failures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), FileAnalysisPollPolicy.Backoff(failures));
    }

    // ── Terminal copy ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("provider_error", "We couldn’t read this statement", FileAnalysisTerminalAction.Retry)]
    [InlineData("provider_credential_error", "Analysis isn’t set up correctly", FileAnalysisTerminalAction.Close)]
    [InlineData("configuration_unavailable", "Analysis is temporarily unavailable", FileAnalysisTerminalAction.Retry)]
    [InlineData("destination_changed", "Who processes your document changed", FileAnalysisTerminalAction.Review)]
    [InlineData("disclosure_changed", "Who processes your document changed", FileAnalysisTerminalAction.Review)]
    [InlineData("prompt_version_mismatch", "The server was being updated", FileAnalysisTerminalAction.Retry)]
    [InlineData("not_started", "The analysis didn’t start", FileAnalysisTerminalAction.Retry)]
    [InlineData("interrupted", "The analysis stopped", FileAnalysisTerminalAction.Retry)]
    [InlineData("internal_error", "Something went wrong", FileAnalysisTerminalAction.Retry)]
    [InlineData("something_new", "Something went wrong", FileAnalysisTerminalAction.Retry)]
    [InlineData(null, "Something went wrong", FileAnalysisTerminalAction.Retry)]
    public void EveryFailedCode_HasItsRow(string? code, string heading, FileAnalysisTerminalAction action)
    {
        var copy = FileAnalysisJobCopy.ForFailed(code);
        Assert.Equal(heading, copy.Heading);
        Assert.Equal(action, copy.Action);
        Assert.True(copy.ShowSent);
    }

    [Theory]
    [InlineData("disabled", "Analysis was switched off")]
    [InlineData("requester_unavailable", "Analysis was stopped")]
    [InlineData("file_changed", "Analysis was stopped")]
    [InlineData("unknown_code", "Analysis was stopped")]
    public void EveryCancelledCode_HasItsRow_AndOnlyClose(string code, string heading)
    {
        var copy = FileAnalysisJobCopy.ForCancelled(code);
        Assert.Equal(heading, copy.Heading);
        Assert.Equal(FileAnalysisTerminalAction.Close, copy.Action);
    }

    [Fact]
    public void ThePostOutcomes_NeverSayWhetherAnythingWasSent()
    {
        Assert.False(FileAnalysisJobCopy.PostTimeout.ShowSent);
        Assert.False(FileAnalysisJobCopy.PostUnavailable.ShowSent);
        Assert.False(FileAnalysisJobCopy.PostRefused.ShowSent);
        Assert.False(FileAnalysisJobCopy.PollForbidden.ShowSent);
    }

    [Theory]
    [InlineData(FileAnalysisDocumentSent.No, "Anthropic", "Your document was not sent.")]
    [InlineData(FileAnalysisDocumentSent.Yes, "Anthropic", "Your document was sent to Anthropic.")]
    [InlineData(FileAnalysisDocumentSent.Possibly, null, "Your document may have been sent to the analysis provider.")]
    public void TheSentLine_FollowsDocumentSent_AndTheStampedProcessor(FileAnalysisDocumentSent sent, string? processor, string line)
    {
        Assert.Equal(line, FileAnalysisJobCopy.SentLine(sent, processor));
    }

    [Theory]
    [InlineData(33554432L, "The limit is 32 MB.")]
    [InlineData(10485760L, "The limit is 10 MB.")]
    [InlineData(null, "It’s over the analysis size limit.")]
    public void TheTooLargeBody_UsesOnlyTheServerLimit(long? limit, string body)
    {
        Assert.Equal(body, FileAnalysisJobCopy.TooLargeBody(limit));
    }

    // ── The Files row matrix ─────────────────────────────────────────────────

    private static ResumableAnalysisSummary Summary(
        FileAnalysisJobStatus status, int pending = 0, LatestAnalysisFailure? latest = null,
        FileAnalysisDocumentSent sent = FileAnalysisDocumentSent.No) =>
        new(Guid.NewGuid(), Guid.NewGuid(), status, null, pending, pending, sent, null, latest);

    [Theory]
    [InlineData(FileAnalysisJobStatus.Queued)]
    [InlineData(FileAnalysisJobStatus.Running)]
    public void AnOpenJob_IsProgressOnly_NeverAPendingReview(FileAnalysisJobStatus status)
    {
        var state = FileAnalysisRowState.For(Summary(status, pending: 0));

        Assert.NotNull(state.OpenJobId);
        Assert.Null(state.PendingReview);
        Assert.Null(state.Failure);
    }

    [Fact]
    public void APendingReview_BesideANewerFailure_ShowsBoth()
    {
        var failureId = Guid.NewGuid();
        var state = FileAnalysisRowState.For(Summary(FileAnalysisJobStatus.Completed, pending: 4,
            new LatestAnalysisFailure(failureId, FileAnalysisJobStatus.Failed, "provider_error", FileAnalysisDocumentSent.Possibly, null),
            FileAnalysisDocumentSent.Yes));

        Assert.NotNull(state.PendingReview);
        Assert.Equal(failureId, state.Failure!.JobId);
        Assert.False(state.Failure.Stopped);
        Assert.True(state.FailureIsLastAttempt);
        Assert.Equal(FileAnalysisDocumentSent.Possibly, state.PriorDocumentSent);
    }

    [Fact]
    public void AStoppedJob_IsAStoppedResult()
    {
        var state = FileAnalysisRowState.For(Summary(FileAnalysisJobStatus.Cancelled));

        Assert.True(state.Failure!.Stopped);
        Assert.False(state.FailureIsLastAttempt);
    }

    [Fact]
    public void ACompletedJobWithNothingPending_IsNothing()
    {
        Assert.Same(FileAnalysisRowState.None, FileAnalysisRowState.For(Summary(FileAnalysisJobStatus.Completed)));
        Assert.Same(FileAnalysisRowState.None, FileAnalysisRowState.For(null));
    }

    // ── Source-lint (issue #445 §5, F17) ─────────────────────────────────────

    private static string DialogSource =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "FileAnalysisDialog.razor.cs"))
        + File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "FileAnalysisDialog.razor"));

    [Fact]
    public void TheDialog_RendersNoServerText()
    {
        var source = DialogSource;

        Assert.DoesNotContain("FailureMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ex.Message", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkupString", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_errorMessage", source, StringComparison.Ordinal);
        // `.Error` as a member read (ApiResult.Error); the Severity enum member is not one.
        Assert.Empty(Regex.Matches(source, @"(?<!Severity)\.Error\b"));
        Assert.DoesNotContain(".Detail", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Title", source.Replace("_session.Title", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePollPath_CallsNoToastVariant()
    {
        var source = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "FileAnalysisDialog.razor.cs"));
        var poll = source[source.IndexOf("private async Task PollLoopAsync", StringComparison.Ordinal)..
                          source.IndexOf("private void ObserveOpen", StringComparison.Ordinal)];

        Assert.DoesNotMatch(new Regex(@"OrToast|\bToast\(|Snackbar"), poll);
    }
}
