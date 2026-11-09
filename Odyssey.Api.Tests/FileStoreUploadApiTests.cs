using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
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

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string? area, byte[]? bytes = null, string name = "Lønnsslipp.pdf")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes ?? Pdf);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
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
        Assert.Equal(Pdf, await File.ReadAllBytesAsync(Path.Combine(factory.PhotoVolumeRoot, stored.RelativePath)));
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
        factory.PhotoVolumeSpace.Override = (1024L * 1024 * 1024, 1024L * 1024 * 1024 * 1024);

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
}
