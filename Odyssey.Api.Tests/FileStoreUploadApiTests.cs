using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Testing.FileStorage;
using Odyssey.Context;
using Odyssey.Api;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #448 §5: <c>POST /api/files</c> writes to the area the surface names, a download streams from disk with the shared
/// headers, and the move's status reads under <c>file-storage.read</c> only.
/// </summary>
public sealed class FileStoreUploadApiTests
{
    private static readonly byte[] Pdf = "%PDF-1.7 statement for the store"u8.ToArray();

    private static readonly string[] FilesClaims = [PermissionClaims.FilesCreate, PermissionClaims.FilesRead, PermissionClaims.FilesDelete];

    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string? area, byte[]? bytes = null, string name = "Lønnsslipp.pdf", string contentType = "application/pdf")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes ?? Pdf);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, "file", name);
        if (area is not null)
        {
            form.Add(new StringContent(area), "area");
        }

        return client.PostAsync("/api/files", form);
    }

    private static async Task<FileMetadata> RowAsync(OdysseyApiFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileMetadata.AsNoTracking().SingleAsync(f => f.Id == id);
    }

    /// <summary>Nothing reached the store: no row, no object, no bucket slot and no file in either area's folder.</summary>
    private static async Task AssertNothingWrittenAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.FileMetadata.AnyAsync());
        Assert.False(await context.StoredObjects.AnyAsync());
        Assert.False(await context.StoredFileBuckets.AnyAsync());
        Assert.Empty(FilesUnder(factory.FileVolumeRoot, "finance-files"));
        Assert.Empty(FilesUnder(factory.FileVolumeRoot, "journal-files"));
    }

    private static IEnumerable<string> FilesUnder(string root, string folder)
    {
        var path = Path.Combine(root, folder);
        return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : [];
    }

    [Theory]
    [InlineData("Finance", "finance-files/", StoredFileArea.Finance)]
    [InlineData("journal", "journal-files/", StoredFileArea.Journal)]
    public async Task An_upload_lands_in_its_area_on_disk_and_creates_no_blob(string area, string root, StoredFileArea expected)
    {
        await using var factory = new OdysseyApiFactory(FilesClaims);
        using var client = factory.CreateClient();

        var response = await UploadAsync(client, area);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var uploaded = (await response.Content.ReadFromJsonAsync<FileUploadResponse>())!;
        var row = await RowAsync(factory, uploaded.Id);
        Assert.Null(row.FileBlobId);
        Assert.Equal(expected, StoredFileAreas.AreaOf(row));
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var stored = await context.StoredObjects.AsNoTracking().SingleAsync(o => o.Id == StoredFileAreas.StoredObjectIdOf(row));
        Assert.StartsWith(root + DateTime.UtcNow.Year.ToString("D4") + "/1/", stored.RelativePath, StringComparison.Ordinal);
        Assert.Equal(Pdf, await File.ReadAllBytesAsync(Path.Combine(factory.FileVolumeRoot, stored.RelativePath)));
        Assert.False(await context.FileBlob.AnyAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Contacts")]
    [InlineData("ContactAvatars")]
    [InlineData("0")]
    [InlineData("Photos")]
    public async Task An_upload_without_a_usable_area_is_a_400_and_writes_nothing(string? area)
    {
        await using var factory = new OdysseyApiFactory(FilesClaims);
        using var client = factory.CreateClient();

        var response = await UploadAsync(client, area);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.FileMetadata.AnyAsync());
        Assert.False(await context.StoredObjects.AnyAsync());
    }

    [Fact]
    public async Task Below_the_reserve_an_upload_is_a_507_and_writes_nothing()
    {
        await using var factory = new OdysseyApiFactory(FilesClaims);
        using var client = factory.CreateClient();
        factory.FileVolumeSpace.Override = (1024L * 1024 * 1024, 1024L * 1024 * 1024 * 1024);

        var response = await UploadAsync(client, "Finance");

        Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.FileMetadata.AnyAsync());
        // Checked before the bucket slot, so a refusal burns none (issue #448 §3.3).
        Assert.False(await context.StoredFileBuckets.AnyAsync());
    }

    [Fact]
    public async Task On_a_degraded_volume_an_upload_is_a_503_with_retry_after_and_writes_nothing()
    {
        await using var factory = new OdysseyApiFactory(FilesClaims);
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<FileStorageVolumeRegistry>();
        registry.MarkDegraded(registry.Find("photos")!, new IOException("disk gone"));

        var response = await UploadAsync(client, "Finance");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.FileMetadata.AnyAsync());
        Assert.False(await context.StoredFileBuckets.AnyAsync());
    }

    [Fact]
    public async Task Past_the_per_user_limit_an_upload_is_a_429_with_retry_after()
    {
        await using var factory = new OdysseyApiFactory(FilesClaims, configuration: new Dictionary<string, string?>
        {
            [$"{FileUploadRateLimitOptions.SectionName}:PermitLimit"] = "2",
            [$"{FileUploadRateLimitOptions.SectionName}:WindowSeconds"] = "600",
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, "Finance", "%PDF-1.7 one"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, "Finance", "%PDF-1.7 two"u8.ToArray())).StatusCode);
        var third = await UploadAsync(client, "Finance", "%PDF-1.7 three"u8.ToArray());

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileMetadata.CountAsync());
    }

    [Fact]
    public async Task A_download_from_disk_carries_the_shared_headers_ranges_and_conditional_requests()
    {
        await using var factory = new OdysseyApiFactory(FilesClaims);
        using var client = factory.CreateClient();
        var uploaded = (await (await UploadAsync(client, "Finance")).Content.ReadFromJsonAsync<FileUploadResponse>())!;

        var full = await client.GetAsync($"/api/files/{uploaded.Id}/content");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(Pdf, await full.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", full.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal($"\"{uploaded.Sha256Hash}\"", full.Headers.ETag!.Tag);
        Assert.Contains("no-cache", full.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Equal("attachment", full.Content.Headers.ContentDisposition!.DispositionType);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{uploaded.Id}/content");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{uploaded.Sha256Hash}\""));
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);

        using var range = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{uploaded.Id}/content");
        range.Headers.Range = new RangeHeaderValue(0, 3);
        var partial = await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(Pdf[..4], await partial.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Deleting_a_file_on_disk_releases_it_for_erasure()
    {
        await using var factory = new OdysseyApiFactory(FilesClaims);
        using var client = factory.CreateClient();
        var uploaded = (await (await UploadAsync(client, "Journal")).Content.ReadFromJsonAsync<FileUploadResponse>())!;

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/files/{uploaded.Id}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.FileMetadata.AnyAsync());
        Assert.Equal(StoredObjectState.PendingDelete, (await context.StoredObjects.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task The_move_status_reads_counts_under_file_storage_read_only()
    {
        await using (var denied = new OdysseyApiFactory(FilesClaims))
        {
            using var client = denied.CreateClient();
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/file-storage/file-backfill")).StatusCode);
        }

        await using var factory = new OdysseyApiFactory([.. FilesClaims, PermissionClaims.FileStorageRead]);
        using var admin = factory.CreateClient();
        await UploadAsync(admin, "Finance");
        await UploadAsync(admin, "Journal", "%PDF-1.7 another"u8.ToArray());

        var status = (await admin.GetFromJsonAsync<FileBackfillStatus>("/api/file-storage/file-backfill"))!;

        Assert.Equal((0, 0, 0, 0), (status.Remaining, status.ToMove, status.WaitingOnPhotos, status.Failed));
        Assert.Equal((1, 1, 0), (status.OnDisk.Finance, status.OnDisk.Journal, status.OnDisk.ContactAvatars));
        var body = await (await admin.GetAsync("/api/file-storage/file-backfill")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("pdf", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>AC 5 on disk: without <c>files.create</c> the policy refuses before the action, so nothing is written.</summary>
    [Fact]
    public async Task Without_files_create_an_upload_is_a_403_and_writes_nothing()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.FilesRead, PermissionClaims.FilesDelete]);
        using var client = factory.CreateClient();

        var response = await UploadAsync(client, "Finance");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNothingWrittenAsync(factory);
    }

    /// <summary>AC 5 on disk: a multipart upload without the antiforgery token is a 400 and writes nothing.</summary>
    [Fact]
    public async Task Without_the_antiforgery_token_an_upload_is_a_400_and_writes_nothing()
    {
        await using var factory = new AntiforgeryEnforcedFactory(FilesClaims);
        using var client = factory.CreateClient();

        var response = await UploadAsync(client, "Finance");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Invalid or missing antiforgery token.", problem?.Detail);
        await AssertNothingWrittenAsync(factory);
    }

    /// <summary>
    /// AC 6/36: an SVG (active content, off the allow-list) and a PDF-declared file carrying PNG magic bytes are refused by
    /// validation, which runs before the bucket slot and the write — so neither leaves a row, an object, a slot or a file.
    /// </summary>
    [Theory]
    [InlineData("image/svg+xml", "logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("application/pdf", "statement.pdf", "\u0089PNG\r\n\u001a\n not a pdf at all")]
    public async Task A_refused_type_or_a_magic_byte_mismatch_is_a_400_before_any_write(string contentType, string name, string content)
    {
        await using var factory = new OdysseyApiFactory(FilesClaims);
        using var client = factory.CreateClient();
        var bytes = content.Select(c => (byte)c).ToArray();

        var response = await UploadAsync(client, "Finance", bytes, name, contentType);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingWrittenAsync(factory);
    }

    /// <summary>AC 7: uploads past a (test-sized) bucket's capacity roll over into the next bucket of the same year.</summary>
    [Fact]
    public async Task A_full_bucket_rolls_over_to_the_next()
    {
        await using var factory = new OdysseyApiFactory(FilesClaims,
            configureServices: services => services.Configure<StoredFileOptions>(o => o.BucketCapacity = 2));
        using var client = factory.CreateClient();

        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var response = await UploadAsync(client, "Finance", [.. Pdf, (byte)('a' + i)], $"statement-{i}.pdf");
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            ids.Add((await response.Content.ReadFromJsonAsync<FileUploadResponse>())!.Id);
        }

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var year = DateTime.UtcNow.Year.ToString("D4");
        var paths = new List<string>();
        foreach (var id in ids)
        {
            var row = await context.FileMetadata.AsNoTracking().SingleAsync(f => f.Id == id);
            paths.Add((await context.StoredObjects.AsNoTracking().SingleAsync(o => o.Id == row.FinanceStoredObjectId)).RelativePath);
        }

        Assert.StartsWith($"finance-files/{year}/1/", paths[0], StringComparison.Ordinal);
        Assert.StartsWith($"finance-files/{year}/1/", paths[1], StringComparison.Ordinal);
        Assert.StartsWith($"finance-files/{year}/2/", paths[2], StringComparison.Ordinal);
        var bucket = await context.StoredFileBuckets.AsNoTracking().SingleAsync();
        Assert.Equal((StoredFileAreas.FinanceFeatureKey, 2, 1), (bucket.FeatureKey, bucket.CurrentBucket, bucket.ObjectsWritten));
    }

    /// <summary>
    /// AC 26: with <c>FinanceFiles</c> bound to a volume of its own, a finance upload lands on it and a journal upload stays on
    /// the primary volume — and neither volume holds the other's file.
    /// </summary>
    [Fact]
    public async Task With_finance_files_on_a_second_volume_finance_uploads_land_there_and_journal_ones_on_the_primary()
    {
        var volumes = Directory.CreateTempSubdirectory("odyssey-api-two-volumes-");
        try
        {
            var primary = Directory.CreateDirectory(Path.Combine(volumes.FullName, "primary")).FullName;
            var finance = Directory.CreateDirectory(Path.Combine(volumes.FullName, "finance")).FullName;
            await using var factory = new OdysseyApiFactory(FilesClaims,
                configuration: new Dictionary<string, string?>
                {
                    ["FileStorage:Volumes:primary:RootPath"] = primary,
                    ["FileStorage:Volumes:finance:RootPath"] = finance,
                    ["FileStorage:Bindings:FinanceFiles"] = "finance",
                    ["FileStorage:Bindings:JournalFiles"] = "primary",
                    ["FileStorage:Bindings:ContactAvatars"] = "primary",
                    ["FileStorage:Bindings:PhotoOriginals"] = "primary",
                    ["FileStorage:Bindings:PhotoThumbnails"] = "primary",
                },
                configureServices: services =>
                {
                    // Temp roots are forbidden in a deployment (C4), and the reserve reads the probe rather than the CI disk.
                    services.Replace(ServiceDescriptor.Singleton(new FileStorageForbiddenRoots([])));
                    services.Replace(ServiceDescriptor.Singleton<IFileStorageSpaceProbe>(new TestSpaceProbe()));
                });
            using var client = factory.CreateClient();
            var journalBytes = "%PDF-1.7 journal attachment"u8.ToArray();

            var financeUpload = await UploadAsync(client, "Finance");
            var journalUpload = await UploadAsync(client, "Journal", journalBytes, "dagbok.pdf");

            Assert.Equal(HttpStatusCode.Created, financeUpload.StatusCode);
            Assert.Equal(HttpStatusCode.Created, journalUpload.StatusCode);
            var financeRow = await RowAsync(factory, (await financeUpload.Content.ReadFromJsonAsync<FileUploadResponse>())!.Id);
            var journalRow = await RowAsync(factory, (await journalUpload.Content.ReadFromJsonAsync<FileUploadResponse>())!.Id);
            using (var scope = factory.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
                var financeObject = await context.StoredObjects.AsNoTracking().SingleAsync(o => o.Id == financeRow.FinanceStoredObjectId);
                var journalObject = await context.StoredObjects.AsNoTracking().SingleAsync(o => o.Id == journalRow.JournalStoredObjectId);
                var volumeNames = await context.FileStorageVolumes.AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Name);

                Assert.Equal("finance", volumeNames[financeObject.VolumeId]);
                Assert.Equal("primary", volumeNames[journalObject.VolumeId]);
                Assert.Equal(Pdf, await File.ReadAllBytesAsync(Path.Combine(finance, financeObject.RelativePath)));
                Assert.Equal(journalBytes, await File.ReadAllBytesAsync(Path.Combine(primary, journalObject.RelativePath)));
            }

            Assert.Empty(FilesUnder(primary, "finance-files"));
            Assert.Empty(FilesUnder(finance, "journal-files"));
            Assert.Equal(Pdf, await (await client.GetAsync($"/api/files/{financeRow.Id}/content")).Content.ReadAsByteArrayAsync());
        }
        finally
        {
            volumes.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The shared factory in a non-"Testing" environment, which turns on the antiforgery enforcement "Testing" is exempt
    /// from — the <c>AntiforgeryEnforcementTests</c> shape, keeping the factory's own temp file volume.
    /// </summary>
    private sealed class AntiforgeryEnforcedFactory : OdysseyApiFactory
    {
        public AntiforgeryEnforcedFactory(IReadOnlyCollection<string> permissions) : base(permissions) =>
            Environment.SetEnvironmentVariable("UseInMemoryDatabase", "true");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("AntiforgeryEnforced");
        }
    }
}
