using Odyssey.Context;
using Odyssey.Testing.FileStorage;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

/// <summary>
/// VTODO/.ics task import/export endpoints (issue #337): what only the HTTP surface shows — claim gates, status codes,
/// headers, query binding, the transport limits and the abort of an over-cap stream — plus a happy-path round trip.
/// The import and export rules themselves are pinned at the service tier, in
/// <c>Odyssey.Core.Tests/Ics/TaskIcsServiceTests</c>.
/// </summary>
public class TaskIcsApiTests(TaskIcsApiTests.Host host) : SharedApiHostTests<TaskIcsApiTests.Host>(host)
{
    private const string ActorUserId = "task-ics-actor-id";
    private const string Path = "/api/tasks";
    private const string IcsPath = "/api/tasks/ics";

    private static readonly string[] ReadOnly = [PermissionClaims.TasksRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.TasksRead, PermissionClaims.TasksCreate, PermissionClaims.TasksUpdate,
    ];

    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.TasksRead, PermissionClaims.TasksCreate,
        PermissionClaims.TasksUpdate, PermissionClaims.FilesRead,
    ];

    private static readonly string[] ReadWriteWithFileUploads =
    [
        PermissionClaims.TasksRead, PermissionClaims.TasksCreate,
        PermissionClaims.TasksUpdate, PermissionClaims.FilesRead, PermissionClaims.FilesCreate,
    ];

    private static readonly byte[] NoteBytes = Encoding.UTF8.GetBytes("Packing list: tent, stove, map.\n");

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Export_EmptyBoard_ReturnsValidVCalendar_WithNoSniff()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadOnly);

        var response = await client.GetAsync(IcsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join("", v) : null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.Contains("END:VCALENDAR", body);
        Assert.DoesNotContain("BEGIN:VTODO", body);
    }

    [Fact]
    public async Task Export_FileName_IsTimestampPrefixed()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadOnly);

        var response = await client.GetAsync(IcsPath);

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(fileName);
        Assert.Matches(@"^odyssey-tasks-\d{8}-\d{6}Z\.ics$", fileName!);
    }

    [Fact]
    public async Task Export_WithoutReadClaim_ReturnsForbidden()
    {
        var factory = Factory;
        using var client = factory.ClientFor([]);

        var response = await client.GetAsync(IcsPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Export_ByIds_WithAllStatuses_ExportsArchivedTask()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var archived = await CreateAsync(client, new NewJournalTask { Title = "Archived one", Status = JournalTaskStatus.Archived });

        // The row menu pairs ids with all statuses so an archived task still exports.
        var url = $"{IcsPath}?statuses=Backlog&statuses=Doing&statuses=Done&statuses=Archived&ids={archived.JournalTaskId}";
        var body = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        Assert.Equal(1, CountOccurrences(body, "BEGIN:VTODO"));
        Assert.Contains(archived.ExternalUid, body);
        Assert.Contains("STATUS:CANCELLED", body);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_HandAuthoredVTodos_CreatesTasks_WithVerbatimUid()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(
            Vtodo("urn:uuid:aaaa1111-0000-0000-0000-000000000001", "SUMMARY:One", "STATUS:NEEDS-ACTION"),
            Vtodo("urn:uuid:aaaa1111-0000-0000-0000-000000000002", "SUMMARY:Two", "STATUS:IN-PROCESS"));

        var result = await ImportAsync(client, ics);

        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(result.Skipped);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var rows = await ctx.JournalTasks.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ExternalUid == "urn:uuid:aaaa1111-0000-0000-0000-000000000001");
    }

    [Fact]
    public async Task Import_ExportedFile_IsIdempotent_UpdatesInPlace()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await CreateAsync(client, new NewJournalTask { Title = "Alpha" });
        await CreateAsync(client, new NewJournalTask { Title = "Beta", Status = JournalTaskStatus.Doing });

        var exported = await (await client.GetAsync(IcsPath)).Content.ReadAsStringAsync();
        var result = await ImportAsync(client, exported);

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(2, result.UpdatedCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(2, await ctx.JournalTasks.CountAsync()); // no duplicates
    }

    [Fact]
    public async Task Import_MalformedFile_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var response = await PostIcsAsync(client, "this is not an ics file");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_NonIcsExtension_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("x1", "SUMMARY:x"));
        var response = await PostIcsAsync(client, ics, "data.txt");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_WrongContentType_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("x2", "SUMMARY:x"));
        var response = await PostIcsAsync(client, ics, "data.ics", contentType: "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_OctetStreamContentType_IsAccepted()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("o1", "SUMMARY:Octet"));
        var response = await PostIcsAsync(client, ics, "tasks.ics", contentType: "application/octet-stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Import_WithOnlyCreateClaim_ReturnsForbidden()
    {
        var factory = Factory;
        using var client = factory.ClientFor([PermissionClaims.TasksCreate, PermissionClaims.TasksRead]);

        var ics = Vcalendar(Vtodo("p1", "SUMMARY:x"));
        var response = await PostIcsAsync(client, ics);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------ ExternalUid on the direct API

    // ------------------------------------------------------------------ import position (AC #14) + link diffing

    // ------------------------------------------------------------------ attached files travel inline

    [Fact]
    public async Task Export_WithAttachedFile_WithoutFilesRead_IsForbidden_AndSoIsTheSummary()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);
        var fileId = await SeedFileAsync(factory, "note.txt", "text/plain", NoteBytes);
        await CreateAsync(client, new NewJournalTask { Title = "With file", AttachmentFileIds = [fileId] });

        var readOnly = Factory;
        using var readOnlyClient = readOnly.ClientFor(ReadOnly);

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath + "/summary")).StatusCode);
    }

    [Fact]
    public async Task Export_WithoutAttachedFiles_NeedsOnlyTasksRead()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await CreateAsync(client, new NewJournalTask { Title = "Plain" });

        var readOnly = Factory;
        using var readOnlyClient = readOnly.ClientFor(ReadOnly);

        Assert.Equal(HttpStatusCode.OK, (await readOnlyClient.GetAsync(IcsPath)).StatusCode);
    }

    [Theory]
    [InlineData(PermissionClaims.FilesCreate)]
    [InlineData(PermissionClaims.FilesRead)]
    public async Task Import_InlineFile_WithoutBothFileClaims_IsForbidden_NothingImported(string missing)
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFileUploads.Where(c => c != missing).ToArray());

        var response = await PostIcsAsync(client, Vcalendar(Vtodo("f1", "SUMMARY:Gated", InlineAttach(NoteBytes, "note.txt", "text/plain"))));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await ctx.JournalTasks.CountAsync());
        Assert.Empty(await FilesAsync(factory));
    }

    [Fact]
    public async Task Export_ThatGrowsPastTheByteCapWhileStreaming_IsAborted_NotTruncated()
    {
        // The pre-flight check judges attached files exactly, but task text only as it is written. Text alone over the
        // cap must end the response abnormally, so a browser reports a failed download instead of saving part of it.
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await SetSettingAsync(factory, SystemSettingsKeys.TaskIcsMaxExportMegabytes, "1");
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            for (var i = 0; i < 400; i++)
            {
                context.JournalTasks.Add(new JournalTask
                {
                    ExternalUid = $"bulk-{i}",
                    Title = $"Bulk {i}",
                    Content = new string('x', 4000),
                    Position = i,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }

            await context.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(IcsPath + "/summary")).StatusCode); // nothing to judge up front
        var failure = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.GetAsync(IcsPath);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"The export completed ({body.Length} chars) instead of being aborted.");
        });

        Assert.True(failure is HttpRequestException or IOException or OperationCanceledException, failure?.ToString());
    }

    [Fact]
    public async Task Import_InlineFile_LargerThanTheImportCap_IsRefusedWhole()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFileUploads);
        await SetSettingAsync(factory, SystemSettingsKeys.TaskIcsMaxImportMegabytes, "1");

        var response = await PostIcsAsync(client, Vcalendar(Vtodo("b1", "SUMMARY:Big", InlineAttach(new byte[1024 * 1024], "big.txt", "text/plain"))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await FilesAsync(factory));
    }

    // ------------------------------------------------------------------ helpers

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Vcalendar(params string[] todos) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(todos) + "END:VCALENDAR\r\n";

    private static string Vtodo(string uid, params string[] lines) =>
        "BEGIN:VTODO\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VTODO\r\n";

    private static async Task<ExistingJournalTask> CreateAsync(HttpClient client, NewJournalTask request)
    {
        var post = await client.PostAsJsonAsync(Path, request);
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingJournalTask>())!;
    }

    private static async Task<TaskIcsImportResult> ImportAsync(HttpClient client, string ics)
    {
        var response = await PostIcsAsync(client, ics);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskIcsImportResult>())!;
    }

    private static async Task<HttpResponseMessage> PostIcsAsync(
        HttpClient client, string ics, string fileName = "tasks.ics", string contentType = "text/calendar")
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(ics));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        return await client.PostAsync(IcsPath, content);
    }

    private static readonly byte[] SeededFileContent = [1, 2, 3];

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    // One inline attachment line, folded the way a producer might: base64 split over continuation lines.
    private static string InlineAttach(byte[] content, string fileName, string contentType, string? extraParams = null)
    {
        var base64 = Convert.ToBase64String(content);
        var folded = string.Join("\r\n ", Enumerable.Range(0, (base64.Length + 59) / 60).Select(i => base64.Substring(i * 60, Math.Min(60, base64.Length - i * 60))));
        return $"ATTACH;FMTTYPE={contentType};ENCODING=BASE64;VALUE=BINARY;FILENAME=\"{fileName}\"{extraParams}:{folded}";
    }

    private static async Task<List<FileMetadata>> FilesAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.FileMetadata.AsNoTracking().ToListAsync();
    }

    private static async Task SetSettingAsync(WebApplicationFactory<Program> factory, string key, string value)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        var row = await context.SystemSettings.SingleOrDefaultAsync(s => s.Key == key);
        if (row is null)
        {
            context.SystemSettings.Add(new SystemSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            row.Value = value;
        }

        await context.SaveChangesAsync();
    }

    private static Task<Guid> SeedFileAsync(WebApplicationFactory<Program> factory, string fileName, string contentType) =>
        SeedFileAsync(factory, fileName, contentType, SeededFileContent);

    private static async Task<Guid> SeedFileAsync(
        WebApplicationFactory<Program> factory, string fileName, string contentType, byte[] content)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var blobContent = content;
        var metadataId = Guid.NewGuid();
        context.FileMetadata.Add(new FileMetadata
        {
            Id = metadataId,
            UploadedByUserId = ActorUserId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = content.Length,
            Sha256Hash = Sha256(content),
            UploadedAtUtc = DateTime.UtcNow,
            FinanceStoredObjectId = await StoredFileFixtures.PutAsync(scope.ServiceProvider, blobContent),
        });
        await context.SaveChangesAsync();
        return metadataId;
    }

    public class ApiFactory : OdysseyApiFactory
    {
        private readonly string databaseName;

        // sharingStoreWith: a second caller (other claims) over the same database and file volume.
        public ApiFactory(IReadOnlyCollection<string>? permissions, ApiFactory? sharingStoreWith = null)
            : this(permissions, sharingStoreWith?.databaseName ?? $"domain-{Guid.NewGuid()}", sharingStoreWith)
        {
        }

        private ApiFactory(IReadOnlyCollection<string>? permissions, string databaseName, ApiFactory? sharingStoreWith)
            : base(permissions, ActorUserId, configureServices: services => IsolateDomainContext(services, databaseName),
                sharingStoreWith: sharingStoreWith)
        {
            this.databaseName = databaseName;
        }

        private static void IsolateDomainContext(IServiceCollection services, string databaseName)
        {
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
        }
    }

    /// <summary>The class's shared host: every caller's claims travel per request (<see cref="SharedApiHostClients"/>).</summary>
    public sealed class Host() : ApiFactory(permissions: null);
}
