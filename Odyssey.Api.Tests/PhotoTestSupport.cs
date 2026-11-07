using Odyssey.Dtos;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Odyssey.Dtos.Journal;
using Odyssey.Testing.FileStorage;

namespace Odyssey.Api.Tests;

/// <summary>Deterministic stub extractor so photo tests can drive extraction-dependent behaviour
/// (caller precedence, keyword auto-create, metadata fill) without crafting real EXIF/IPTC bytes.</summary>
public sealed class StubPhotoMetadataExtractor(PhotoMetadata metadata) : IPhotoMetadataExtractor
{
    public PhotoMetadata Extract(byte[] content) => metadata;
}

/// <summary>Shared seeding helpers for the Photos-module API tests.</summary>
public static class PhotoTestSupport
{
    /// <summary>
    /// <c>POST /api/photos</c> as the client sends it (issue #428 §5.1): the image as part <c>file</c> plus the
    /// fields, each id list as the repeated field name. Fresh bytes by default, so two uploads never collide on the
    /// duplicate check.
    /// </summary>
    public static Task<HttpResponseMessage> PostPhotoAsync(
        HttpClient client, NewPhotoUpload? fields = null, byte[]? bytes = null, string fileName = "photo.jpg",
        string contentType = "image/jpeg")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes ?? PhotoTestData.Jpeg(Random.Shared.Next()));
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, "file", fileName);
        foreach (var (name, value) in FormFields(fields ?? new NewPhotoUpload()))
        {
            form.Add(new StringContent(value), name);
        }

        return client.PostAsync("/api/photos", form);
    }

    /// <summary><see cref="PostPhotoAsync"/>, asserting success and returning the created photo.</summary>
    public static async Task<ExistingPhoto> UploadPhotoAsync(
        HttpClient client, NewPhotoUpload? fields = null, byte[]? bytes = null, string fileName = "photo.jpg")
    {
        var post = await PostPhotoAsync(client, fields, bytes, fileName);
        if (!post.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Photo upload failed: {(int)post.StatusCode} {await post.Content.ReadAsStringAsync()}");
        }

        return (await post.Content.ReadFromJsonAsync<ExistingPhoto>())!;
    }

    private static IEnumerable<(string Name, string Value)> FormFields(NewPhotoUpload f)
    {
        var invariant = CultureInfo.InvariantCulture;
        if (f.Title is not null) yield return ("title", f.Title);
        if (f.Caption is not null) yield return ("caption", f.Caption);
        if (f.TakenAt is { } takenAt) yield return ("takenAt", takenAt.ToString("O", invariant));
        if (f.CapturedLatitude is { } lat) yield return ("capturedLatitude", lat.ToString("R", invariant));
        if (f.CapturedLongitude is { } lon) yield return ("capturedLongitude", lon.ToString("R", invariant));
        if (f.LocationName is not null) yield return ("locationName", f.LocationName);
        if (f.PixelWidth is { } w) yield return ("pixelWidth", w.ToString(invariant));
        if (f.PixelHeight is { } h) yield return ("pixelHeight", h.ToString(invariant));
        if (f.Favourite) yield return ("favourite", "true");
        foreach (var id in f.TagIds) yield return ("tagIds", id.ToString());
        foreach (var id in f.PersonContactIds) yield return ("personContactIds", id.ToString());
        foreach (var id in f.AlbumIds) yield return ("albumIds", id.ToString());
    }

    /// <summary>A legacy photo (issue #428: created before photos owned their bytes), inserted directly.</summary>
    public static async Task<(Guid PhotoId, Guid FileId)> SeedLegacyPhotoAsync(
        WebApplicationFactory<Program> factory, string? uploaderId, string fileName = "photo.jpg", byte[]? bytes = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        var (photo, file) = PhotoTestData.StageLegacyPhoto(context, bytes, fileName, uploaderId);
        await context.SaveChangesAsync();
        return (photo.PhotoId, file.Id);
    }

    public static async Task<Guid> SeedImageFileAsync(
        WebApplicationFactory<Program> factory, string uploaderId, string fileName = "photo.jpg",
        string contentType = "image/jpeg", byte[]? content = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = content ?? [1, 2, 3] };
        var metadataId = Guid.NewGuid();
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = metadataId,
            UploadedByUserId = uploaderId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = blob.Content.Length,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = blob.Id,
            FileBlob = blob,
        });
        await context.SaveChangesAsync();
        return metadataId;
    }

    public static async Task<Guid> SeedPersonAsync(
        WebApplicationFactory<Program> factory, string name = "Ada Lovelace",
        Odyssey.Dtos.ContactType type = Odyssey.Dtos.ContactType.Person)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        var contact = new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = name.Trim().ToUpperInvariant(),
            Type = type,
        };
        if (type == Odyssey.Dtos.ContactType.Person)
            contact.PersonDetails = new() { FirstName = name, LastName = string.Empty };
        else
            contact.OrganizationDetails = new() { LegalName = name };
        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return id;
    }

    public static async Task<Guid> SeedPhotoTagAsync(
        WebApplicationFactory<Program> factory, string name, bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.PhotoTags.Add(new PhotoTag
        {
            PhotoTagId = id,
            Name = name,
            Archived = archived ? DateTime.UtcNow : null,
        });
        await context.SaveChangesAsync();
        return id;
    }
}
