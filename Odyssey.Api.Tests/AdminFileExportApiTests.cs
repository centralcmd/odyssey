using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Odyssey.Core.FileExport;
using Odyssey.Context;
using Odyssey.Testing.FileStorage;
using Odyssey.Core.FileStorage;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

public class AdminFileExportApiTests(AdminFileExportApiTests.Host host) : SharedApiHostTests<AdminFileExportApiTests.Host>(host)
{
    private const string ActorUserId = "file-export-actor-id";
    private const string ExportPath = "/api/admin/files/export";
    private const string FilteredExportPath = "/api/admin/files/export/filtered";
    private const string SummaryPath = "/api/admin/files/export/summary";

    // ── Authorization matrix (spec §3.4 / §11.2) ──────────────────────────────

    [Fact]
    public async Task Export_Unauthenticated_ReturnsUnauthorized()
    {
        var factory = Factory.ForCaller(null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ExportPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Export_WithoutFilesExportAllPermission_ReturnsForbidden()
    {
        var factory = Factory.ForCaller([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ExportPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Export_WithPermissionAndFeatureEnabled_ReturnsZipAttachment()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        await SeedFileAsync(factory, "receipt.pdf", "application/pdf", "pdf-bytes");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ExportPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Matches(@"^odyssey-files-export-\d{8}-\d{6}Z\.zip$", disposition.FileName!.Trim('"'));
    }

    // ── Archive shape (spec §6 / §11.1) ───────────────────────────────────────

    [Fact]
    public async Task Export_Archive_ContainsFileMapAndOneEntryPerFile()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        var idA = await SeedFileAsync(factory, "receipt.pdf", "application/pdf", "A");
        var idB = await SeedFileAsync(factory, "statement.csv", "text/csv", "B");
        using var client = factory.CreateClient();

        using var archive = await GetArchiveAsync(client);

        var fileEntries = archive.Entries.Where(e => e.FullName.StartsWith("files/")).ToList();
        Assert.Equal(2, fileEntries.Count);
        Assert.NotNull(archive.GetEntry("file-map.json"));

        var map = await ReadFileMapAsync(archive);
        Assert.Equal(2, map.Files.Count);
        // Every stored FileId appears in the map…
        Assert.Contains(map.Files, f => f.FileId == idA.ToString());
        Assert.Contains(map.Files, f => f.FileId == idB.ToString());
        // …and each mapped filename is an actual archive entry under files/.
        foreach (var entry in map.Files)
        {
            Assert.NotNull(archive.GetEntry($"files/{entry.FileName}"));
        }
    }

    [Fact]
    public async Task Export_FileMap_ContainsOnlyIdAndFileName()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        await SeedFileAsync(factory, "receipt.pdf", "application/pdf", "A");
        using var client = factory.CreateClient();

        using var archive = await GetArchiveAsync(client);
        await using var stream = archive.GetEntry("file-map.json")!.Open();
        using var document = await JsonDocument.ParseAsync(stream);

        var entry = document.RootElement.GetProperty("files").EnumerateArray().Single();
        Assert.Equal(["fileId", "fileName"], entry.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task Export_SanitizesUnsafeNamesAndDeduplicates()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        await SeedFileAsync(factory, "receipt.pdf", "application/pdf", "1");
        await SeedFileAsync(factory, "receipt.pdf", "application/pdf", "2");      // duplicate name
        await SeedFileAsync(factory, "../../etc/passwd", "text/plain", "3");      // path traversal attempt
        var emptyId = await SeedFileAsync(factory, "", "application/pdf", "4");   // empty/unsafe name
        using var client = factory.CreateClient();

        using var archive = await GetArchiveAsync(client);
        var names = archive.Entries
            .Where(e => e.FullName.StartsWith("files/"))
            .Select(e => e.FullName["files/".Length..])
            .ToList();

        Assert.Equal(4, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // all unique
        Assert.Contains("receipt.pdf", names);
        Assert.Contains("receipt (2).pdf", names);
        // No entry can escape the files/ folder or carry separators / traversal.
        Assert.All(names, name =>
        {
            Assert.DoesNotContain('/', name);
            Assert.DoesNotContain('\\', name);
            Assert.False(name.StartsWith(".."));
        });
        // Empty original name fell back to file-{id}.
        Assert.Contains(names, name => name.StartsWith($"file-{emptyId}"));
    }

    [Fact]
    public async Task Export_MissingBlobContent_ReturnsServerError()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        await SeedOrphanMetadataAsync(factory, "orphan.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ExportPath);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Export_NoFiles_ReturnsZipWithEmptyMap()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        using var client = factory.CreateClient();

        using var archive = await GetArchiveAsync(client);

        Assert.DoesNotContain(archive.Entries, e => e.FullName.StartsWith("files/"));
        var map = await ReadFileMapAsync(archive);
        Assert.Empty(map.Files);
    }

    // ── Filtered export (Odyssey Design System · Files.jsx "Export filtered") ─

    [Fact]
    public async Task ExportFiltered_WithoutFilesExportAllPermission_ReturnsForbidden()
    {
        var factory = Factory.ForCaller([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(FilteredExportPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExportFiltered_NoFilter_MatchesUnfilteredExport()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        await SeedFileAsync(factory, "receipt.pdf", "application/pdf", "A");
        await SeedFileAsync(factory, "statement.csv", "text/csv", "B");
        using var client = factory.CreateClient();

        using var archive = await GetArchiveAsync(client, FilteredExportPath);

        var fileEntries = archive.Entries.Where(e => e.FullName.StartsWith("files/")).ToList();
        Assert.Equal(2, fileEntries.Count);
    }

    [Fact]
    public async Task ExportFiltered_BySearch_MatchesOnlyFilenamesContainingTheTerm()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        await SeedFileAsync(factory, "annual-receipt.pdf", "application/pdf", "A");
        await SeedFileAsync(factory, "statement.csv", "text/csv", "B");
        using var client = factory.CreateClient();

        using var archive = await GetArchiveAsync(client, $"{FilteredExportPath}?search=receipt");

        var map = await ReadFileMapAsync(archive);
        Assert.Equal(["annual-receipt.pdf"], map.Files.Select(f => f.FileName));
    }

    [Fact]
    public async Task ExportFiltered_ByMultipleKinds_MatchesAnySelectedKind()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        await SeedFileAsync(factory, "receipt.pdf", "application/pdf", "A");
        await SeedFileAsync(factory, "photo.png", "image/png", "B");
        await SeedFileAsync(factory, "notes.txt", "text/plain", "C");
        using var client = factory.CreateClient();

        // The Files page's Type filter is multi-select; the export must match ANY of the kinds sent,
        // unlike the general list endpoint's single-value Kind query param.
        using var archive = await GetArchiveAsync(client, $"{FilteredExportPath}?kind=Pdf&kind=Image");

        var map = await ReadFileMapAsync(archive);
        Assert.Equal(2, map.Files.Count);
        Assert.Contains(map.Files, f => f.FileName == "receipt.pdf");
        Assert.Contains(map.Files, f => f.FileName == "photo.png");
        Assert.DoesNotContain(map.Files, f => f.FileName == "notes.txt");
    }

    [Fact]
    public async Task ExportFiltered_SearchAndKindCombine_AsAnAndAcrossDimensions()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        await SeedFileAsync(factory, "receipt.pdf", "application/pdf", "A");   // matches both
        await SeedFileAsync(factory, "receipt.png", "image/png", "B");        // matches search, not kind
        await SeedFileAsync(factory, "statement.pdf", "application/pdf", "C"); // matches kind, not search
        using var client = factory.CreateClient();

        using var archive = await GetArchiveAsync(client, $"{FilteredExportPath}?search=receipt&kind=Pdf");

        var map = await ReadFileMapAsync(archive);
        Assert.Equal(["receipt.pdf"], map.Files.Select(f => f.FileName));
    }

    [Fact]
    public async Task ExportFiltered_FileName_UsesFilteredSuffix()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(FilteredExportPath);

        response.EnsureSuccessStatusCode();
        var fileName = response.Content.Headers.ContentDisposition!.FileName!.Trim('"');
        Assert.Matches(@"^odyssey-files-export-filtered-\d{8}-\d{6}Z\.zip$", fileName);
    }

    // ── Summary endpoint (client card, spec §10.1) ────────────────────────────

    [Fact]
    public async Task Summary_WithoutPermission_ReturnsForbidden()
    {
        var factory = Factory.ForCaller([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(SummaryPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Summary_ReportsFileCountAndTotalSize()
    {
        var factory = Factory.ForCaller([PermissionClaims.FilesExportAll]);
        // Distinct byte lengths so the summed size is unambiguous: 3 + 7 = 10 bytes.
        await SeedFileAsync(factory, "a.pdf", "application/pdf", "aaa");
        await SeedFileAsync(factory, "b.pdf", "application/pdf", "bbbbbbb");
        using var client = factory.CreateClient();

        var summary = await client.GetFromJsonAsync<FileExportSummary>(SummaryPath);

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.FileCount);
        Assert.Equal(10, summary.TotalSizeBytes);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<ZipArchive> GetArchiveAsync(HttpClient client, string path = ExportPath)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
    }

    private static async Task<FileMapDocument> ReadFileMapAsync(ZipArchive archive)
    {
        await using var stream = archive.GetEntry("file-map.json")!.Open();
        return (await JsonSerializer.DeserializeAsync<FileMapDocument>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static async Task<Guid> SeedFileAsync(
        WebApplicationFactory<Program> factory, string fileName, string contentType, string content)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var file = await StoredFileFixtures.SeedFileAsync(
            scope.ServiceProvider, Encoding.UTF8.GetBytes(content), fileName, contentType, uploadedByUserId: "uploader");
        return file.Id;
    }

    private static async Task SeedOrphanMetadataAsync(
        WebApplicationFactory<Program> factory, string fileName, string contentType)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        // Metadata whose stored object does not exist.
        context.FileMetadata.Add(new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = "uploader",
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = 10,
            Sha256Hash = "hash",
            FinanceStoredObjectId = Guid.NewGuid(),
            UploadedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    public class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    /// <summary>The class's shared host: every caller's claims travel per request (<see cref="SharedApiHostClients"/>).</summary>
    public sealed class Host() : ApiFactory([]);
}
