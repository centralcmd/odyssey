using Odyssey.Dtos;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
