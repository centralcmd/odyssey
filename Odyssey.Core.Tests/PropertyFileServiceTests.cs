using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextPropertyFileType = Odyssey.Context.PropertyFileType;
using DtoPropertyFileType = Odyssey.Dtos.Finance.PropertyFileType;

namespace Odyssey.Core.Tests;

/// <summary>
/// Fast coverage for <see cref="PropertyFileService"/> (issue #210): attach, list order and scoping,
/// the full-replacement update, detach, the duplicate conflict, the issuer check, the shared
/// <see cref="DocumentValidity"/> rules, and the unknown-property answers. Also AC 17 (a property delete
/// removes its document links but not the files), AC 18 (<see cref="FileReferenceGuard"/> names a
/// property document), and the Context/Dtos enum parity the Mapster registration relies on.
/// </summary>
public class PropertyFileServiceTests
{
    private const string TestUserId = "test-user";
    private static readonly DateTimeOffset Start = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class MovableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PropertyFileService Service(OdysseyContext context, TimeProvider? clock = null) =>
        new(context, TestContextFactory.ContactLookup(context), clock ?? new MovableTimeProvider(Start));

    private static async Task<Guid> SeedProperty(OdysseyContext context, string name = "Storgata 14") =>
        (await new PropertyService(context).Create(PropertyTestData.House(name), userId: null)).PropertyId;

    private static async Task<Guid> SeedFile(OdysseyContext context, string fileName = "deed.pdf")
    {
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = TestUserId,
            FileName = fileName,
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            UploadedAtUtc = Start.UtcDateTime,
            FileBlobId = blob.Id,
            FileBlob = blob,
        };
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(metadata);
        await context.SaveChangesAsync();
        return metadata.Id;
    }

    private static async Task<Guid> SeedContact(OdysseyContext context)
    {
        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "kartverket",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Kartverket" },
        };
        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return contact.ContactId;
    }

    // ── Attach ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AttachFile_StoresAndReturnsEveryField_AttributedToTheCaller()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);
        var contactId = await SeedContact(context);
        var validFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var validTo = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var issuedAt = new DateTime(2025, 12, 18, 0, 0, 0, DateTimeKind.Utc);

        var attached = await service.AttachFile(propertyId, new AttachPropertyFileRequest
        {
            FileMetadataId = fileId,
            FileType = DtoPropertyFileType.Deed,
            ValidFrom = validFrom,
            ValidTo = validTo,
            IssuedAt = issuedAt,
            IssuedBy = contactId,
        }, TestUserId);

        Assert.NotNull(attached);
        Assert.Equal(propertyId, attached!.PropertyId);
        Assert.Equal(fileId, attached.FileMetadata.Id);
        Assert.Equal("deed.pdf", attached.FileMetadata.FileName);
        Assert.Equal(DtoPropertyFileType.Deed, attached.FileType);
        Assert.Equal(TestUserId, attached.AttachedByUserId);
        Assert.Equal(Start.UtcDateTime, attached.AttachedAtUtc);
        Assert.Equal(validFrom, attached.ValidFrom);
        Assert.Equal(validTo, attached.ValidTo);
        Assert.Equal(issuedAt, attached.IssuedAt);
        Assert.Equal(contactId, attached.IssuedBy);
        Assert.Null(attached.AttachedByName);

        var stored = await context.PropertyFiles.SingleAsync();
        Assert.Equal(attached.PropertyFileId, stored.PropertyFileId);
        Assert.Equal(ContextPropertyFileType.Deed, stored.FileType);
    }

    [Fact]
    public async Task AttachFile_WithoutTypeOrMetadata_DefaultsToOther_AndStoresNulls()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);

        var attached = await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = fileId }, TestUserId);

        Assert.Equal(DtoPropertyFileType.Other, attached!.FileType);
        Assert.Null(attached.ValidFrom);
        Assert.Null(attached.ValidTo);
        Assert.Null(attached.IssuedAt);
        Assert.Null(attached.IssuedBy);
    }

    [Fact]
    public async Task AttachFile_UnknownProperty_ReturnsNull_AndStoresNothing()
    {
        await using var context = TestContextFactory.Create();
        var fileId = await SeedFile(context);

        var attached = await Service(context).AttachFile(
            Guid.NewGuid(), new AttachPropertyFileRequest { FileMetadataId = fileId }, TestUserId);

        Assert.Null(attached);
        Assert.False(await context.PropertyFiles.AnyAsync());
    }

    [Fact]
    public async Task AttachFile_Twice_IsAConflict_AndStoresOneRow()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);
        var request = new AttachPropertyFileRequest { FileMetadataId = fileId };

        await service.AttachFile(propertyId, request, TestUserId);
        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.AttachFile(propertyId, request, TestUserId));

        Assert.Contains(fileId.ToString(), error.Message);
        Assert.Single(context.PropertyFiles);
    }

    [Fact]
    public async Task AttachFile_SameFileToTwoProperties_IsAllowed()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var first = await SeedProperty(context, "First");
        var second = await SeedProperty(context, "Second");
        var fileId = await SeedFile(context);

        await service.AttachFile(first, new AttachPropertyFileRequest { FileMetadataId = fileId }, TestUserId);
        await service.AttachFile(second, new AttachPropertyFileRequest { FileMetadataId = fileId }, TestUserId);

        Assert.Equal(2, await context.PropertyFiles.CountAsync());
    }

    [Fact]
    public async Task AttachFile_UnknownIssuer_ThrowsOnIssuedBy_AndStoresNothing()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AttachFile(propertyId, new AttachPropertyFileRequest
            {
                FileMetadataId = fileId,
                IssuedBy = Guid.NewGuid(),
            }, TestUserId));

        Assert.True(error.Errors!.ContainsKey(nameof(AttachPropertyFileRequest.IssuedBy)));
        Assert.False(await context.PropertyFiles.AnyAsync());
    }

    [Fact]
    public async Task AttachFile_InvertedValidity_ThrowsOnValidTo_AndStoresNothing()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AttachFile(propertyId, new AttachPropertyFileRequest
            {
                FileMetadataId = fileId,
                ValidFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            }, TestUserId));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.ValidToField));
        Assert.False(await context.PropertyFiles.AnyAsync());
    }

    [Fact]
    public async Task AttachFile_DateOutsideTheStorableRange_ThrowsOnThatDate()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AttachFile(propertyId, new AttachPropertyFileRequest
            {
                FileMetadataId = fileId,
                IssuedAt = new DateTime(202, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            }, TestUserId));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.IssuedAtField));
        Assert.False(await context.PropertyFiles.AnyAsync());
    }

    [Fact]
    public async Task AttachFile_LocalKindDate_IsStoredNormalisedToUtc()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);
        var local = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Local);

        await service.AttachFile(propertyId, new AttachPropertyFileRequest
        {
            FileMetadataId = fileId,
            ValidFrom = local,
        }, TestUserId);

        var stored = await context.PropertyFiles.SingleAsync();
        Assert.Equal(local.ToUniversalTime(), stored.ValidFrom);
    }

    // ── List ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFiles_NullForAnUnknownProperty_EmptyForOneWithoutDocuments()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);

        Assert.Null(await service.GetFiles(Guid.NewGuid()));
        Assert.Empty((await service.GetFiles(await SeedProperty(context)))!);
    }

    [Fact]
    public async Task GetFiles_IsOldestAttachmentFirst_AndScopedToTheProperty()
    {
        await using var context = TestContextFactory.Create();
        var clock = new MovableTimeProvider(Start);
        var service = Service(context, clock);
        var propertyId = await SeedProperty(context, "Mine");
        var otherId = await SeedProperty(context, "Other");
        var later = await SeedFile(context, "a-later.pdf");
        var earlier = await SeedFile(context, "z-earlier.pdf");
        var elsewhere = await SeedFile(context, "elsewhere.pdf");

        await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = later }, TestUserId);
        clock.Now = Start.AddDays(-1);
        await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = earlier }, TestUserId);
        clock.Now = Start.AddDays(-2);
        await service.AttachFile(otherId, new AttachPropertyFileRequest { FileMetadataId = elsewhere }, TestUserId);

        var files = await service.GetFiles(propertyId);

        Assert.Equal(["z-earlier.pdf", "a-later.pdf"], files!.Select(f => f.FileMetadata.FileName));
        Assert.All(files!, f => Assert.Equal(propertyId, f.PropertyId));
        Assert.Equal(["elsewhere.pdf"], (await service.GetFiles(otherId))!.Select(f => f.FileMetadata.FileName));
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateFile_ReplacesTypeAndEveryField()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);
        var contactId = await SeedContact(context);
        await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = fileId }, TestUserId);
        var validFrom = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var validTo = new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var issuedAt = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);

        var updated = await service.UpdateFile(propertyId, fileId, new UpdatePropertyFileRequest
        {
            FileType = DtoPropertyFileType.Insurance,
            ValidFrom = validFrom,
            ValidTo = validTo,
            IssuedAt = issuedAt,
            IssuedBy = contactId,
        });

        Assert.True(updated);
        var stored = await context.PropertyFiles.AsNoTracking().SingleAsync();
        Assert.Equal(ContextPropertyFileType.Insurance, stored.FileType);
        Assert.Equal(validFrom, stored.ValidFrom);
        Assert.Equal(validTo, stored.ValidTo);
        Assert.Equal(issuedAt, stored.IssuedAt);
        Assert.Equal(contactId, stored.IssuedBy);
        Assert.Equal(TestUserId, stored.AttachedByUserId);
    }

    /// <summary>The update is a full replacement: an omitted date or issuer clears the stored value.</summary>
    [Fact]
    public async Task UpdateFile_OmittedFields_ClearTheStoredValues()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);
        var contactId = await SeedContact(context);
        await service.AttachFile(propertyId, new AttachPropertyFileRequest
        {
            FileMetadataId = fileId,
            FileType = DtoPropertyFileType.Warranty,
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidTo = new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuedAt = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuedBy = contactId,
        }, TestUserId);

        Assert.True(await service.UpdateFile(propertyId, fileId, new UpdatePropertyFileRequest
        {
            FileType = DtoPropertyFileType.Receipt,
        }));

        var stored = await context.PropertyFiles.AsNoTracking().SingleAsync();
        Assert.Equal(ContextPropertyFileType.Receipt, stored.FileType);
        Assert.Null(stored.ValidFrom);
        Assert.Null(stored.ValidTo);
        Assert.Null(stored.IssuedAt);
        Assert.Null(stored.IssuedBy);
    }

    [Fact]
    public async Task UpdateFile_UnknownPropertyOrUnattachedFile_ReturnsFalse()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var otherId = await SeedProperty(context, "Other");
        var fileId = await SeedFile(context);
        await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = fileId }, TestUserId);
        var request = new UpdatePropertyFileRequest { FileType = DtoPropertyFileType.Tax };

        Assert.False(await service.UpdateFile(Guid.NewGuid(), fileId, request));
        Assert.False(await service.UpdateFile(propertyId, Guid.NewGuid(), request));
        // The file exists and is attached — but to a different property, so it is not addressable here.
        Assert.False(await service.UpdateFile(otherId, fileId, request));
        Assert.Equal(ContextPropertyFileType.Other, (await context.PropertyFiles.AsNoTracking().SingleAsync()).FileType);
    }

    [Fact]
    public async Task UpdateFile_UnknownIssuer_ThrowsOnIssuedBy_AndChangesNothing()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);
        await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = fileId }, TestUserId);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateFile(propertyId, fileId, new UpdatePropertyFileRequest
            {
                FileType = DtoPropertyFileType.Deed,
                IssuedBy = Guid.NewGuid(),
            }));

        Assert.True(error.Errors!.ContainsKey(nameof(UpdatePropertyFileRequest.IssuedBy)));
        var stored = await context.PropertyFiles.AsNoTracking().SingleAsync();
        Assert.Equal(ContextPropertyFileType.Other, stored.FileType);
        Assert.Null(stored.IssuedBy);
    }

    [Fact]
    public async Task UpdateFile_InvertedValidity_ThrowsOnValidTo()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);
        await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = fileId }, TestUserId);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateFile(propertyId, fileId, new UpdatePropertyFileRequest
            {
                FileType = DtoPropertyFileType.Deed,
                ValidFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            }));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.ValidToField));
    }

    // ── Detach ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetachFile_RemovesTheLinkOnly_AndReportsAbsence()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);
        await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = fileId }, TestUserId);

        Assert.False(await service.DetachFile(Guid.NewGuid(), fileId));
        Assert.True(await service.DetachFile(propertyId, fileId));
        Assert.False(await service.DetachFile(propertyId, fileId));

        Assert.False(await context.PropertyFiles.AnyAsync());
        Assert.True(await context.FileMetadata.AnyAsync(f => f.Id == fileId));
        Assert.Single(context.FileBlob);
    }

    [Fact]
    public async Task IsFileAttached_IsScopedToThePair()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var otherId = await SeedProperty(context, "Other");
        var fileId = await SeedFile(context);
        await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = fileId }, TestUserId);

        Assert.True(await service.IsFileAttached(propertyId, fileId));
        Assert.False(await service.IsFileAttached(otherId, fileId));
        Assert.True(await service.PropertyExists(otherId));
        Assert.False(await service.PropertyExists(Guid.NewGuid()));
    }

    // ── AC 17: a property delete removes its links, never the files ───────────

    [Fact]
    public async Task PropertyDelete_RemovesItsDocumentLinks_ButKeepsTheFilesAndOtherPropertiesLinks()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var propertyId = await SeedProperty(context);
        var otherId = await SeedProperty(context, "Other");
        var first = await SeedFile(context, "deed.pdf");
        var second = await SeedFile(context, "valuation.pdf");
        await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = first }, TestUserId);
        await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = second }, TestUserId);
        await service.AttachFile(otherId, new AttachPropertyFileRequest { FileMetadataId = first }, TestUserId);

        Assert.True(await new PropertyService(context).Delete(propertyId, userId: null));

        var remaining = Assert.Single(context.PropertyFiles);
        Assert.Equal(otherId, remaining.PropertyId);
        Assert.Equal(2, await context.FileMetadata.CountAsync());
        Assert.Equal(2, await context.FileBlob.CountAsync());
    }

    // ── AC 18: the file-reference guard names a property document ─────────────

    [Fact]
    public async Task FileReferenceGuard_NamesAPropertyDocument_OnlyWhileAttached()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var guard = new FileReferenceGuard(context);
        var propertyId = await SeedProperty(context);
        var fileId = await SeedFile(context);

        Assert.Empty(await guard.DescribeNonPhotoReferencesAsync(fileId));

        await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = fileId }, TestUserId);
        Assert.Equal(["a property document"], await guard.DescribeNonPhotoReferencesAsync(fileId));

        await service.DetachFile(propertyId, fileId);
        Assert.Empty(await guard.DescribeNonPhotoReferencesAsync(fileId));
    }

    // ── The two enums and their Mapster registration ──────────────────────────

    [Fact]
    public void PropertyFileType_ContextAndDtoEnums_ShareEveryNameAndOrdinal()
    {
        var context = Enum.GetValues<ContextPropertyFileType>().Select(v => (v.ToString(), (int)v)).ToList();
        var dto = Enum.GetValues<DtoPropertyFileType>().Select(v => (v.ToString(), (int)v)).ToList();

        Assert.Equal(context, dto);
        Assert.Equal(12, dto.Count);
        Assert.Equal(0, (int)DtoPropertyFileType.Other);
        Assert.Equal(11, (int)DtoPropertyFileType.Drawing);
    }

    public static TheoryData<DtoPropertyFileType> DtoMembers()
    {
        var data = new TheoryData<DtoPropertyFileType>();
        foreach (var member in Enum.GetValues<DtoPropertyFileType>())
            data.Add(member);
        return data;
    }

    [Theory]
    [MemberData(nameof(DtoMembers))]
    public void PropertyFileType_MapsterRoundTripsEveryMember(DtoPropertyFileType member)
    {
        MapsterConfig.Register();

        var persisted = member.Adapt<ContextPropertyFileType>();
        Assert.Equal(member.ToString(), persisted.ToString());
        Assert.Equal((int)member, (int)persisted);

        Assert.Equal(member, persisted.Adapt<DtoPropertyFileType>());
    }

    [Fact]
    public void PropertyFileType_AnUndefinedOrdinal_DegradesToOther_InBothDirections()
    {
        MapsterConfig.Register();

        Assert.Equal(DtoPropertyFileType.Other, ((ContextPropertyFileType)99).Adapt<DtoPropertyFileType>());
        Assert.Equal(ContextPropertyFileType.Other, ((DtoPropertyFileType)99).Adapt<ContextPropertyFileType>());
    }
}
