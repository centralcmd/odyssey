using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #247: a <c>files.update</c> holder could rename a shared file to a name holding CR/LF or a
/// quote. The rename stored it unsanitized, and the download handlers interpolated it into a
/// hand-built <c>Content-Disposition</c> that Kestrel refuses — so every download of that file
/// <c>500</c>ed, for everyone. These pin both halves of the fix: the rename sanitizes and bounds the
/// name, and the download header is encoded by the framework whatever the stored name holds.
/// </summary>
/// <remarks>
/// TestServer does not validate header values the way Kestrel does, so the header tests assert the
/// property Kestrel enforces (no control characters) directly rather than relying on a <c>500</c>.
/// </remarks>
public sealed class FileRenameSanitizationApiTests
{
    private const string ActorUserId = "file-rename-actor";

    private sealed class ApiFactory(IReadOnlyCollection<string> permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    private static ApiFactory CreateFactory() =>
        new([PermissionClaims.FilesRead, PermissionClaims.FilesUpdate]);

    [Fact]
    public async Task Rename_WithCrLfAndQuotes_StoresTheSanitizedName_AndTheFileStillDownloads()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var fileId = await SeedFileAsync(factory, "statement.pdf");

        var put = await client.PutAsJsonAsync($"/api/files/{fileId}/metadata",
            new UpdateFileMetadataRequest { FileName = "evil\r\nX-Injected: 1\".pdf" });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<FileMetadataResponse>();
        Assert.Equal("evilX-Injected_ 1_.pdf", body!.FileName);
        Assert.Equal("evilX-Injected_ 1_.pdf", await StoredNameAsync(factory, fileId));

        var download = await client.GetAsync($"/api/files/{fileId}/content");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var disposition = download.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal("evilX-Injected_ 1_.pdf", disposition.FileNameStar ?? disposition.FileName!.Trim('"'));
        Assert.False(download.Headers.Contains("X-Injected"));
    }

    [Theory]
    [InlineData("///")]
    [InlineData("\u0001\u007F")]
    public async Task Rename_ToANameWithNothingLeftAfterSanitizing_Is400OnFileName(string requested)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var fileId = await SeedFileAsync(factory, "statement.pdf");

        var put = await client.PutAsJsonAsync($"/api/files/{fileId}/metadata",
            new UpdateFileMetadataRequest { Description = "desc", FileName = requested });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("FileName", await put.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("statement.pdf", await StoredNameAsync(factory, fileId));
    }

    [Theory]
    [InlineData(257, 0)]
    [InlineData(0, 257)]
    public async Task Rename_OverTheColumnLength_Is400(int fileNameLength, int descriptionLength)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var fileId = await SeedFileAsync(factory, "statement.pdf");

        var put = await client.PutAsJsonAsync($"/api/files/{fileId}/metadata", new UpdateFileMetadataRequest
        {
            Description = descriptionLength == 0 ? null : new string('d', descriptionLength),
            FileName = fileNameLength == 0 ? null : new string('n', fileNameLength),
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal("statement.pdf", await StoredNameAsync(factory, fileId));
    }

    [Fact]
    public async Task Rename_AtTheColumnLength_IsAccepted()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var fileId = await SeedFileAsync(factory, "statement.pdf");
        var name = new string('n', 256);

        var put = await client.PutAsJsonAsync($"/api/files/{fileId}/metadata",
            new UpdateFileMetadataRequest { Description = new string('d', 256), FileName = name });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(name, await StoredNameAsync(factory, fileId));
    }

    /// <summary>
    /// A row stored before the rename was sanitized (or written past the service) must still download:
    /// the header is the framework's encoding of the name, never an interpolation of it.
    /// </summary>
    [Fact]
    public async Task Download_OfAPreExistingRowWithAHeaderBreakingName_EmitsNoControlCharacters()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var fileId = await SeedFileAsync(factory, "legacy\r\nX-Injected: 1\".pdf");

        var download = await client.GetAsync($"/api/files/{fileId}/content");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var raw = string.Join(", ", download.Content.Headers.GetValues(HeaderNames.ContentDisposition));
        Assert.StartsWith("attachment", raw);
        Assert.DoesNotContain(raw, char.IsControl);
        Assert.False(download.Headers.Contains("X-Injected"));
    }

    [Fact]
    public async Task Download_OfANonAsciiName_UsesFileNameStar()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var fileId = await SeedFileAsync(factory, "Kontoauszug-März.pdf");

        var download = await client.GetAsync($"/api/files/{fileId}/content");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var raw = string.Join(", ", download.Content.Headers.GetValues(HeaderNames.ContentDisposition));
        Assert.Contains("filename*=UTF-8''", raw);
        Assert.All(raw, c => Assert.True(c < 0x80, $"non-ASCII character in header: {raw}"));
        Assert.Equal("Kontoauszug-März.pdf", download.Content.Headers.ContentDisposition!.FileNameStar);
    }

    private static async Task<Guid> SeedFileAsync(OdysseyApiFactory factory, string fileName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await db.Database.EnsureCreatedAsync();

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [(byte)'%', (byte)'P', (byte)'D', (byte)'F'] };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = ActorUserId,
            FileName = fileName,
            ContentType = "application/pdf",
            SizeBytes = blob.Content.Length,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = blob.Id,
            FileBlob = blob,
        };
        db.FileBlob.Add(blob);
        db.FileMetadata.Add(metadata);
        await db.SaveChangesAsync();
        return metadata.Id;
    }

    private static async Task<string> StoredNameAsync(OdysseyApiFactory factory, Guid fileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return (await db.FileMetadata.AsNoTracking().SingleAsync(fm => fm.Id == fileId)).FileName;
    }
}
