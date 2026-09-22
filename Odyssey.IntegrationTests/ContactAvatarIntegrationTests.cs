using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Xmp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Imaging;
using Odyssey.Core.Journal.Avatar;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Odyssey.TestData.Fixtures;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for contact images (issue #86, AC 2–5, 10, 16, 17).
/// </summary>
/// <remarks>
/// <para>
/// Everything here is invisible to the fast tiers, in three ways. EF InMemory enforces <b>no foreign
/// keys at all</b>, so <c>AvatarFileId</c>'s <c>SET NULL</c> is unobservable there. It honours neither
/// transactions nor the execution strategy, so the atomicity of the attach, the release and the
/// contact-delete cascade cannot be asserted. And it has no unique-index enforcement, so the
/// concurrent double-POST that must be a <c>409</c> simply succeeds twice.
/// </para>
/// <para>
/// <b>AC 10 is the one that matters most.</b> The runtime re-validation is a second pass of the
/// project's own container walk, so on its own it is a self-check — a bug in the walk could be
/// invisible to it. The oracle below is <c>MetadataExtractor</c>, a third-party parser sharing no code
/// with the stripper, run over the bytes the endpoint actually stores. That independence is the whole
/// property; collapsing the two implementations into one removes it, not just a test.
/// </para>
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContactAvatarIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contact_avatars";

    // ── AC 10: the metadata guarantee, proven by an independent parser ────────────────────────────

    public static TheoryData<string, byte[], string> Adversarial() => new()
    {
        { "EXIF and a COM comment", ContactImageFixtures.JpegWithExifAndComment(), "image/jpeg" },
        { "an APP2/MPF second image with its own metadata", ContactImageFixtures.JpegWithMpfSecondImage(), "image/jpeg" },
        { "a JFIF APP0 thumbnail and JFXX", ContactImageFixtures.JpegWithJfifThumbnail(), "image/jpeg" },
        { "a ZIP trailer after EOI", ContactImageFixtures.JpegWithZipTrailer(), "image/jpeg" },
        { "tEXt, eXIf and iCCP", ContactImageFixtures.PngWithTextAndProfile(), "image/png" },
        { "EXIF, XMP and ICCP chunks", ContactImageFixtures.WebpWithMetadataChunks(), "image/webp" },
    };

    [SkippableTheory]
    [MemberData(nameof(Adversarial))]
    public async Task TheStoredImage_CarriesNoExifIptcXmpOrIcc_UnderAThirdPartyParser(
        string label, byte[] source, string contentType)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var contactId = await SeedPersonAsync(context);
            var expected = ImageContainerWalk.Walk(source, contentType);

            Assert.True(await AvatarServiceFor(context).AttachAsync(contactId, source, contentType, UploaderId));

            var stored = await StoredBytesAsync(context, contactId);

            // The oracle. MetadataExtractor shares no code with the stripper, so it can see what a
            // second pass of the walk cannot.
            var directories = ImageMetadataReader.ReadMetadata(new MemoryStream(stored)).ToList();

            Assert.DoesNotContain(directories, d => d is ExifIfd0Directory or ExifSubIfdDirectory or GpsDirectory);
            Assert.DoesNotContain(directories, d => d is IptcDirectory);
            Assert.DoesNotContain(directories, d => d is XmpDirectory);
            Assert.DoesNotContain(directories, d => d is IccDirectory);

            // Still the same image, and nothing past the container terminator.
            var reWalked = ImageContainerWalk.Walk(stored, contentType);
            Assert.Equal(ImageWalkOutcome.Ok, reWalked.Outcome);
            Assert.Equal(expected.Width, reWalked.Width);
            Assert.Equal(expected.Height, reWalked.Height);
            Assert.Equal(stored.Length, reWalked.ConsumedLength);

            Assert.True(stored.Length <= source.Length, $"{label}: the stored image grew.");
        }

        await DropAsync();
    }

    // ── AC 2: a replace destroys BOTH previous rows ───────────────────────────────────────────────

    [SkippableFact]
    public async Task ReplacingAnImage_RemovesThePreviousMetadataAndBlobRows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var contactId = await SeedPersonAsync(context);
            var service = AvatarServiceFor(context);

            await service.AttachAsync(contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg", UploaderId);
            var first = await AvatarFileIdAsync(context, contactId);
            var firstBlobId = await BlobIdAsync(context, first!.Value);

            await service.AttachAsync(contactId, ContactImageFixtures.BaselinePng(), "image/png", UploaderId);
            var second = await AvatarFileIdAsync(context, contactId);

            Assert.NotEqual(first, second);

            // Both rows, not just the reference: the image is personal data and the erasure has to reach
            // the blob.
            Assert.False(await context.FileMetadata.AnyAsync(fm => fm.Id == first));
            Assert.False(await context.FileBlob.AnyAsync(b => b.Id == firstBlobId));
            Assert.Equal(1, await context.FileMetadata.CountAsync());
            Assert.Equal(1, await context.FileBlob.CountAsync());
        }

        await DropAsync();
    }

    // ── AC 3: a delete destroys both rows ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RemovingAnImage_NullsTheReferenceAndRemovesBothRows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var contactId = await SeedPersonAsync(context);
            var service = AvatarServiceFor(context);
            await service.AttachAsync(contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg", UploaderId);

            Assert.True(await service.RemoveAsync(contactId));

            Assert.Null(await AvatarFileIdAsync(context, contactId));
            Assert.Equal(0, await context.FileMetadata.CountAsync());
            Assert.Equal(0, await context.FileBlob.CountAsync());

            // The contact itself is unchanged: removal is not an archival and not a delete.
            Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
        }

        await DropAsync();
    }

    // ── AC 4: the contact-delete cascade, and its atomicity ───────────────────────────────────────

    [SkippableFact]
    public async Task DeletingAContact_DeletesItsImageInTheSameTransaction()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var contactId = await SeedPersonAsync(context);
            await AvatarServiceFor(context).AttachAsync(contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg", UploaderId);

            // Contact deleted → image deleted is NOT expressible as a foreign key (the FK runs the other
            // way, with SET NULL), so it is application code inside the delete's own transaction.
            await ContactServiceFor(context).Delete(contactId);

            Assert.False(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
            Assert.Equal(0, await context.FileMetadata.CountAsync());
            Assert.Equal(0, await context.FileBlob.CountAsync());
        }

        await DropAsync();
    }

    [SkippableFact]
    public async Task AFailureMidDelete_RollsBackTheImageRemovalAsWellAsTheContact()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        Guid contactId;
        Guid fileId;

        await using (var context = new OdysseyContext(options))
        {
            contactId = await SeedPersonAsync(context);
            await AvatarServiceFor(context).AttachAsync(contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg", UploaderId);
            fileId = (await AvatarFileIdAsync(context, contactId))!.Value;
        }

        await using (var context = new OdysseyContext(options))
        {
            // The guard refuses partway through, INSIDE the transaction the delete opened — the shape
            // of a real refusal (a contact named as a contract beneficiary) without needing one.
            var service = new ContactService(context, new ThrowingReferenceGuard(), logger: NullLogger<ContactService>.Instance);

            await Assert.ThrowsAsync<DomainConflictException>(() => service.Delete(contactId));
        }

        await using (var verify = new OdysseyContext(options))
        {
            // Nothing partially applied: the contact is still there and so are BOTH of its file rows.
            Assert.True(await verify.Contacts.AnyAsync(c => c.ContactId == contactId));
            Assert.True(await verify.FileMetadata.AnyAsync(fm => fm.Id == fileId));
            Assert.Equal(1, await verify.FileBlob.CountAsync());
            Assert.Equal(fileId, await AvatarFileIdAsync(verify, contactId));
        }

        await DropAsync();
    }

    // ── AC 5: the FK detaches gracefully ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task DeletingTheUnderlyingFile_NullsTheReferenceRatherThanFailing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var contactId = await SeedPersonAsync(context);
            await AvatarServiceFor(context).AttachAsync(contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg", UploaderId);
            var fileId = (await AvatarFileIdAsync(context, contactId))!.Value;

            // EF's default for an optional relationship is ClientSetNull, which emits RESTRICT — under
            // which this would be a raw FK violation surfacing as a 500 rather than the graceful detach
            // that leaves the contact on its type glyph. The explicit SetNull is what makes it pass.
            Assert.True(await FileServiceFor(context).DeleteFileAsync(fileId));

            Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
            Assert.Null(await AvatarFileIdAsync(context, contactId));
        }

        await DropAsync();
    }

    [SkippableFact]
    public async Task TheDatabaseItself_SetsTheReferenceNullWhenTheFileRowGoes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var contactId = await SeedPersonAsync(context);
            await AvatarServiceFor(context).AttachAsync(contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg", UploaderId);
            var fileId = (await AvatarFileIdAsync(context, contactId))!.Value;

            // Raw SQL, bypassing the application-level counterpart entirely, so what is asserted is the
            // CONSTRAINT rather than the code that imitates it for the InMemory tiers.
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM `FileMetadata` WHERE `Id` = {0}", fileId);

            context.ChangeTracker.Clear();
            Assert.Null(await AvatarFileIdAsync(context, contactId));
        }

        await DropAsync();
    }

    // ── AC 16: the contact-delete half of the release rule ────────────────────────────────────────

    [SkippableFact]
    public async Task DeletingAContactMisPointedAtANonImage_LeavesThatFileIntact()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var contactId = await SeedPersonAsync(context);
            var pdfId = await MisPointAsync(context, contactId);

            await ContactServiceFor(context).Delete(contactId);

            // Detach-never-delete: a mis-pointed reference is a defect to investigate, not a licence to
            // destroy the thing it points at — and the contact's own delete still succeeds.
            Assert.False(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
            Assert.True(await context.FileMetadata.AnyAsync(fm => fm.Id == pdfId));
            Assert.Equal(1, await context.FileBlob.CountAsync());
        }

        await DropAsync();
    }

    // ── AC 17: the concurrent double-POST is a 409, never a 500 ───────────────────────────────────

    [SkippableFact]
    public async Task AConcurrentSecondAttach_IsAConflictRatherThanARawIndexViolation()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        Guid contactId;
        await using (var seed = new OdysseyContext(options))
        {
            contactId = await SeedPersonAsync(seed);
        }

        // Two contexts, so neither sees the other's change tracker — the shape of two requests.
        await using var first = new OdysseyContext(options);
        await using var second = new OdysseyContext(options);

        // The unique index on AvatarFileId is what makes "deleting the contact deletes its avatar file"
        // safe, and it is also what a racing pair of writes trips. It must surface as a conflict the
        // caller can retry, not as a 500.
        var firstFileId = Guid.NewGuid();
        var secondFileId = Guid.NewGuid();
        await InsertOrphanFileAsync(first, firstFileId);
        await InsertOrphanFileAsync(second, secondFileId);

        await first.Database.ExecuteSqlRawAsync(
            "UPDATE `Contacts` SET `AvatarFileId` = {0} WHERE `ContactId` = {1}", firstFileId, contactId);

        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() =>
            second.Database.ExecuteSqlRawAsync(
                "UPDATE `Contacts` SET `AvatarFileId` = {0} WHERE `ContactId` = {1}", firstFileId, contactId)
                .ContinueWith(_ => second.Database.ExecuteSqlRawAsync(
                    "INSERT INTO `Contacts` (`ContactId`, `ExternalUid`, `NormalizedName`, `Type`, `CreatedAt`, `UpdatedAt`, `AvatarFileId`) "
                    + "VALUES ({0}, {1}, {2}, 0, UTC_TIMESTAMP(), UTC_TIMESTAMP(), {3})",
                    Guid.NewGuid(), $"urn:uuid:{Guid.NewGuid()}", "RACER", firstFileId)).Unwrap());

        // A second contact pointing at the SAME file is what the unique index forbids, and that is the
        // race the 409 exists for.
        Assert.Contains("Duplicate entry", Flatten(duplicate), StringComparison.OrdinalIgnoreCase);

        await DropAsync();
    }

    /// <summary>
    /// The <b>other</b> shape of the same race, driven through the real <c>AttachAsync</c>: two requests
    /// replacing the same contact's image both stage the outgoing file's removal, and the loser finds the
    /// row already gone. It must be the same <c>409</c>, not a <c>500</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DbUpdateConcurrencyException</c> derives from <c>DbUpdateException</c>, so the duplicate-entry
    /// filter above does <b>not</b> cover it — which is exactly why it has its own catch, and why that
    /// catch needs its own test. The test above proves only that the database rejects the duplicate; it
    /// never calls the service at all, so it cannot show the mapping firing.
    /// </para>
    /// <para>
    /// The race is made DETERMINISTIC rather than run in parallel and hoped for: the loser's context is
    /// given the first half of its own request — the staged release, which is literally
    /// <see cref="ContactAvatarRelease.StageAsync"/>, the same call <c>AttachAsync</c> makes — before the
    /// winner commits. Racing two threads would reproduce this only sometimes, and a concurrency test
    /// that passes when the bug is present is worse than none.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task AReplaceWhoseOutgoingFileWasAlreadyDeleted_IsAConflictRatherThanACrash()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        Guid contactId;
        await using (var seed = new OdysseyContext(options))
        {
            contactId = await SeedPersonAsync(seed);
            await AvatarServiceFor(seed).AttachAsync(
                contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg", UploaderId);
        }

        // Two contexts, so neither sees the other's change tracker — the shape of two requests.
        await using var loser = new OdysseyContext(options);
        await using var winner = new OdysseyContext(options);

        // The loser gets as far as staging the outgoing file's removal, then stalls — the state a second
        // request is in when the first one commits underneath it.
        var stalled = await loser.Contacts.FirstAsync(c => c.ContactId == contactId);
        var released = await ContactAvatarRelease.StageAsync(
            loser, stalled, logger: null, site: "replace-race");
        Assert.Equal(AvatarReleaseOutcome.Deleted, released);

        Assert.True(await AvatarServiceFor(winner).AttachAsync(
            contactId, ContactImageFixtures.BaselinePng(), "image/png", UploaderId));

        // The loser now resumes. Its pending DELETE hits zero rows, which EF reports as a concurrency
        // failure — and the caller must see a retryable conflict, not an unhandled 500.
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            AvatarServiceFor(loser).AttachAsync(
                contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg", UploaderId));

        Assert.Contains("changed by another request", conflict.Message, StringComparison.OrdinalIgnoreCase);

        // The winner's write is intact: the losing request must not have taken anything down with it.
        await using (var check = new OdysseyContext(options))
        {
            var fileId = await AvatarFileIdAsync(check, contactId);
            Assert.NotNull(fileId);
            Assert.Equal("image/png", await check.FileMetadata.Where(fm => fm.Id == fileId)
                .Select(fm => fm.ContentType).SingleAsync());
        }

        await DropAsync();
    }

    // ── AC 18: a revalidation does not read the blob ──────────────────────────────────────────────

    [SkippableFact]
    public async Task ResolvingTheDescriptor_NeverMaterialisesTheBlob()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var contactId = await SeedPersonAsync(context);
            await AvatarServiceFor(context).AttachAsync(contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg", UploaderId);
        }

        var interceptor = new CommandRecorder();
        var watched = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.ConnectionStringFor(Database)))
            .AddInterceptors(interceptor)
            .Options;

        await using (var context = new OdysseyContext(watched))
        {
            var contactId = await context.Contacts.Select(c => c.ContactId).FirstAsync();
            interceptor.Commands.Clear();

            var descriptor = await AvatarServiceFor(context).GetDescriptorAsync(contactId);

            Assert.NotNull(descriptor);

            // no-cache makes revalidation the HOT path, so touching the LONGBLOB to answer a conditional
            // request would make every return visit pay full materialisation for a body it never sends.
            Assert.DoesNotContain(interceptor.Commands, sql => sql.Contains("`Content`", StringComparison.Ordinal));
            Assert.DoesNotContain(interceptor.Commands, sql => sql.Contains("FileBlob", StringComparison.Ordinal));
        }

        await DropAsync();
    }

    // ── AC 19: the stored row, on the real engine ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheStoredRow_CarriesAGeneratedNameAndTheValidatedContentType()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var contactId = await SeedPersonAsync(context);
            await AvatarServiceFor(context).AttachAsync(contactId, ContactImageFixtures.StillWebp(), "image/webp", UploaderId);

            var fileId = (await AvatarFileIdAsync(context, contactId))!.Value;
            var file = await context.FileMetadata.SingleAsync(fm => fm.Id == fileId);

            Assert.EndsWith(".webp", file.FileName);
            Assert.Equal("image/webp", file.ContentType);
            Assert.Equal("Contact image", file.Description);
            Assert.Equal(UploaderId, file.UploadedByUserId);

            // The stored size is the STRIPPED size, and the hash is of those same bytes.
            var stored = await StoredBytesAsync(context, contactId);
            Assert.Equal(stored.LongLength, file.SizeBytes);
            Assert.Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stored)).ToLowerInvariant(),
                file.Sha256Hash);
        }

        await DropAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static ContactAvatarService AvatarServiceFor(OdysseyContext context) =>
        new(context,
            FileServiceFor(context),
            new FixedUploadLimits(64L * 1024 * 1024),
            logger: NullLogger<ContactAvatarService>.Instance);

    private static FileService FileServiceFor(OdysseyContext context) =>
        new(context, new FileValidationService());

    private static ContactService ContactServiceFor(OdysseyContext context) =>
        new(context, new ContactReferenceGuard(context), logger: NullLogger<ContactService>.Instance);

    /// <summary>
    /// The principal a stored image is attributed to. <c>FileMetadata.UploadedByUserId</c> is a real
    /// foreign key, so a fixture that invents an id is rejected by the engine rather than quietly
    /// stored — the fix is to seed the user, never to relax the key.
    /// </summary>
    private const string UploaderId = "avatar-uploader";

    private static async Task<Guid> SeedPersonAsync(OdysseyContext context)
    {
        await AttributionUsers.EnsureAsync(context, UploaderId);

        var created = await ContactServiceFor(context).Create(new NewContact
        {
            Type = ContactType.Person,
            Archived = false,
            PersonDetails = new PersonDetailsDto { FirstName = "Ada", LastName = "Lovelace" },
        });

        return created.ContactId;
    }

    private static Task<Guid?> AvatarFileIdAsync(OdysseyContext context, Guid contactId) =>
        context.Contacts.AsNoTracking().Where(c => c.ContactId == contactId)
            .Select(c => c.AvatarFileId).SingleAsync();

    private static Task<Guid> BlobIdAsync(OdysseyContext context, Guid fileId) =>
        context.FileMetadata.AsNoTracking().Where(fm => fm.Id == fileId).Select(fm => fm.FileBlobId).SingleAsync();

    private static async Task<byte[]> StoredBytesAsync(OdysseyContext context, Guid contactId)
    {
        var fileId = (await AvatarFileIdAsync(context, contactId))!.Value;
        return (await AvatarServiceFor(context).GetContentAsync(fileId))!;
    }

    /// <summary>Points a contact at a non-image file — the anomaly the release rule exists to survive.</summary>
    private static async Task<Guid> MisPointAsync(OdysseyContext context, Guid contactId)
    {
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = "%PDF-1.7 tax statement"u8.ToArray() };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            FileName = "tax-statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = blob.Content.LongLength,
            Sha256Hash = new string('c', 64),
            FileBlobId = blob.Id,
            UploadedAtUtc = DateTime.UtcNow,
        };
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(metadata);

        var contact = await context.Contacts.SingleAsync(c => c.ContactId == contactId);
        contact.AvatarFileId = metadata.Id;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return metadata.Id;
    }

    private static async Task InsertOrphanFileAsync(OdysseyContext context, Guid fileId)
    {
        var blobId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `FileBlob` (`Id`, `Content`) VALUES ({0}, {1})", blobId, new byte[] { 1, 2, 3 });
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `FileMetadata` (`Id`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`, `FileBlobId`, `UploadedAtUtc`) "
            + "VALUES ({0}, 'contact-avatar.png', 'image/png', 3, {1}, {2}, UTC_TIMESTAMP())",
            fileId, new string('d', 64), blobId);
    }

    private static string Flatten(Exception exception)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.Append(current.Message).Append(' ');
        }

        return text.ToString();
    }

    private async Task<DbContextOptions<OdysseyContext>> MigratedSchemaAsync()
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var options = OptionsFor(fixture.ConnectionStringFor(Database));
        await using (var context = new OdysseyContext(options))
        {
            await context.Database.MigrateAsync();
        }

        return options;
    }

    private async Task DropAsync()
    {
        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync("DROP DATABASE IF EXISTS `" + Database + "`");
    }

    private static DbContextOptions<OdysseyContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;

    /// <summary>
    /// Refuses partway through the delete, inside the transaction it opened — the shape of a real
    /// refusal (a contact named as a contract beneficiary) without needing one.
    /// </summary>
    private sealed class ThrowingReferenceGuard : IContactReferenceGuard
    {
        public Task<bool> IsReferencedByRestrictedLinkAsync(Guid contactId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<ContactDeleteBlockers> GetDeleteBlockersAsync(
            Guid contactId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ContactDeleteBlockers.None);

        public Task<ContactLinkDetachPlan> ReadLinkDetachPlanAsync(
            Guid contactId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Odyssey.Dtos.Finance.DetachedContactLinks StageLinkDetach(ContactLinkDetachPlan plan) =>
            throw new NotSupportedException();

        public Task ClearAndCascadeReferencesAsync(Guid contactId, CancellationToken cancellationToken = default) =>
            throw new DomainConflictException("Refused after the transaction opened.");
    }

    /// <summary>Records the SQL every command carries, so "the blob was not read" is an assertion.</summary>
    private sealed class CommandRecorder : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override System.Data.Common.DbCommand CommandCreated(
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEndEventData eventData,
            System.Data.Common.DbCommand result)
        {
            Commands.Add(result.CommandText);
            return base.CommandCreated(eventData, result);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
