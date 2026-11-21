using Odyssey.Context;
using Odyssey.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Testing.FileStorage;

namespace Odyssey.Api.Tests;

/// <summary>
/// VJOURNAL/.ics journal-entry import/export endpoints (issue #339): what only the HTTP surface shows — claim gates,
/// status codes, problem-details codes, headers, the transport limits and the abort of an over-cap stream — plus a
/// happy-path round trip. The import and export rules themselves are pinned at the service tier, in
/// <c>Odyssey.Core.Tests/Ics/JournalEntryIcsServiceTests</c>.
/// </summary>
public class JournalEntryIcsApiTests(JournalEntryIcsApiTests.Host host) : SharedApiHostTests<JournalEntryIcsApiTests.Host>(host)
{
    private const string ActorUserId = "journal-entry-ics-actor-id";
    private const string Path = "/api/journal-entries";
    private const string IcsPath = "/api/journal-entries/vjournal";

    // The shipped counts are unlimited (entries carry their files and photos inline), so the cap tests pin an
    // administrator's finite value instead.
    private const int AdminCountCap = 5;

    private static readonly string[] ReadOnly = [PermissionClaims.JournalRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate,
    ];

    // Photo links need photos.read since issue #428 §3.3; attachments still need files.read.
    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate,
        PermissionClaims.JournalUpdate, PermissionClaims.FilesRead, PermissionClaims.PhotosRead,
    ];

    // Importing inline content needs the create claims too: files.read + files.create, photos.read + photos.create.
    private static readonly string[] ReadWriteWithUploads =
    [
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate,
        PermissionClaims.FilesRead, PermissionClaims.FilesCreate, PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate,
    ];

    private static readonly byte[] NoteBytes = Encoding.UTF8.GetBytes("Packing list: tent, stove, map.");

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
        Assert.DoesNotContain("BEGIN:VJOURNAL", body);
    }

    [Fact]
    public async Task ExportAll_FileName_IsTimestampPrefixed()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadOnly);

        var fileName = await FileNameOf(await client.GetAsync(IcsPath));
        Assert.Matches(@"^odyssey-journal-entries-\d{8}-\d{6}Z\.ics$", fileName!);
    }

    [Fact]
    public async Task Export_WithoutReadClaim_ReturnsForbidden()
    {
        var factory = Factory;
        using var client = factory.ClientFor([]);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(IcsPath)).StatusCode);
    }

    [Fact]
    public async Task ExportSingle_ReturnsOneVJournal()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var keep = await CreateAsync(client, NewEntry("Keep"));
        await CreateAsync(client, NewEntry("Drop"));

        var response = await client.GetAsync($"{Path}/{keep.JournalEntryId}/vjournal");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(1, Count(body, "BEGIN:VJOURNAL"));
        Assert.Contains(keep.ExternalUid, body);
        var fileName = await FileNameOf(response);
        Assert.Matches(@"^odyssey-journal-entry-\d{8}-\d{6}Z\.ics$", fileName!);
    }

    [Fact]
    public async Task ExportSingle_NotFound_Returns404()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadOnly);

        var response = await client.GetAsync($"{Path}/{Guid.NewGuid()}/vjournal");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_HandAuthored_CreatesEntries_WithVerbatimUid()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(
            Vjournal("urn:uuid:e1", "SUMMARY:One", "DESCRIPTION:First", "DTSTART;VALUE=DATE:20260101"),
            Vjournal("external-2", "SUMMARY:Two", "DESCRIPTION:Second", "DTSTART;VALUE=DATE:20260102"));

        var result = await ImportAsync(client, ics);

        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(result.Skipped);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Contains(await ctx.JournalEntries.ToListAsync(), r => r.ExternalUid == "external-2");
    }

    [Fact]
    public async Task Import_ExportedFile_IsIdempotent_UpdatesInPlace()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await CreateAsync(client, NewEntry("Alpha"));
        await CreateAsync(client, NewEntry("Beta"));

        var exported = await (await client.GetAsync(IcsPath)).Content.ReadAsStringAsync();
        var result = await ImportAsync(client, exported);

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(2, result.UpdatedCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(2, await ctx.JournalEntries.CountAsync()); // no duplicates
    }

    [Fact]
    public async Task Import_MalformedFile_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostIcsAsync(client, "not an ics file")).StatusCode);
    }

    [Fact]
    public async Task Import_NonIcsExtension_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vjournal("x1", "SUMMARY:x", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostIcsAsync(client, ics, "data.txt")).StatusCode);
    }

    [Fact]
    public async Task Import_WithOnlyCreateClaim_ReturnsForbidden()
    {
        var factory = Factory;
        using var client = factory.ClientFor([PermissionClaims.JournalCreate, PermissionClaims.JournalRead]);

        var ics = Vcalendar(Vjournal("p1", "SUMMARY:x", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostIcsAsync(client, ics)).StatusCode);
    }

    // ------------------------------------------------------------------ update: link removal / reorder

    // ------------------------------------------------------------------ bounded fields / caps

    // ------------------------------------------------------------------ export cap discriminator

    [Fact]
    public async Task Export_ExceedingCap_Returns400_WithMachineReadableCode()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadOnly);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.JournalIcsMaxExportRows, AdminCountCap.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await SeedManyEntriesAsync(factory, AdminCountCap + 1);

        var response = await client.GetAsync(IcsPath);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(problem!.Extensions.TryGetValue("code", out var code));
        // The wire value itself (issue #516 AC 20): a rename of the constant must fail here, not pass silently.
        Assert.Equal("journal_entries_export_limit_exceeded", code!.ToString());
    }

    // ------------------------------------------------------------------ direct API ExternalUid

    [Fact]
    public async Task Create_WithControlCharExternalUid_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var request = NewEntry("Bad");
        request.ExternalUid = "line1\nline2";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, request)).StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    // ------------------------------------------------------------------ attached files and photos travel inline

    [Fact]
    public async Task Export_WithAttachedFile_WithoutFilesRead_IsForbidden_AndSoIsTheSummary()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);
        var fileId = await SeedFileWithContentAsync(factory, "note.txt", "text/plain", NoteBytes);
        var entry = await CreateAsync(client, NewEntry("With file", attachmentFileIds: [fileId]));

        var readOnly = Factory;
        using var readOnlyClient = readOnly.ClientFor([PermissionClaims.JournalRead, PermissionClaims.PhotosRead]);

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath + "/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync($"{Path}/{entry.JournalEntryId}/vjournal")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync($"{Path}/{entry.JournalEntryId}/vjournal/summary")).StatusCode);
    }

    [Fact]
    public async Task Export_WithPhoto_WithoutPhotosRead_IsForbidden_AndSoIsTheSummary()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);
        var photoId = await SeedPhotoAsync(factory, "beach.jpg");
        await CreateAsync(client, NewEntry("With photo", photoIds: [photoId]));

        var readOnly = Factory;
        using var readOnlyClient = readOnly.ClientFor([PermissionClaims.JournalRead, PermissionClaims.FilesRead]);

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath + "/summary")).StatusCode);
    }

    [Fact]
    public async Task Export_WithoutFilesOrPhotos_NeedsOnlyJournalRead()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await CreateAsync(client, NewEntry("Plain"));

        var readOnly = Factory;
        using var readOnlyClient = readOnly.ClientFor(ReadOnly);

        Assert.Equal(HttpStatusCode.OK, (await readOnlyClient.GetAsync(IcsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnlyClient.GetAsync(IcsPath + "/summary")).StatusCode);
    }

    [Theory]
    [InlineData(PermissionClaims.FilesCreate)]
    [InlineData(PermissionClaims.FilesRead)]
    public async Task Import_InlineFile_WithoutBothFileClaims_IsForbidden_NothingImported(string missing)
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithUploads.Where(c => c != missing).ToArray());

        var response = await PostIcsAsync(client, Vcalendar(Vjournal("f1", "SUMMARY:Gated", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlineAttach(NoteBytes, "note.txt", "text/plain"))));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await ctx.JournalEntries.CountAsync());
        Assert.Empty(await FilesAsync(factory));
    }

    [Theory]
    [InlineData(PermissionClaims.PhotosCreate)]
    [InlineData(PermissionClaims.PhotosRead)]
    public async Task Import_InlinePhoto_WithoutBothPhotoClaims_IsForbidden_NothingImported(string missing)
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithUploads.Where(c => c != missing).ToArray());

        var response = await PostIcsAsync(client, Vcalendar(Vjournal("g1", "SUMMARY:Gated", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "beach.jpg"))));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await ctx.JournalEntries.CountAsync());
        Assert.Empty(await PhotosAsync(factory));
    }

    [Fact]
    public async Task Import_InlineFile_LargerThanTheImportCap_IsRefusedWhole()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithUploads);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.JournalIcsMaxImportMegabytes, "1");

        var response = await PostIcsAsync(client, Vcalendar(Vjournal("b1", "SUMMARY:Big", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlineAttach(new byte[1024 * 1024], "big.txt", "text/plain"))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await FilesAsync(factory));
    }

    // The two kinds are gated apart: photo claims never stand in for file claims, nor the reverse.
    [Fact]
    public async Task Import_PhotoClaimsAlone_AcceptAnInlinePhoto_ButRefuseAnInlineFile()
    {
        string[] photosOnly =
        [
            PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate,
            PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate,
        ];
        var factory = Factory;
        using var client = factory.ClientFor(photosOnly);

        var photo = await ImportAsync(client, Vcalendar(Vjournal("po1", "SUMMARY:Photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "beach.jpg"))));
        var file = await PostIcsAsync(client, Vcalendar(Vjournal("po2", "SUMMARY:File", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlineAttach(NoteBytes, "note.txt", "text/plain"))));

        Assert.Equal((1, 1), (photo.ImportedCount, photo.StoredPhotoCount));
        Assert.Equal(HttpStatusCode.Forbidden, file.StatusCode);
    }

    [Fact]
    public async Task Import_FileClaimsAlone_AcceptAnInlineFile_ButRefuseAnInlinePhoto()
    {
        string[] filesOnly =
        [
            PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate,
            PermissionClaims.FilesRead, PermissionClaims.FilesCreate,
        ];
        var factory = Factory;
        using var client = factory.ClientFor(filesOnly);

        var file = await ImportAsync(client, Vcalendar(Vjournal("fo1", "SUMMARY:File", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlineAttach(NoteBytes, "note.txt", "text/plain"))));
        var photo = await PostIcsAsync(client, Vcalendar(Vjournal("fo2", "SUMMARY:Photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "beach.jpg"))));

        Assert.Equal((1, 1), (file.ImportedCount, file.StoredFileCount));
        Assert.Equal(HttpStatusCode.Forbidden, photo.StatusCode);
        Assert.Empty(await PhotosAsync(factory));
    }

    [Fact]
    public async Task Export_ThatGrowsPastTheByteCapWhileStreaming_IsAborted_NotTruncated()
    {
        // The pre-flight check judges files and photos exactly, but entry text only as it is written. Text alone over
        // the cap must end the response abnormally, so a browser reports a failed download instead of saving part of it.
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.JournalIcsMaxExportMegabytes, "1");
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var now = DateTime.UtcNow;
            for (var i = 0; i < 400; i++)
            {
                context.JournalEntries.Add(new JournalEntry
                {
                    ExternalUid = $"urn:uuid:bulk-{i}",
                    Title = $"Bulk {i}",
                    Content = new string('x', 4000),
                    EntryDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    CreatedByUserId = ActorUserId,
                    CreatedAt = now,
                    UpdatedAt = now,
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

    // Both export actions stream gigabytes of inline content, so both take the export permits and deadline; a
    // single-entry route without them would be an unthrottled path to the same streams.
    [Theory]
    [InlineData(nameof(Odyssey.Api.Controllers.JournalEntriesController.ExportVJournal))]
    [InlineData(nameof(Odyssey.Api.Controllers.JournalEntriesController.ExportSingleVJournal))]
    public void EveryStreamingExport_TakesTheExportConcurrencyLimiters(string action)
    {
        var method = typeof(Odyssey.Api.Controllers.JournalEntriesController).GetMethod(action)!;

        var rateLimit = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), false)
            .Cast<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>().SingleOrDefault();
        Assert.Equal(Odyssey.Api.ImportExportRateLimiting.ExportConcurrencyPolicy, rateLimit?.PolicyName);
        Assert.Contains(method.GetCustomAttributes(typeof(TypeFilterAttribute), false).Cast<TypeFilterAttribute>(),
            a => a.ImplementationType == typeof(Odyssey.Api.ExportConcurrencyFilter));
    }

    private static int Count(string haystack, string needle)
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

    private static async Task<string?> FileNameOf(HttpResponseMessage response) =>
        response.Content.Headers.ContentDisposition?.FileNameStar
        ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');

    private static string Vcalendar(params string[] journals) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(journals) + "END:VCALENDAR\r\n";

    private static string Vjournal(string uid, params string[] lines) =>
        "BEGIN:VJOURNAL\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VJOURNAL\r\n";

    private static NewJournalEntry NewEntry(
        string title,
        string? content = null,
        string? location = null,
        DateTime? entryDate = null,
        Guid[]? tagIds = null,
        Guid[]? contactIds = null,
        Guid[]? photoIds = null,
        Guid[]? attachmentFileIds = null) => new()
        {
            Title = title,
            Content = content ?? "Dear journal, today was a day.",
            EntryDate = entryDate ?? new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Location = location,
            TagIds = tagIds ?? [],
            ContactIds = contactIds ?? [],
            PhotoIds = photoIds ?? [],
            AttachmentFileIds = attachmentFileIds ?? [],
        };

    private static async Task<ExistingJournalEntry> CreateAsync(HttpClient client, NewJournalEntry request)
    {
        var post = await client.PostAsJsonAsync(Path, request);
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingJournalEntry>())!;
    }

    private static async Task<JournalEntryIcsImportResult> ImportAsync(HttpClient client, string ics)
    {
        var response = await PostIcsAsync(client, ics);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JournalEntryIcsImportResult>())!;
    }

    private static async Task<HttpResponseMessage> PostIcsAsync(
        HttpClient client, string ics, string fileName = "entries.ics", string contentType = "text/calendar")
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(ics));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        return await client.PostAsync(IcsPath, content);
    }

    private static Task<Guid> SeedPhotoAsync(WebApplicationFactory<Program> factory, string fileName) =>
        PhotoTestSupport.SeedPhotoAsync(factory, null, fileName);

    // A file with real content and its real hash, so an import can match it.
    private static async Task<Guid> SeedFileWithContentAsync(
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

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    // One inline attachment line, folded the way a producer might: base64 split over continuation lines.
    private static string InlineAttach(byte[] content, string fileName, string contentType, string? extraParams = null)
    {
        var base64 = Convert.ToBase64String(content);
        var folded = string.Join("\r\n ", Enumerable.Range(0, (base64.Length + 59) / 60).Select(i => base64.Substring(i * 60, Math.Min(60, base64.Length - i * 60))));
        return $"ATTACH;FMTTYPE={contentType};ENCODING=BASE64;VALUE=BINARY;FILENAME=\"{fileName}\"{extraParams}:{folded}";
    }

    private static string InlinePhoto(byte[] content, string fileName, string? title = null, string? caption = null) =>
        InlineAttach(content, fileName, "image/jpeg",
            ";X-ODYSSEY-KIND=PHOTO"
            + (title is null ? "" : $";X-ODYSSEY-TITLE=\"{title}\"")
            + (caption is null ? "" : $";X-ODYSSEY-CAPTION=\"{caption}\""));

    private static async Task<List<FileMetadata>> FilesAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.FileMetadata.AsNoTracking().ToListAsync();
    }

    private static async Task<List<Photo>> PhotosAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Photos.AsNoTracking().ToListAsync();
    }

    private static async Task SeedManyEntriesAsync(WebApplicationFactory<Program> factory, int count)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        for (var i = 0; i < count; i++)
        {
            context.JournalEntries.Add(new JournalEntry
            {
                ExternalUid = $"urn:uuid:bulk-{i}",
                Title = $"Entry {i}",
                Content = "seeded",
                EntryDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedByUserId = ActorUserId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await context.SaveChangesAsync();
    }

    /// <summary>The class's shared host: every caller's claims travel per request (<see cref="SharedApiHostClients"/>).</summary>
    public sealed class Host() : ApiFactory(permissions: null);

    public class ApiFactory : OdysseyApiFactory
    {
        private readonly string databaseName;

        // sharingStoreWith: a second caller (other claims) over the same database and file volume.
        public ApiFactory(IReadOnlyCollection<string>? permissions, ApiFactory? sharingStoreWith = null)
            : this(permissions, sharingStoreWith?.databaseName ?? $"journal-entry-ics-{Guid.NewGuid()}", sharingStoreWith)
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
}
