using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Models;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The analyze dialog against a QUEUED job (issue #445): the poller and its clock, the attached review that never
/// auto-matches, the client-owned terminal copy and sent line, and the POST refusals. The poll delay runs on a
/// <see cref="FakeTimeProvider"/>, so nothing here sleeps.
/// </summary>
[Collection(FileAnalysisDialogCollection.Name)]
public sealed class FileAnalysisQueuedDialogTests : IAsyncDisposable
{
    private const string ServerText = "SERVER-SUPPLIED TEXT THAT MUST NEVER RENDER";

    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid FileId = Guid.NewGuid();
    private static readonly Guid AccountFileId = Guid.NewGuid();
    private static readonly Guid JobId = Guid.NewGuid();

    private readonly BunitContext ctx = new();
    private readonly Mock<IAccountsApiClient> accounts = new();
    private readonly Mock<IFileAnalysisApiClient> analysis = new();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly Queue<ApiResult<ExistingFileAnalysisJob>> polls = new();
    private int pollCount;

    static FileAnalysisQueuedDialogTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    public FileAnalysisQueuedDialogTests()
    {
        FileAnalysisDialog.InteractiveCheck = static () => true;
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<TimeProvider>(time);
        ctx.Services.AddSingleton(accounts.Object);
        ctx.Services.AddSingleton(analysis.Object);
        ctx.Services.AddSingleton(Mock.Of<IContactsApiClient>());
        ctx.Services.AddSingleton(Mock.Of<ITagsApiClient<ExistingTransactionTag>>());

        var reference = new Mock<IReferenceDataCache>();
        reference.Setup(r => r.ContactsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        reference.Setup(r => r.TransactionTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        reference.Setup(r => r.ActiveCurrenciesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        ctx.Services.AddSingleton(reference.Object);

        var disclosures = new Mock<IFileAnalysisDisclosureCache>();
        disclosures.Setup(d => d.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new FileAnalysisDisclosureDto
        {
            Processor = "Today's Processor",
            ProcessorRegion = "United States",
            LawfulBasis = "Consent",
            PrivacyNoticeUrl = "https://example.com/privacy",
            Model = "model",
            Enabled = true,
            DisclosureVersion = "v1",
            MatchVocabularyFields = ["contactName", "tagName"],
            MatchVocabularyCoverage = FileAnalysisDisclosureText.AllActiveCoverage,
        });
        disclosures.SetupGet(d => d.IsResolved).Returns(true);
        ctx.Services.AddSingleton(disclosures.Object);

        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedIn());

        analysis.Setup(a => a.GetJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCount++;
                return polls.Count > 1 ? polls.Dequeue() : polls.Peek();
            });
    }

    public async ValueTask DisposeAsync()
    {
        FileAnalysisDialog.InteractiveCheck = OperatingSystem.IsBrowser;
        await ctx.DisposeAsync();
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private static ExistingFileAnalysisJob Job(
        FileAnalysisJobStatus status, string? code = null, FileAnalysisDocumentSent sent = FileAnalysisDocumentSent.No,
        string? processor = "Stamped Processor", Guid? accountFileId = null, bool transferStarted = false, int candidates = 0) =>
        new(JobId, accountFileId ?? AccountFileId, status, null, null, null, code, ServerText, "Claude", "model", "1.1",
            [.. Enumerable.Range(0, candidates).Select(i => new ExistingFileAnalysisCandidateTransaction(
                Guid.NewGuid(), new DateTime(2026, 11, 1), null, $"Row {i}", null, null, -1m, "USD", null, null, 0.9m, null,
                CandidateTransactionReviewStatus.Pending, null, null, null, [], null, null, MatchMethod.None))],
            FileAnalysisMatchStatus.NotRun, null, 0.6, FileAnalysisMatchVocabularyScope.NamesOnly,
            null, null, null, null, null,
            QueuedAt: DateTime.UtcNow, TransferStartedAt: transferStarted ? DateTime.UtcNow : null,
            DocumentSent: sent, ProcessorInForce: processor);

    private static ApiResult<ExistingFileAnalysisJob> Ok(ExistingFileAnalysisJob job, int? retryAfterSeconds = null) =>
        ApiResult<ExistingFileAnalysisJob>.Success(job, HttpStatusCode.OK) with
        {
            RetryAfterDelta = retryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
        };

    private static ApiResult<ExistingFileAnalysisJob> Failure(HttpStatusCode status) =>
        ApiResult<ExistingFileAnalysisJob>.Failure(status, new ApiProblem { Status = (int)status, Detail = ServerText });

    private static ApiProblem Problem(string json) =>
        JsonSerializer.Deserialize<ApiProblem>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private IRenderedComponent<Host> Render(FileAnalysisDialog.StartMode start, Guid? jobId = null,
        FileAnalysisDocumentSent? prior = null, bool alreadyRunning = false) =>
        ctx.Render<Host>(p => p
            .Add(h => h.Start, start)
            .Add(h => h.JobId, jobId)
            .Add(h => h.Prior, prior)
            .Add(h => h.AlreadyRunning, alreadyRunning));

    private void Advance(TimeSpan by, IRenderedComponent<Host> cut) => cut.InvokeAsync(() => time.Advance(by)).Wait();

    private static string Text(IRenderedComponent<Host> cut) => cut.Markup;

    private async Task SendFromConsentAsync(IRenderedComponent<Host> cut)
    {
        var panel = cut.FindComponent<FileAnalysisConsentPanel>();
        await cut.InvokeAsync(() => panel.Instance.CheckedChanged.InvokeAsync(true));
        var send = cut.FindAll("button").Single(b => b.TextContent.Contains("Send & analyze", StringComparison.Ordinal));
        await cut.InvokeAsync(() => send.Click());
    }

    // ── Progress / attached ──────────────────────────────────────────────────

    [Fact]
    public void Progress_QueuedThenRunningThenCompleted_OpensAnUnmatchedReview_WithoutCallingMatch()
    {
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Queued)));
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Running, transferStarted: true)));
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Completed, sent: FileAnalysisDocumentSent.Yes, candidates: 2)));

        var cut = Render(FileAnalysisDialog.StartMode.Progress, JobId);

        cut.WaitForAssertion(() => Assert.Contains("Queued — nothing has been sent yet", Text(cut)));
        var current = cut.Find("li[aria-current='step']");
        Assert.Contains("Queued — nothing has been sent yet", current.TextContent);
        Assert.Contains("Closing this dialog won’t stop the analysis.", Text(cut));

        Advance(TimeSpan.FromSeconds(2), cut);
        cut.WaitForAssertion(() => Assert.Contains("Sending to Stamped Processor and extracting", cut.Find("li[aria-current='step']").TextContent));

        Advance(TimeSpan.FromSeconds(2), cut);
        cut.WaitForAssertion(() => Assert.Contains("Merchants and categories aren’t matched yet.", Text(cut)));
        Assert.Contains("Review 2 transactions", Text(cut));
        analysis.Verify(a => a.MatchAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Running_BeforeTheTransfer_SaysPreparing()
    {
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Running)));

        var cut = Render(FileAnalysisDialog.StartMode.Progress, JobId);

        cut.WaitForAssertion(() => Assert.Contains("Preparing to send to Stamped Processor", cut.Find("li[aria-current='step']").TextContent));
        Assert.DoesNotContain("nothing has been sent yet", Text(cut));
    }

    [Fact]
    public void ThePoller_FollowsRetryAfter_AndStopsOnATerminalStatus()
    {
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Queued), retryAfterSeconds: 5));
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Failed, FileAnalysisCodes.ProviderError)));

        var cut = Render(FileAnalysisDialog.StartMode.Progress, JobId);
        cut.WaitForAssertion(() => Assert.Equal(1, pollCount));

        Advance(TimeSpan.FromSeconds(4), cut);
        Assert.Equal(1, pollCount);
        Advance(TimeSpan.FromSeconds(1), cut);
        cut.WaitForAssertion(() => Assert.Equal(2, pollCount));

        Advance(TimeSpan.FromSeconds(30), cut);
        Assert.Equal(2, pollCount);
    }

    [Fact]
    public void TakingLonger_AppearsAfterSixtySecondsInThisDialog()
    {
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Queued)));

        var cut = Render(FileAnalysisDialog.StartMode.Progress, JobId);
        cut.WaitForAssertion(() => Assert.Equal(1, pollCount));
        Assert.DoesNotContain(FileAnalysisJobCopy.TakingLonger, Text(cut));

        for (var i = 0; i < 31; i++)
            Advance(TimeSpan.FromSeconds(2), cut);

        cut.WaitForAssertion(() => Assert.Contains(FileAnalysisJobCopy.TakingLonger, Text(cut)));
    }

    [Fact]
    public void ThreeTransientFailures_ShowTheHiccup_AndASuccessClearsIt_WhileA429DoesNotCount()
    {
        polls.Enqueue(Failure(HttpStatusCode.TooManyRequests));
        polls.Enqueue(Failure(HttpStatusCode.ServiceUnavailable));
        polls.Enqueue(Failure(HttpStatusCode.InternalServerError));
        polls.Enqueue(Failure(default));
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Queued)));

        var cut = Render(FileAnalysisDialog.StartMode.Progress, JobId);
        cut.WaitForAssertion(() => Assert.Equal(1, pollCount));

        Advance(TimeSpan.FromSeconds(2), cut);  // after the 429: first 5xx
        Advance(TimeSpan.FromSeconds(2), cut);  // second
        Assert.DoesNotContain(FileAnalysisJobCopy.PollHiccup, Text(cut));
        Advance(TimeSpan.FromSeconds(4), cut);  // third → hiccup
        cut.WaitForAssertion(() => Assert.Contains(FileAnalysisJobCopy.PollHiccup, Text(cut)));
        Assert.Contains(cut.FindAll("[role='status']"), e => e.TextContent.Contains(FileAnalysisJobCopy.PollHiccup, StringComparison.Ordinal));

        Advance(TimeSpan.FromSeconds(8), cut);
        cut.WaitForAssertion(() => Assert.DoesNotContain(FileAnalysisJobCopy.PollHiccup, Text(cut)));
    }

    [Fact]
    public void APoll404_IsNoLongerAvailable_WithoutRoleAlert()
    {
        polls.Enqueue(Failure(HttpStatusCode.NotFound));

        var cut = Render(FileAnalysisDialog.StartMode.Progress, JobId);

        cut.WaitForAssertion(() => Assert.Contains("Nothing left to resume", Text(cut)));
        Assert.Empty(cut.FindAll(".fan-content [role='alert']"));
    }

    [Fact]
    public void AJobForAnotherFile_IsDropped()
    {
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Queued, accountFileId: Guid.NewGuid())));

        var cut = Render(FileAnalysisDialog.StartMode.Progress, JobId);

        cut.WaitForAssertion(() => Assert.Contains("Something went wrong", Text(cut)));
        Advance(TimeSpan.FromSeconds(10), cut);
        Assert.Equal(1, pollCount);
    }

    // ── Terminal copy ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FileAnalysisDocumentSent.No, "Your document was not sent.")]
    [InlineData(FileAnalysisDocumentSent.Yes, "Your document was sent to Stamped Processor.")]
    [InlineData(FileAnalysisDocumentSent.Possibly, "Your document may have been sent to Stamped Processor.")]
    public void AFailedJob_ShowsItsCodeCopy_AndTheSentLine_AndNoServerText(FileAnalysisDocumentSent sent, string line)
    {
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Failed, FileAnalysisCodes.Interrupted, sent)));

        var cut = Render(FileAnalysisDialog.StartMode.Result, JobId);

        cut.WaitForAssertion(() => Assert.Contains("The analysis stopped", cut.Find("h3").TextContent));
        Assert.Equal(line, cut.Find(".fan-sent").TextContent.Trim());
        Assert.DoesNotContain(ServerText, Text(cut));
        var describedBy = cut.Find("h3").GetAttribute("aria-describedby")!.Split(' ');
        Assert.Equal(2, describedBy.Length);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Try again", StringComparison.Ordinal));
    }

    [Fact]
    public void ACancelledJob_ShowsItsCodeCopy_AndOnlyClose()
    {
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Cancelled, FileAnalysisCodes.FileChanged, processor: null)));

        var cut = Render(FileAnalysisDialog.StartMode.Result, JobId);

        cut.WaitForAssertion(() => Assert.Contains("The file was changed or removed after you started the analysis.", Text(cut)));
        Assert.Equal("Your document was not sent.", cut.Find(".fan-sent").TextContent.Trim());
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Try again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryAgain_ReturnsToAnUntickedConsent_WithTheSecondTransferAlert()
    {
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Failed, FileAnalysisCodes.ProviderError, FileAnalysisDocumentSent.Possibly)));
        var cut = Render(FileAnalysisDialog.StartMode.Result, JobId);
        cut.WaitForAssertion(() => Assert.Contains("We couldn’t read this statement", Text(cut)));

        var retry = cut.FindAll("button").Single(b => b.TextContent.Contains("Try again", StringComparison.Ordinal));
        await cut.InvokeAsync(() => retry.Click());

        cut.WaitForAssertion(() => Assert.Contains("Analyzing again sends the document to Today's Processor again.", Text(cut)));
        Assert.False(cut.FindComponent<FileAnalysisConsentPanel>().Instance.Checked);
    }

    // ── The POST ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A202_StartsPollingTheReturnedJob()
    {
        accounts.Setup(a => a.AnalyzeFileAsync(AccountId, FileId, It.IsAny<AnalyzeFileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<AnalyzeFileResponse>.Success(new AnalyzeFileResponse(JobId), HttpStatusCode.Accepted));
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Queued)));
        var cut = Render(FileAnalysisDialog.StartMode.Consent);

        await SendFromConsentAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("Queued — nothing has been sent yet", Text(cut)));
        accounts.Verify(a => a.AnalyzeFileAsync(AccountId, FileId,
            It.Is<AnalyzeFileRequest>(r => r.ConsentText!.EndsWith(FileAnalysisConsent.BackgroundSentence, StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A409InProgress_AttachesToTheOpenJob()
    {
        accounts.Setup(a => a.AnalyzeFileAsync(AccountId, FileId, It.IsAny<AnalyzeFileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<AnalyzeFileResponse>.Failure(HttpStatusCode.Conflict,
                Problem($$"""{"status":409,"code":"analysis_in_progress","analysisJobId":"{{JobId}}"}""")));
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Running, transferStarted: true)));
        var cut = Render(FileAnalysisDialog.StartMode.Consent);

        await SendFromConsentAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("Sending to Stamped Processor and extracting", Text(cut)));
        analysis.Verify(a => a.GetJobAsync(JobId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task A422TooLarge_ShowsTheServerLimit()
    {
        accounts.Setup(a => a.AnalyzeFileAsync(AccountId, FileId, It.IsAny<AnalyzeFileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<AnalyzeFileResponse>.Failure(HttpStatusCode.UnprocessableEntity,
                Problem("""{"status":422,"code":"file_too_large_for_analysis","limitBytes":33554432,"detail":"SERVER-SUPPLIED TEXT THAT MUST NEVER RENDER"}""")));
        var cut = Render(FileAnalysisDialog.StartMode.Consent);

        await SendFromConsentAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("This file is too large to analyze", Text(cut)));
        Assert.Contains("The limit is 32 MB.", Text(cut));
        Assert.DoesNotContain(ServerText, Text(cut));
    }

    [Theory]
    [InlineData(null, "Analysis is temporarily unavailable")]
    [InlineData("feature_disabled", "Analysis isn’t available")]
    public async Task A503_IsDisabledOnlyWithItsCode(string? code, string heading)
    {
        var json = code is null ? """{"status":503}""" : $$"""{"status":503,"code":"{{code}}"}""";
        accounts.Setup(a => a.AnalyzeFileAsync(AccountId, FileId, It.IsAny<AnalyzeFileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<AnalyzeFileResponse>.Failure(HttpStatusCode.ServiceUnavailable, Problem(json)));
        var cut = Render(FileAnalysisDialog.StartMode.Consent);

        await SendFromConsentAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains(heading, cut.Find("h3").TextContent));
    }

    [Fact]
    public async Task ANetworkError_SaysItCouldNotConfirm_WithNoSentLine()
    {
        accounts.Setup(a => a.AnalyzeFileAsync(AccountId, FileId, It.IsAny<AnalyzeFileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<AnalyzeFileResponse>.Failure(new HttpRequestException(ServerText)));
        var cut = Render(FileAnalysisDialog.StartMode.Consent);

        await SendFromConsentAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("We couldn’t confirm the analysis started", Text(cut)));
        Assert.Empty(cut.FindAll(".fan-sent"));
        Assert.DoesNotContain(ServerText, Text(cut));
    }

    [Fact]
    public async Task ClosingWhileSubmitting_StartsNoPoller_WhenThe202LandsLater()
    {
        // The host keeps a closed dialog mounted until its next open re-keys it, so a 202 that lands after Close
        // must not start polling — a Completed poll would run the match step from a dialog nobody sees.
        var pending = new TaskCompletionSource<ApiResult<AnalyzeFileResponse>>();
        accounts.Setup(a => a.AnalyzeFileAsync(AccountId, FileId, It.IsAny<AnalyzeFileRequest>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Completed)));
        var cut = Render(FileAnalysisDialog.StartMode.Consent);

        await SendFromConsentAsync(cut);
        var close = cut.FindAll("button").First(b => b.TextContent.Trim() == "Close");
        await cut.InvokeAsync(() => close.Click());
        await cut.InvokeAsync(() => pending.SetResult(
            ApiResult<AnalyzeFileResponse>.Success(new AnalyzeFileResponse(JobId), HttpStatusCode.Accepted)));
        Advance(TimeSpan.FromSeconds(30), cut);

        analysis.Verify(a => a.GetJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        analysis.Verify(a => a.MatchAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submitting_KeepsOneBusyButton_AndASecondClickSendsNothing()
    {
        var pending = new TaskCompletionSource<ApiResult<AnalyzeFileResponse>>();
        accounts.Setup(a => a.AnalyzeFileAsync(AccountId, FileId, It.IsAny<AnalyzeFileRequest>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        var cut = Render(FileAnalysisDialog.StartMode.Consent);

        await SendFromConsentAsync(cut);
        var busy = cut.FindAll("button").Single(b => b.TextContent.Contains("Starting", StringComparison.Ordinal));
        Assert.Equal("true", busy.GetAttribute("aria-busy"));
        Assert.Equal("true", busy.GetAttribute("aria-disabled"));
        Assert.False(busy.HasAttribute("disabled"));
        await cut.InvokeAsync(() => busy.Click());

        accounts.Verify(a => a.AnalyzeFileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<AnalyzeFileRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        pending.SetResult(ApiResult<AnalyzeFileResponse>.Failure(new HttpRequestException("x")));
    }

    [Fact]
    public void TheTitle_IsAnH2()
    {
        polls.Enqueue(Ok(Job(FileAnalysisJobStatus.Queued)));
        var cut = Render(FileAnalysisDialog.StartMode.Progress, JobId);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("h2.ods-modal-title")));
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(PermissionClaims.Type, PermissionClaims.FileAnalysisCreate)], "test"))));
    }

    /// <summary>The dialog inside the providers its modal and popovers need.</summary>
    public sealed class Host : ComponentBase
    {
        [Parameter] public FileAnalysisDialog.StartMode Start { get; set; }

        [Parameter] public Guid? JobId { get; set; }

        [Parameter] public FileAnalysisDocumentSent? Prior { get; set; }

        [Parameter] public bool AlreadyRunning { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<FileAnalysisDialog>(2);
            builder.AddComponentParameter(3, nameof(FileAnalysisDialog.Open), true);
            builder.AddComponentParameter(4, nameof(FileAnalysisDialog.AccountId), AccountId);
            builder.AddComponentParameter(5, nameof(FileAnalysisDialog.FileId), FileId);
            builder.AddComponentParameter(6, nameof(FileAnalysisDialog.AccountFileId), AccountFileId);
            builder.AddComponentParameter(7, nameof(FileAnalysisDialog.FileName), "statement.pdf");
            builder.AddComponentParameter(8, nameof(FileAnalysisDialog.Start), Start);
            builder.AddComponentParameter(9, nameof(FileAnalysisDialog.JobId), JobId);
            builder.AddComponentParameter(10, nameof(FileAnalysisDialog.PriorDocumentSent), Prior);
            builder.AddComponentParameter(11, nameof(FileAnalysisDialog.AnnounceAlreadyRunning), AlreadyRunning);
            builder.CloseComponent();
        }
    }
}

/// <summary>Serialises the classes that flip <see cref="FileAnalysisDialog.InteractiveCheck"/>, a process-wide seam.</summary>
[CollectionDefinition(Name)]
public sealed class FileAnalysisDialogCollection
{
    public const string Name = "FileAnalysisDialog";
}
