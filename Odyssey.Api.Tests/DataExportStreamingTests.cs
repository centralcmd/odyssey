using System.Linq.Expressions;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Api.DataExport;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The streaming contract for the admin data export (issue #395). The endpoint used to materialize
/// every table into one <see cref="DataExportDocument"/> and then serialize that to a <c>byte[]</c>,
/// holding the whole database in managed memory twice before the first byte reached the client. It now
/// writes rows to the response as it reads them, which buys flat memory at the cost of two things
/// these tests pin down:
/// <list type="bullet">
/// <item>the bytes must still be exactly what the serializer would have produced, or every existing
/// reader of the format breaks;</item>
/// <item>a failure past the first byte can no longer be a ProblemDetails, so the document carries a
/// terminal <c>complete</c> sentinel that is absent from a truncated one.</item>
/// </list>
/// </summary>
public class DataExportStreamingTests
{
    private const string ActorUserId = "data-export-streaming-actor";
    private const string ExportPath = "/api/admin/data-export";

    // ── Byte-for-byte parity with the buffered serializer ─────────────────────

    /// <summary>
    /// Round-trip proof: the streamed bytes deserialize into a <see cref="DataExportDocument"/> that
    /// re-serializes to the identical bytes. That pins property names, casing, order, indentation and
    /// number/date formatting to what <see cref="JsonSerializer"/> produces for the same document —
    /// which is exactly what the buffered implementation emitted.
    /// </summary>
    [Fact]
    public async Task WriteExport_EmitsTheSameBytesTheSerializerWouldProduce()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await DataExportApiTests.SeedFinanceAsync(factory);

        var streamed = await WriteExportAsync(factory);

        var document = JsonSerializer.Deserialize<DataExportDocument>(
            streamed, DataExportService.ExportJsonOptions);
        Assert.NotNull(document);
        var reserialized = JsonSerializer.SerializeToUtf8Bytes(document, DataExportService.ExportJsonOptions);

        // Compared as text so a mismatch is readable rather than a byte-array dump.
        Assert.Equal(Encoding.UTF8.GetString(reserialized), Encoding.UTF8.GetString(streamed));
    }

    // ── Flat memory: the payload never exists as one buffer ───────────────────

    /// <summary>
    /// With enough rows to exceed the writer's flush threshold several times over, the output must
    /// reach the stream in many small chunks. A single write of the whole payload is the signature of
    /// the buffered implementation this replaced.
    /// </summary>
    [Fact]
    public async Task WriteExport_FlushesIncrementally_RatherThanInOneBuffer()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedManyAccountsAsync(factory, count: 600);

        var recording = new RecordingStream();
        await WriteExportAsync(factory, recording);

        Assert.True(recording.Length > 64 * 1024,
            $"the fixture must be big enough to force several flushes; it produced {recording.Length} bytes");
        Assert.True(recording.WriteCount > 4,
            $"expected the payload to arrive in many chunks, got {recording.WriteCount} write(s)");

        // No single write may carry a large fraction of the payload — that would mean the whole
        // serialized document had been buffered before anything was handed to the stream.
        Assert.True(recording.LargestWrite < recording.Length / 4,
            $"largest single write was {recording.LargestWrite} of {recording.Length} bytes");
    }

    // ── Failure past the first byte is detectable ─────────────────────────────

    [Fact]
    public async Task WriteExport_WhenTheStreamFailsMidWrite_LeavesNoCompletenessSentinel()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedManyAccountsAsync(factory, count: 600);

        var failing = new RecordingStream { FailAfterWrites = 2 };

        await Assert.ThrowsAsync<IOException>(() => WriteExportAsync(factory, failing));

        var partial = Encoding.UTF8.GetString(failing.Written);
        Assert.NotEmpty(partial);
        Assert.DoesNotContain("\"complete\"", partial, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteExport_WhenTheClientCancelsMidStream_LeavesNoCompletenessSentinel()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedManyAccountsAsync(factory, count: 600);

        using var cancellation = new CancellationTokenSource();
        var cancelling = new RecordingStream { CancelAfterWrites = 2, Cancellation = cancellation };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WriteExportAsync(factory, cancelling, cancellation.Token));

        var partial = Encoding.UTF8.GetString(cancelling.Written);
        Assert.NotEmpty(partial);
        Assert.DoesNotContain("\"complete\"", partial, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other end of the sentinel contract (issue #401). <c>DataExportApiClient</c> decides whether
    /// a download is whole by scanning the payload's tail for <c>"complete": true</c> rather than
    /// parsing the whole database, which assumes the sentinel is the document's last property. That
    /// assumption is about *this* writer's output, so it is pinned here against real streamed bytes —
    /// move <c>Complete</c> off the end and this fails rather than silently costing the client its
    /// only truncation check.
    /// </summary>
    [Fact]
    public async Task WriteExport_EmitsATailTheClientAcceptsAsComplete()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await DataExportApiTests.SeedFinanceAsync(factory);

        var streamed = await WriteExportAsync(factory);

        Assert.True(Odyssey.ApiClient.Resources.DataExportApiClient.HasCompletenessSentinel(streamed),
            $"the client rejected a whole export; its tail was: {Tail(streamed)}");

        // And the truncated case the client must catch.
        var failing = new RecordingStream { FailAfterWrites = 2 };
        await SeedManyAccountsAsync(factory, count: 600);
        await Assert.ThrowsAsync<IOException>(() => WriteExportAsync(factory, failing));

        Assert.False(Odyssey.ApiClient.Resources.DataExportApiClient.HasCompletenessSentinel(failing.Written));
    }

    private static string Tail(byte[] payload) =>
        Encoding.UTF8.GetString(payload, Math.Max(0, payload.Length - 64), Math.Min(64, payload.Length));

    // ── Failing over real HTTP, on both sides of the first flush ──────────────

    /// <summary>
    /// The attachment headers are now set before the export runs, which raises the question of what a
    /// failure in that window labels the error body as. It labels it as nothing: ASP.NET Core's
    /// <c>ExceptionHandlerMiddleware</c> clears status and headers before invoking
    /// <c>GlobalExceptionHandler</c>, so the ProblemDetails goes out clean. Pinned here because the
    /// alternative — a browser saving a 500 body under the export's filename as though the download
    /// had succeeded — is silent, and the wiring that prevents it lives outside this controller.
    ///
    /// The export is well under the writer's 32 KB flush threshold, so this genuinely exercises the
    /// not-yet-started path rather than the truncation path below.
    /// </summary>
    [Fact]
    public async Task Export_FailingBeforeTheResponseStarts_ReturnsCleanProblemDetails()
    {
        var logger = new CapturingLogger<DataExportController>();
        await using var factory = new ApiFactory([PermissionClaims.DataExport], services =>
        {
            services.AddSingleton<ILogger<DataExportController>>(logger);
            services.AddScoped(ContextFailingOnAccounts);
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ExportPath);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        // The export's own headers must not survive onto the error body.
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.DoesNotContain(response.Content.Headers, h =>
            h.Key.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("Data export failed", entry.Message, StringComparison.Ordinal);
        Assert.Contains("nothing written", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other side of the same coin, and the case the <c>complete</c> sentinel exists for: enough
    /// accounts to push past the flush threshold before the (poisoned) Contacts query fails, so the
    /// response has already started and no error document can replace what is on the wire. The client
    /// is left with a truncated body — detectable only by the missing sentinel.
    /// </summary>
    [Fact]
    public async Task Export_FailingAfterTheResponseStarts_TruncatesWithoutTheSentinel()
    {
        var logger = new CapturingLogger<DataExportController>();
        await using var factory = new ApiFactory([PermissionClaims.DataExport], services =>
        {
            services.AddSingleton<ILogger<DataExportController>>(logger);
            services.AddScoped(ContextFailingOnContacts);
        });
        await SeedManyAccountsAsync(factory, count: 600);
        using var client = factory.CreateClient();

        var body = await ReadBodyIgnoringTruncationAsync(client);

        Assert.NotEmpty(body);
        Assert.Contains("\"accounts\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"complete\"", body, StringComparison.Ordinal);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("response already started", entry.Message, StringComparison.Ordinal);
    }

    // ── The controller's logging survives the move to streaming ───────────────

    /// <summary>
    /// The success log used to read its row counts off the materialized document. Streaming means
    /// counting as rows are written, so the counts have to come back from the writer — and they still
    /// have to be there.
    /// </summary>
    [Fact]
    public async Task Export_SuccessLog_ReportsBytesElapsedAndRowCounts()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await DataExportApiTests.SeedFinanceAsync(factory);

        var logger = new CapturingLogger<DataExportController>();
        using var output = new MemoryStream();
        await InvokeControllerAsync(factory, logger, output, CancellationToken.None);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("Data export succeeded", message, StringComparison.Ordinal);
        Assert.Contains($"{output.Length} bytes", message, StringComparison.Ordinal);

        // Per-table counts, keyed as before by the CLR table name. The counted *values* matter as much
        // as the keys: this PR replaced List.Count on a materialized collection with a counter
        // incremented while streaming, so an off-by-one or a reset between tables would be invisible
        // if only the key names were asserted. The fixture seeds three accounts and one of most others.
        Assert.Contains("[Accounts, 3]", message, StringComparison.Ordinal);
        Assert.Contains("[AccountTerms, 1]", message, StringComparison.Ordinal);
        Assert.Contains("[Transactions, 1]", message, StringComparison.Ordinal);
        Assert.Contains("[AccountFiles, 1]", message, StringComparison.Ordinal);
        // Nothing seeded for this one — a table that streams zero rows must still report zero.
        Assert.Contains("[TransactionFiles, 0]", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #382 point 1: a client that walks away mid-download is an informational event, not an
    /// error. The response is already partly written by then, which is precisely the case that used to
    /// be impossible.
    /// </summary>
    [Fact]
    public async Task Export_WhenTheClientCancelsMidStream_LogsCancellationNotAnError()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedManyAccountsAsync(factory, count: 600);

        var logger = new CapturingLogger<DataExportController>();
        using var cancellation = new CancellationTokenSource();
        var cancelling = new RecordingStream { CancelAfterWrites = 2, Cancellation = cancellation };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeControllerAsync(factory, logger, cancelling, cancellation.Token));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("cancelled by client", entry.Message, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A live context on the same in-memory store that fails the moment the export reaches
    /// <see cref="Account"/> — the first table it writes, so nothing has been flushed yet.
    /// </summary>
    /// <remarks>
    /// This used to be a plain disposed context, which failed the first query whatever it was. That
    /// stopped working when identity moved into <see cref="OdysseyContext"/>: the scoped context is now
    /// the one <c>PasswordChangeRequiredMiddleware</c> reads too, so a disposed one threw in the
    /// middleware and the request never reached the controller — the export's own error log, which is
    /// what this test is about, never ran. Poisoning one entity type keeps the failure where the test
    /// means it to be.
    /// </remarks>
    private static OdysseyContext ContextFailingOnAccounts(IServiceProvider services) =>
        PoisonedOn<Account>(services);

    /// <summary>
    /// A live context on the same in-memory store that fails only when the export reaches Contacts —
    /// the fifth table, and the one the truncation case needs, since everything before it must succeed
    /// and push the response past the flush threshold.
    /// </summary>
    private static OdysseyContext ContextFailingOnContacts(IServiceProvider services) =>
        PoisonedOn<Contact>(services);

    /// <remarks>
    /// The <see cref="PoisonedContext{TEntity}"/> subclass is load-bearing, not decoration. The
    /// compiled-query cache is keyed partly on the context type, and <c>QueryCompilationStarting</c>
    /// fires only on a cache miss — so on the shared <see cref="OdysseyContext"/> the interceptor is
    /// skipped entirely whenever a sibling test in this class has already compiled the same query, and
    /// the test passes alone but fails in the run. Its own type gives it its own cache entries, and the
    /// generic parameter gives each poisoned entity type a distinct one.
    /// </remarks>
    private static OdysseyContext PoisonedOn<TEntity>(IServiceProvider services) =>
        new PoisonedContext<TEntity>(new DbContextOptionsBuilder<OdysseyContext>(
                services.GetRequiredService<DbContextOptions<OdysseyContext>>())
            .AddInterceptors(new FailOnQueriesOf(typeof(TEntity)))
            .Options);

    private sealed class PoisonedContext<TEntity>(DbContextOptions<OdysseyContext> options) : OdysseyContext(options);

    private sealed class FailOnQueriesOf(Type entityType) : IQueryExpressionInterceptor
    {
        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData) =>
            RootFinder.Queries(queryExpression, entityType)
                ? throw new InvalidOperationException($"Poisoned {entityType.Name} query.")
                : queryExpression;

        /// <summary>
        /// The query root is an <see cref="EntityQueryRootExpression"/>, which renders as its type name
        /// rather than the entity's — so the entity type has to be read off the node, not off
        /// <c>ToString()</c>.
        /// </summary>
        private sealed class RootFinder(Type entityType) : ExpressionVisitor
        {
            private bool found;

            public static bool Queries(Expression expression, Type entityType)
            {
                var finder = new RootFinder(entityType);
                finder.Visit(expression);
                return finder.found;
            }

            public override Expression? Visit(Expression? node)
            {
                if (node is EntityQueryRootExpression { EntityType.ClrType: var clrType } && clrType == entityType)
                {
                    found = true;
                }

                return base.Visit(node);
            }
        }
    }

    /// <summary>
    /// Reads a response whose body is expected to stop mid-stream. The server aborts the connection
    /// rather than completing it, so the read itself throws once the truncation is reached — the bytes
    /// that did arrive are still the thing under test.
    /// </summary>
    private static async Task<string> ReadBodyIgnoringTruncationAsync(HttpClient client)
    {
        using var response = await client.GetAsync(ExportPath, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();

        try
        {
            await stream.CopyToAsync(buffer);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            // Expected: the export failed part-way and the connection was aborted.
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<byte[]> WriteExportAsync(ApiFactory factory)
    {
        using var output = new MemoryStream();
        await WriteExportAsync(factory, output);
        return output.ToArray();
    }

    private static async Task WriteExportAsync(
        ApiFactory factory, Stream output, CancellationToken cancellationToken = default)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DataExportService>();
        await service.WriteExportAsync(output, service.CreateHeader(ActorUserId), cancellationToken);
    }

    /// <summary>
    /// Drives <see cref="DataExportController.Export"/> over a synthetic <see cref="HttpContext"/> so
    /// the response body is a stream the test controls. Going through the real HTTP pipeline cannot
    /// fail a write at a chosen offset, and that offset is the whole point of these cases.
    /// </summary>
    private static async Task InvokeControllerAsync(
        ApiFactory factory,
        CapturingLogger<DataExportController> logger,
        Stream responseBody,
        CancellationToken cancellationToken)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DataExportService>();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, ActorUserId)], "Test")),
        };
        httpContext.Response.Body = responseBody;

        var controller = new DataExportController(service, logger)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        await controller.Export(cancellationToken);
    }

    private static async Task SeedManyAccountsAsync(ApiFactory factory, int count)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        for (var index = 0; index < count; index++)
        {
            context.Accounts.Add(new Account
            {
                AccountId = Guid.NewGuid(),
                Name = $"Account {index:D4}",
                Description = $"Streaming fixture account number {index:D4}",
                Opened = DateTime.UtcNow,
            });
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Records every write so a test can see how the payload was chunked, and can fail or cancel the
    /// stream at a chosen write.
    /// </summary>
    private sealed class RecordingStream : Stream
    {
        private readonly MemoryStream written = new();

        public int WriteCount { get; private set; }

        public int LargestWrite { get; private set; }

        public byte[] Written => written.ToArray();

        /// <summary>Throw an <see cref="IOException"/> once this many writes have succeeded.</summary>
        public int? FailAfterWrites { get; init; }

        /// <summary>Signal <see cref="Cancellation"/> once this many writes have succeeded.</summary>
        public int? CancelAfterWrites { get; init; }

        public CancellationTokenSource? Cancellation { get; init; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written.Length;

        public override long Position
        {
            get => written.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Record(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Record(ReadOnlySpan<byte> buffer)
        {
            if (FailAfterWrites is { } failAfter && WriteCount >= failAfter)
            {
                throw new IOException("Simulated mid-stream write failure.");
            }

            written.Write(buffer);
            WriteCount++;
            LargestWrite = Math.Max(LargestWrite, buffer.Length);

            if (CancelAfterWrites is { } cancelAfter && WriteCount >= cancelAfter)
            {
                Cancellation?.Cancel();
            }
        }
    }

    private sealed class ApiFactory(
        IReadOnlyCollection<string>? permissions,
        Action<IServiceCollection>? configureServices = null)
        : OdysseyApiFactory(permissions, ActorUserId, configureServices: configureServices);
}
