using System.Security.Cryptography;
using Bogus;
using Odyssey.Context;
using Odyssey.TestData.Catalog;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic journal entries for the Journal module (issue #311). Anchored to
/// <see cref="DemoDataDefaults.AnchorDate"/> so entry dates and derived read state are stable.
///
/// Entries exercise every read surface: tagged entries (linked to
/// <see cref="JournalTagGenerator"/> ids), contact-linked entries (soft references to the
/// existing <see cref="Catalog.Contacts"/> roster — no contact is created here), entries
/// carrying first-class photos and generic file attachments, an edited entry (last-editor stamped),
/// and one archived entry so the archived filter has data.
///
/// The Journal rows keep only file <em>references</em>, so this generator also provisions the backing
/// Files-store records (<see cref="FileBlob"/> + <see cref="FileMetadata"/>): real browser-renderable
/// PNGs for photos and tiny valid PDFs for attachments. They are returned separately
/// (<see cref="Result.FileBlobs"/> / <see cref="Result.FileMetadata"/>) so the seeder can write them
/// with the rest of the Files store — which it must do first, since the references are real FKs. Authors are the seeded non-Guest demo users (Guest is excluded from the module).
/// </summary>
public static class JournalEntryGenerator
{
    /// <summary>
    /// Flat result lists (mirrors the seeder's <c>AddRangeAsync</c> pattern): the journal rows and the
    /// photo/attachment blobs + metadata they reference, kept apart so the seeder can write the Files
    /// store first. Nav collections are left empty so nothing double-inserts.
    /// </summary>
    public sealed record Result(
        IReadOnlyList<JournalEntry> Entries,
        IReadOnlyList<JournalEntryTag> EntryTags,
        IReadOnlyList<JournalEntryContact> EntryContacts,
        IReadOnlyList<JournalEntryPhoto> Photos,
        IReadOnlyList<JournalEntryAttachment> Attachments,
        IReadOnlyList<FileBlob> FileBlobs,
        IReadOnlyList<FileMetadata> FileMetadata,
        // Photo Library unification (issue #321 v4): a journal entry links a library Photo by PhotoId, so
        // each journal photo file also gets a shared library Photo record (no double-seeding the file).
        IReadOnlyList<Photo> LibraryPhotos);

    private sealed record EntrySpec(
        string Key,
        string Title,
        string Content,
        DateTime EntryDate,
        string? Location,
        string AuthorRole,
        string? EditorRole,
        string[] TagNames,
        string[] ContactNames,
        int PhotoCount,
        int AttachmentCount,
        bool Archived);

    public static Guid IdFor(string key) => DeterministicGuid.From($"journal-entry::{key}");

    /// <summary>
    /// Builds the journal-entry graph. <paramref name="anchor"/> is the reference "today"; entry
    /// dates are placed relative to it. Deterministic: the same anchor always yields byte-identical
    /// output (photo/attachment bytes included).
    /// </summary>
    public static Result Generate(DateTime anchor)
    {
        // Seed Bogus once so the filler entries below are reproducible; curated specs don't use it.
        Randomizer.Seed = new Random(RandomizerSeed);

        var specs = new List<EntrySpec>
        {
            new(
                "kitchen-reno", "Kitchen renovation kickoff",
                "Signed off on the new kitchen layout today. Ordered the cabinets and booked the "
                    + "fitter for next month. Keeping every quote and receipt attached here so the "
                    + "budget stays honest.",
                anchor.AddDays(-410), "Home",
                AuthorRole: "Owner", EditorRole: "Admin",
                TagNames: ["Home", "Finance"],
                ContactNames: [Contacts.CityPowerWater],
                PhotoCount: 2, AttachmentCount: 1, Archived: false),

            new(
                "coast-trip", "Long weekend on the coast",
                "Drove out to the coast for three days. Weather held, food was excellent, and the "
                    + "cottage was everything the photos promised. Note to self: book the same place "
                    + "next spring before it fills up.",
                anchor.AddDays(-300), "Whitby, North Yorkshire",
                AuthorRole: "User", EditorRole: null,
                TagNames: ["Travel", "Personal"],
                ContactNames: [Contacts.Delta, Contacts.Uber],
                PhotoCount: 3, AttachmentCount: 0, Archived: false),

            new(
                "budget-review", "Annual budget review",
                "Went through the whole year's spending against the budget. Groceries crept up but "
                    + "travel came in under. Rebalanced the savings transfer and pulled the summary "
                    + "into the attached statement.",
                anchor.AddDays(-190), "Home office",
                AuthorRole: "Owner", EditorRole: null,
                TagNames: ["Finance"],
                ContactNames: [Contacts.FirstNationalBank, Contacts.Vanguard],
                PhotoCount: 0, AttachmentCount: 1, Archived: false),

            new(
                "doctor-visit", "Annual check-up notes",
                "Routine annual check-up. Everything looks good; asked to repeat the bloods in six "
                    + "months. Prescription renewed. No follow-up needed before then.",
                anchor.AddDays(-120), "BlueCross Clinic",
                AuthorRole: "User", EditorRole: null,
                TagNames: ["Health", "Personal"],
                ContactNames: [Contacts.BlueCross],
                PhotoCount: 0, AttachmentCount: 0, Archived: false),

            new(
                "espresso-machine", "New espresso machine",
                "Finally replaced the old drip machine. The upgrade is night and day. Logging the "
                    + "purchase and the setup photo so I remember the grind settings that worked.",
                anchor.AddDays(-60), "Home",
                AuthorRole: "Admin", EditorRole: null,
                TagNames: ["Personal", "Ideas"],
                ContactNames: [Contacts.Hm],
                PhotoCount: 1, AttachmentCount: 0, Archived: false),

            new(
                "apartment-move", "Moved apartments",
                "Move-in day. Boxes everywhere but the important rooms are set up. Snapped the meter "
                    + "readings for the record and kept the inventory checklist attached.",
                anchor.AddDays(-25), "New apartment",
                AuthorRole: "Owner", EditorRole: null,
                TagNames: ["Home", "Milestones"],
                ContactNames: [Contacts.Landlord],
                PhotoCount: 1, AttachmentCount: 1, Archived: false),

            new(
                "gym-plan", "Cancelled gym plan (superseded)",
                "Old note about the discontinued gym membership plan. Kept only for the record — "
                    + "superseded by the new arrangement.",
                anchor.AddDays(-520), null,
                AuthorRole: "User", EditorRole: null,
                TagNames: ["Old Notes"],
                ContactNames: [],
                PhotoCount: 0, AttachmentCount: 0, Archived: true),
        };

        // A few Bogus-generated filler entries for realistic list volume/pagination.
        var faker = new Faker();
        string[] fillerTags = ["Personal", "Ideas", "Finance", "Home", "Health"];
        string[] fillerContacts =
            [Contacts.WholeFoods, Contacts.Starbucks, Contacts.Shell, Contacts.Netflix];

        for (var i = 0; i < 6; i++)
        {
            var title = Clamp(faker.Lorem.Sentence(faker.Random.Int(3, 6)).TrimEnd('.'), 200);
            var content = Clamp(faker.Lorem.Paragraph(), 4096);
            var tagCount = faker.Random.Int(1, 2);
            var tags = faker.PickRandom(fillerTags, tagCount).Distinct().ToArray();
            var contacts = faker.Random.Bool(0.4f)
                ? new[] { faker.PickRandom(fillerContacts) }
                : [];

            specs.Add(new EntrySpec(
                $"filler-{i}", title, content,
                anchor.AddDays(-faker.Random.Int(5, 540)),
                faker.Random.Bool(0.5f) ? faker.Address.City() : null,
                AuthorRole: faker.PickRandom("Admin", "Owner", "User"),
                EditorRole: null,
                TagNames: tags,
                ContactNames: contacts,
                PhotoCount: 0, AttachmentCount: 0, Archived: false));
        }

        var entries = new List<JournalEntry>();
        var entryTags = new List<JournalEntryTag>();
        var entryContacts = new List<JournalEntryContact>();
        var photos = new List<JournalEntryPhoto>();
        var libraryPhotos = new List<Photo>();
        var attachments = new List<JournalEntryAttachment>();
        var blobs = new List<FileBlob>();
        var metadata = new List<FileMetadata>();

        foreach (var spec in specs)
        {
            var entryId = IdFor(spec.Key);
            var authorId = UserId(spec.AuthorRole);

            entries.Add(new JournalEntry
            {
                JournalEntryId = entryId,
                ExternalUid = $"urn:uuid:{DeterministicGuid.From($"journal-entry-uid::{spec.Key}")}",
                Title = spec.Title,
                Content = spec.Content,
                EntryDate = spec.EntryDate,
                Location = spec.Location,
                CreatedByUserId = authorId,
                UpdatedByUserId = spec.EditorRole is null ? null : UserId(spec.EditorRole),
                CreatedAt = spec.EntryDate,
                UpdatedAt = spec.EditorRole is null ? spec.EntryDate : spec.EntryDate.AddDays(1),
                Archived = spec.Archived ? spec.EntryDate.AddDays(2) : null,
            });

            foreach (var tagName in spec.TagNames.Distinct())
            {
                entryTags.Add(new JournalEntryTag
                {
                    JournalEntryId = entryId,
                    JournalTagId = JournalTagGenerator.IdFor(tagName),
                });
            }

            foreach (var contactName in spec.ContactNames.Distinct())
            {
                entryContacts.Add(new JournalEntryContact
                {
                    JournalEntryId = entryId,
                    ContactId = Catalog.Contacts.IdFor(contactName),
                });
            }

            for (var i = 0; i < spec.PhotoCount; i++)
            {
                var (blob, meta) = BuildImageFile(spec.Key, i, authorId, spec.EntryDate);
                blobs.Add(blob);
                metadata.Add(meta);

                // The shared library Photo the journal link points at (one record per image file).
                var photoId = DeterministicGuid.From($"photo::journal::{spec.Key}#{i}");
                libraryPhotos.Add(new Photo
                {
                    PhotoId = photoId,
                    FileId = meta.Id,
                    Title = $"{spec.Title} — photo {i + 1}",
                    TakenAt = DateTime.SpecifyKind(spec.EntryDate, DateTimeKind.Unspecified),
                    CreatedByUserId = authorId,
                    CreatedAt = spec.EntryDate,
                    UpdatedAt = spec.EntryDate,
                });

                photos.Add(new JournalEntryPhoto
                {
                    JournalEntryPhotoId = DeterministicGuid.From($"journal-photo::{spec.Key}#{i}"),
                    JournalEntryId = entryId,
                    PhotoId = photoId,
                    Position = i,
                    CreatedAt = spec.EntryDate,
                });
            }

            for (var i = 0; i < spec.AttachmentCount; i++)
            {
                var (blob, meta) = BuildPdfFile(spec.Key, i, authorId, spec.EntryDate, spec.Title);
                blobs.Add(blob);
                metadata.Add(meta);
                attachments.Add(new JournalEntryAttachment
                {
                    JournalEntryAttachmentId = DeterministicGuid.From($"journal-attachment::{spec.Key}#{i}"),
                    JournalEntryId = entryId,
                    FileId = meta.Id,
                    CreatedAt = spec.EntryDate,
                });
            }
        }

        return new Result(entries, entryTags, entryContacts, photos, attachments, blobs, metadata, libraryPhotos);
    }

    private static string UserId(string role) => DemoUsers.All.First(user => user.Role == role).Id;

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static (FileBlob Blob, FileMetadata Metadata) BuildImageFile(
        string key, int index, string uploaderId, DateTime uploadedAt)
    {
        var blobId = DeterministicGuid.From($"journal-photo-blob::{key}#{index}");
        var metadataId = DeterministicGuid.From($"journal-photo-file::{key}#{index}");

        // A design-system scene gradient per photo (varied but deterministic by key+index) so the gallery
        // tiles look like the design, matching the standalone library photos.
        var seed = key.Aggregate(index, (acc, ch) => acc * 31 + ch);
        var content = DemoImages.GradientPng(DemoImages.PhotoSize, seed);

        var blob = new FileBlob { Id = blobId, Content = content };
        var metadata = new FileMetadata
        {
            Id = metadataId,
            UploadedByUserId = uploaderId,
            FileName = $"{key}-photo-{index + 1}.png",
            ContentType = "image/png",
            SizeBytes = content.LongLength,
            Sha256Hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            FileBlobId = blobId,
            Description = "Demo journal photo.",
            UploadedAtUtc = uploadedAt,
        };
        return (blob, metadata);
    }

    private static (FileBlob Blob, FileMetadata Metadata) BuildPdfFile(
        string key, int index, string uploaderId, DateTime uploadedAt, string title)
    {
        var blobId = DeterministicGuid.From($"journal-attachment-blob::{key}#{index}");
        var metadataId = DeterministicGuid.From($"journal-attachment-file::{key}#{index}");

        var content = MinimalPdf.Create(
            title,
            "Odyssey demo data — journal attachment.",
            $"Reference: {key} (document {index + 1}).");

        var blob = new FileBlob { Id = blobId, Content = content };
        var metadata = new FileMetadata
        {
            Id = metadataId,
            UploadedByUserId = uploaderId,
            FileName = $"{key}-attachment-{index + 1}.pdf",
            ContentType = "application/pdf",
            SizeBytes = content.LongLength,
            Sha256Hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            FileBlobId = blobId,
            Description = "Demo journal attachment.",
            UploadedAtUtc = uploadedAt,
        };
        return (blob, metadata);
    }
}
