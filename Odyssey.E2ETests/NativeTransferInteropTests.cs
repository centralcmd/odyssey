using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// The browser-native upload's interop (issue #517 §3 step 4, AC 7), driven in a real browser against a test server —
/// no application stack. <c>window.odysseyNativeTransfer</c> is lifted verbatim out of the client's
/// <c>index.html</c>, so this exercises the script the app ships: it posts the form fields beside the file, calls the
/// <c>onUploadComplete</c> callback exactly once after the body is sent and before the response, releases the callback
/// on success, error and abort, and resolves <c>aborted</c>/<c>uploadComplete</c> as the dialog's wording needs.
/// </summary>
/// <remarks>
/// The only prerequisite is a browser. Without one the tests skip, unless <c>ODYSSEY_REQUIRE_TIER</c> names
/// <c>e2e</c>. The task and journal imports call the same function with no fields and no callback; the last test is
/// theirs.
/// </remarks>
public sealed class NativeTransferInteropTests : IAsyncLifetime
{
    private const int LargeFileBytes = 48 * 1024 * 1024;

    private readonly UploadServer server = new();
    private IPlaywright? playwright;
    private IBrowser? browser;
    private string? skipReason;

    public async Task InitializeAsync()
    {
        server.Start();
        playwright = await Playwright.CreateAsync();
        browser = await LaunchAsync(playwright);
        if (browser is null)
        {
            skipReason = TestTierGate.Resolve(TestTierGate.E2E, TierProbe.PrerequisiteMissing,
                "No browser: Playwright's Chromium could not be installed and no Chrome is on this machine.");
        }
    }

    public async Task DisposeAsync()
    {
        if (browser is not null)
        {
            await browser.DisposeAsync();
        }

        playwright?.Dispose();
        server.Dispose();
    }

    private static async Task<IBrowser?> LaunchAsync(IPlaywright playwright)
    {
        if (Microsoft.Playwright.Program.Main(["install", "chromium"]) == 0)
        {
            try
            {
                return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            }
            catch (PlaywrightException)
            {
                // Fall through to an installed Chrome.
            }
        }

        try
        {
            return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Channel = "chrome" });
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    private async Task<IPage> PageWithFileAsync(int bytes)
    {
        var page = await browser!.NewPageAsync();
        await page.GotoAsync(server.Origin + "/");
        await page.EvaluateAsync("() => window.odysseyNativeTransfer.watch('box')");
        await page.Locator("#picker").SetInputFilesAsync(new FilePayload
        {
            Name = "calendar.ics",
            MimeType = "text/calendar",
            Buffer = Encoding.ASCII.GetBytes(new string('A', bytes)),
        });
        return page;
    }

    /// <summary>Starts an upload in the page; the result lands in <c>window.result</c>.</summary>
    private static Task StartAsync(IPage page, string path, bool withFields, bool withCallback) =>
        page.EvaluateAsync(
            """
            ([path, withFields, withCallback]) => {
                // Records the timeout each request is SENT with: the browser must never cut an upload off.
                if (!window.__sendWrapped) {
                    const send = XMLHttpRequest.prototype.send;
                    XMLHttpRequest.prototype.send = function (body) { window.sentTimeout = this.timeout; return send.call(this, body); };
                    window.__sendWrapped = true;
                }
                window.sentTimeout = null;
                window.cb = { calls: 0, disposed: 0, respondedAtCall: null,
                    invokeMethodAsync(name) { if (name === 'OnUploadComplete') { this.calls++; this.respondedAtCall = !!window.result; } return Promise.resolve(); },
                    dispose() { this.disposed++; } };
                window.result = null;
                window.odysseyNativeTransfer.upload('box', path, 'file', 'X-XSRF-TOKEN', 'xsrf', 'text/calendar',
                        withFields ? { allowRemovals: 'true' } : null, withCallback ? window.cb : null)
                    .then(r => { window.result = r; });
            }
            """,
            new object[] { path, withFields, withCallback });

    private static async Task<UploadResult> ResultAsync(IPage page)
    {
        await page.WaitForFunctionAsync("() => window.result !== null", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
        return await page.EvaluateAsync<UploadResult>("() => window.result");
    }

    private static Task<CallbackState> CallbackAsync(IPage page) => page.EvaluateAsync<CallbackState>("() => window.cb");

    [SkippableFact] // AC 7
    public async Task The_upload_sends_its_fields_and_signals_once_before_the_response()
    {
        Skip.If(skipReason is not null, skipReason);
        var page = await PageWithFileAsync(4096);
        var held = server.Hold("/hold-response");

        await StartAsync(page, server.Origin + "/hold-response", withFields: true, withCallback: true);

        var request = await held.Received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await page.WaitForFunctionAsync("() => window.cb.calls === 1");
        Assert.True(await page.EvaluateAsync<bool>("() => window.result === null"));

        held.Release(HttpStatusCode.OK, "application/json", """{"importedCount":1}""");
        var result = await ResultAsync(page);
        var callback = await CallbackAsync(page);

        Assert.Equal((200, false, true), (result.Status, result.Aborted, result.UploadComplete));
        Assert.Equal("""{"importedCount":1}""", result.Body);
        Assert.Equal(1, callback.Calls);
        Assert.False(callback.RespondedAtCall);
        Assert.Equal(1, callback.Disposed);
        Assert.Contains("name=\"allowRemovals\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("name=\"file\"; filename=\"calendar.ics\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/calendar", request.Body, StringComparison.Ordinal);
        Assert.Equal("xsrf", request.Token);
        Assert.Equal(0, await page.EvaluateAsync<int>("() => window.sentTimeout"));
    }

    [SkippableFact] // AC 7: an abort before the body has gone resolves aborted && !uploadComplete and releases the callback
    public async Task An_abort_mid_upload_reports_aborted_and_not_complete()
    {
        Skip.If(skipReason is not null, skipReason);
        var page = await PageWithFileAsync(LargeFileBytes);

        // The server never reads the body, so the browser cannot finish sending it. Abort once the request has
        // reached the server — its first bytes — rather than after a guessed delay.
        await StartAsync(page, server.Origin + "/stall", withFields: false, withCallback: true);
        await server.StallReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await page.EvaluateAsync("() => window.odysseyNativeTransfer.abort('box')");

        var result = await ResultAsync(page);
        var callback = await CallbackAsync(page);

        Assert.Equal((0, true, false), (result.Status, result.Aborted, result.UploadComplete));
        Assert.Equal(0, callback.Calls);
        Assert.Equal(1, callback.Disposed);
    }

    [SkippableFact] // AC 7 / FD5: a proxy's HTML page after the body has gone is passed through as text
    public async Task A_non_json_504_after_the_body_is_passed_through()
    {
        Skip.If(skipReason is not null, skipReason);
        var page = await PageWithFileAsync(4096);

        await StartAsync(page, server.Origin + "/gateway-timeout", withFields: false, withCallback: true);
        var result = await ResultAsync(page);
        var callback = await CallbackAsync(page);

        Assert.Equal((504, false, true), (result.Status, result.Aborted, result.UploadComplete));
        Assert.StartsWith("<html>", result.Body, StringComparison.Ordinal);
        Assert.Equal(1, callback.Calls);
        Assert.Equal(1, callback.Disposed);
    }

    [SkippableFact] // AC 7: a connection that fails is an error, not an abort, and the callback is released
    public async Task A_dropped_connection_is_status_zero_and_not_aborted()
    {
        Skip.If(skipReason is not null, skipReason);
        var page = await PageWithFileAsync(4096);

        await StartAsync(page, $"http://127.0.0.1:{UploadServer.ClosedPort()}/nothing-listens", withFields: false, withCallback: true);
        var result = await ResultAsync(page);
        var callback = await CallbackAsync(page);

        Assert.Equal(0, result.Status);
        Assert.False(result.Aborted);
        Assert.Equal(1, callback.Disposed);
    }

    [SkippableFact] // the task and journal imports: no fields, no callback, as before
    public async Task Without_fields_or_a_callback_the_upload_still_posts_the_file()
    {
        Skip.If(skipReason is not null, skipReason);
        var page = await PageWithFileAsync(4096);
        var held = server.Hold("/plain");

        await StartAsync(page, server.Origin + "/plain", withFields: false, withCallback: false);
        var request = await held.Received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        held.Release(HttpStatusCode.OK, "application/json", "{}");
        var result = await ResultAsync(page);

        Assert.Equal((200, false, true), (result.Status, result.Aborted, result.UploadComplete));
        Assert.DoesNotContain("allowRemovals", request.Body, StringComparison.Ordinal);
        Assert.Contains("filename=\"calendar.ics\"", request.Body, StringComparison.Ordinal);
    }

    public sealed class UploadResult
    {
        public int Status { get; set; }

        public string Body { get; set; } = string.Empty;

        public bool Aborted { get; set; }

        public bool UploadComplete { get; set; }
    }

    public sealed class CallbackState
    {
        public int Calls { get; set; }

        public int Disposed { get; set; }

        public bool? RespondedAtCall { get; set; }
    }

    /// <summary>
    /// A loopback server for the page and its uploads: <c>/</c> serves the client's transfer script, a held path
    /// answers only when the test releases it, <c>/stall</c> never reads its body, <c>/gateway-timeout</c> answers an
    /// HTML 504 once the body is read.
    /// </summary>
    private sealed class UploadServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Dictionary<string, HeldResponse> held = [];
        private readonly CancellationTokenSource stopping = new();

        public string Origin { get; private set; } = string.Empty;

        /// <summary>Set when a request to <c>/stall</c> has arrived — its headers, the first bytes of the upload.</summary>
        public TaskCompletionSource StallReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start()
        {
            var port = ClosedPort();
            Origin = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Origin + "/");
            listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        /// <summary>A loopback port with nothing listening on it.</summary>
        public static int ClosedPort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        public HeldResponse Hold(string path)
        {
            var response = new HeldResponse();
            lock (held)
            {
                held[path] = response;
            }

            return response;
        }

        private async Task AcceptAsync()
        {
            while (!stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception) when (stopping.IsCancellationRequested || !listener.IsListening)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            try
            {
                if (path == "/")
                {
                    await WriteAsync(context.Response, HttpStatusCode.OK, "text/html", Page());
                    return;
                }

                if (path == "/stall")
                {
                    StallReached.TrySetResult();
                    await Task.Delay(Timeout.Infinite, stopping.Token);
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.ASCII);
                var body = await reader.ReadToEndAsync();
                if (path == "/gateway-timeout")
                {
                    await WriteAsync(context.Response, HttpStatusCode.GatewayTimeout, "text/html", "<html><body>504 Gateway Time-out</body></html>");
                    return;
                }

                HeldResponse? response;
                lock (held)
                {
                    held.TryGetValue(path, out response);
                }

                if (response is null)
                {
                    await WriteAsync(context.Response, HttpStatusCode.NotFound, "text/plain", "not found");
                    return;
                }

                response.Received.TrySetResult(new ReceivedUpload(body, context.Request.Headers["X-XSRF-TOKEN"]));
                var (status, type, text) = await response.Answer.Task;
                await WriteAsync(context.Response, status, type, text);
            }
            catch (Exception)
            {
                // The page went away, or the test stopped the server.
            }
        }

        private static async Task WriteAsync(HttpListenerResponse response, HttpStatusCode status, string type, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            response.StatusCode = (int)status;
            response.ContentType = type;
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private static string Page() =>
            $"""
            <!DOCTYPE html>
            <html><body>
            <div id="box"><input type="file" id="picker" /></div>
            <script>
            {TransferScript()}
            </script>
            </body></html>
            """;

        /// <summary><c>window.odysseyNativeTransfer</c>, lifted verbatim out of the client's <c>index.html</c>.</summary>
        private static string TransferScript()
        {
            var html = File.ReadAllText(IndexHtml());
            var start = html.IndexOf("window.odysseyNativeTransfer = {", StringComparison.Ordinal);
            var end = html.IndexOf("window.revokeBlobUrl", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "window.odysseyNativeTransfer was not found in index.html.");
            return html[start..end];
        }

        private static string IndexHtml()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Odyssey.Client", "wwwroot", "index.html");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("Odyssey.Client/wwwroot/index.html was not found above the test's directory.");
        }

        public void Dispose()
        {
            stopping.Cancel();
            lock (held)
            {
                foreach (var response in held.Values)
                {
                    response.Answer.TrySetCanceled();
                }
            }

            listener.Close();
            stopping.Dispose();
        }
    }

    private sealed record ReceivedUpload(string Body, string? Token);

    private sealed class HeldResponse
    {
        public TaskCompletionSource<ReceivedUpload> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<(HttpStatusCode, string, string)> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(HttpStatusCode status, string type, string body) => Answer.TrySetResult((status, type, body));
    }
}
