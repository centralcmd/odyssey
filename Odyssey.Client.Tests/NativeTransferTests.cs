using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Odyssey.ApiClient.Auth;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The browser-native transfer the task export and import use for files too large for the WASM heap. Its result must
/// read exactly like the typed client's, so the dialog handles both paths alike — and the service worker must let its
/// download navigation reach the API.
/// </summary>
public class NativeTransferTests
{
    [Fact]
    public async Task A2xxBody_DeserializesIntoTheValue()
    {
        var result = await NativeTransfer.ToResultAsync<TaskIcsImportResult>(
            new NativeTransfer.NativeResponse(200, """{"importedCount":2,"updatedCount":1,"storedFileCount":3,"reusedFileCount":4}"""));

        Assert.True(result.IsSuccess);
        Assert.Equal((2, 1, 3, 4), (result.Value!.ImportedCount, result.Value.UpdatedCount, result.Value.StoredFileCount, result.Value.ReusedFileCount));
    }

    [Fact]
    public async Task AProblemBody_BecomesTheProblem_WithItsStatus()
    {
        var result = await NativeTransfer.ToResultAsync<TaskIcsImportResult>(
            new NativeTransfer.NativeResponse(403, """{"status":403,"title":"Forbidden","detail":"needs permission to read and upload files."}"""));

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Contains("read and upload files", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonJsonErrorBody_StillFails_WithItsStatus()
    {
        // A proxy's own 413 page is HTML, not problem+json.
        var result = await NativeTransfer.ToResultAsync<TaskIcsImportResult>(new NativeTransfer.NativeResponse(413, "<html>413</html>"));

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, result.Status);
    }

    [Fact]
    public async Task ANetworkFailure_IsAFailure_NotASuccess()
    {
        var result = await NativeTransfer.ToResultAsync<TaskIcsImportResult>(new NativeTransfer.NativeResponse(0, "Failed to fetch"));

        Assert.False(result.IsSuccess);
        Assert.Contains("Failed to fetch", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServiceWorker_NeverAnswersAnApiNavigationWithTheAppShell()
    {
        var worker = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "service-worker.published.js"));

        Assert.Contains("!new URL(event.request.url).pathname.startsWith('/api/')", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJsInterop_IsCalledOnlyThroughNativeTransfer()
    {
        var offenders = Directory.EnumerateFiles(ClientSource.Root, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(ClientSource.Root, "*.razor", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.EndsWith("NativeTransfer.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("odysseyNativeTransfer", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    // ── The calendar import's upload (issue #517 §3 step 4, AC 7) ──────────────

    private sealed class TokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"token":"xsrf"}""", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static (BunitContext Ctx, NativeTransfer Transfer) Native()
    {
        var ctx = new BunitContext();
        var services = new ServiceCollection()
            .AddSingleton(new HttpClient(new TokenHandler()) { BaseAddress = new Uri("http://api.test/") })
            .BuildServiceProvider();
        ctx.JSInterop.Setup<bool>("odysseyNativeTransfer.has", _ => true).SetResult(true);
        return (ctx, new NativeTransfer(ctx.JSInterop.JSRuntime, new AntiforgeryTokenStore(services)));
    }

    [Fact] // AC 7: the fields and the callback travel to the script, and the flags come back
    public async Task The_upload_sends_its_fields_and_callback_and_reports_the_flags()
    {
        var (ctx, transfer) = Native();
        await using var _ = ctx;
        var upload = ctx.JSInterop.Setup<NativeTransfer.NativeResponse>("odysseyNativeTransfer.upload", _ => true);
        var completed = 0;

        var call = transfer.TryUploadAsync<IcsImportResult>("box", new FakeBrowserFile("calendar.ics", 10, "text/calendar"),
            "http://api.test/api/calendars/1/ics", "text/calendar", [new("allowRemovals", "true")], () => completed++);

        var invocation = Assert.Single(ctx.JSInterop.Invocations["odysseyNativeTransfer.upload"]);
        Assert.Equal(["box", "http://api.test/api/calendars/1/ics", "file", AntiforgeryHandler.HeaderName, "xsrf", "text/calendar"],
            invocation.Arguments.Take(6));
        Assert.Equal(new Dictionary<string, string> { ["allowRemovals"] = "true" }, invocation.Arguments[6]);
        var reference = Assert.IsType<DotNetObjectReference<NativeTransfer.UploadCompleteRelay>>(invocation.Arguments[7]);

        // The script calls it once per body; a second call is ignored.
        reference.Value.OnUploadComplete();
        reference.Value.OnUploadComplete();
        Assert.Equal(1, completed);

        upload.SetResult(new NativeTransfer.NativeResponse(200, """{"importedCount":2,"removedCount":1}""", Aborted: false, UploadComplete: true));
        var outcome = await call;

        Assert.NotNull(outcome);
        Assert.True(outcome.Result.IsSuccess);
        Assert.Equal((2, 1), (outcome.Result.Value!.ImportedCount, outcome.Result.Value.RemovedCount));
        Assert.False(outcome.Aborted);
        Assert.True(outcome.UploadComplete);
        Assert.True(outcome.JsonBody);

        // Released once the call returns: a disposed reference refuses to be used.
        Assert.Throws<ObjectDisposedException>(() => reference.Value);
    }

    [Fact] // AC 7: the caller going away releases the callback at once, without stopping the upload
    public async Task Cancelling_the_token_releases_the_callback_at_once()
    {
        var (ctx, transfer) = Native();
        await using var _ = ctx;
        var upload = ctx.JSInterop.Setup<NativeTransfer.NativeResponse>("odysseyNativeTransfer.upload", _ => true);
        using var lifetime = new CancellationTokenSource();

        var call = transfer.TryUploadAsync<IcsImportResult>("box", new FakeBrowserFile("calendar.ics", 10), "u", null, null, () => { }, lifetime.Token);
        var reference = (DotNetObjectReference<NativeTransfer.UploadCompleteRelay>)ctx.JSInterop.Invocations["odysseyNativeTransfer.upload"].Single().Arguments[7]!;

        await lifetime.CancelAsync();

        Assert.Throws<ObjectDisposedException>(() => reference.Value);
        Assert.False(call.IsCompleted);
        upload.SetResult(new NativeTransfer.NativeResponse(200, "{}", Aborted: false, UploadComplete: true));
        await call;
    }

    [Fact] // AC 7 / FD5: an abort before the body finished, and a non-JSON body after it
    public async Task Abort_and_non_json_bodies_are_reported()
    {
        var (ctx, transfer) = Native();
        await using var _ = ctx;
        ctx.JSInterop.Setup<NativeTransfer.NativeResponse>("odysseyNativeTransfer.upload", _ => true)
            .SetResult(new NativeTransfer.NativeResponse(0, "The upload was stopped.", Aborted: true, UploadComplete: false));

        var stopped = await transfer.TryUploadAsync<IcsImportResult>("box", new FakeBrowserFile("calendar.ics", 10), "u", null, null, null);

        Assert.True(stopped!.Aborted);
        Assert.False(stopped.UploadComplete);
        Assert.False(stopped.Result.IsSuccess);

        Assert.False(NativeTransfer.IsJson("<html><body>504 Gateway Time-out</body></html>"));
        Assert.False(NativeTransfer.IsJson(string.Empty));
        Assert.True(NativeTransfer.IsJson("""{"status":400}"""));
    }

    [Fact] // the task and journal imports: the plain overload posts no fields and no callback
    public async Task The_plain_overload_posts_no_fields_and_no_callback()
    {
        var (ctx, transfer) = Native();
        await using var _ = ctx;
        ctx.JSInterop.Setup<NativeTransfer.NativeResponse>("odysseyNativeTransfer.upload", _ => true)
            .SetResult(new NativeTransfer.NativeResponse(200, """{"importedCount":1}"""));

        var result = await transfer.TryUploadAsync<TaskIcsImportResult>("box", new FakeBrowserFile("tasks.ics", 10), "u", "text/calendar");

        Assert.True(result!.Value.IsSuccess);
        var invocation = Assert.Single(ctx.JSInterop.Invocations["odysseyNativeTransfer.upload"]);
        Assert.Empty((IDictionary<string, string>)invocation.Arguments[6]!);
        Assert.Null(invocation.Arguments[7]);
    }

    [Fact] // AC 7: the script's contract — XHR, no timeout, a once-only callback, released on every exit, abort
    public void The_upload_script_uses_xhr_with_no_timeout_and_releases_its_callback()
    {
        var html = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "index.html"));
        var start = html.IndexOf("window.odysseyNativeTransfer = {", StringComparison.Ordinal);
        var script = html[start..html.IndexOf("window.revokeBlobUrl", start, StringComparison.Ordinal)];

        Assert.Contains("new XMLHttpRequest()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", script, StringComparison.Ordinal);
        Assert.Contains("xhr.timeout = 0;", script, StringComparison.Ordinal);
        Assert.Contains("xhr.upload.onload", script, StringComparison.Ordinal);
        Assert.Contains("invokeMethodAsync('OnUploadComplete')", script, StringComparison.Ordinal);
        Assert.Contains("callback.dispose()", script, StringComparison.Ordinal);
        Assert.Contains("xhr.onabort = function () { aborted = true;", script, StringComparison.Ordinal);
        Assert.Contains("abort: function (containerId)", script, StringComparison.Ordinal);
        Assert.Contains("uploadComplete: uploadComplete", script, StringComparison.Ordinal);
    }
}
