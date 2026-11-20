using Bogus;
using Odyssey.Context;
using Odyssey.Core.FileStorage.Files;
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
/// The Journal rows keep only file and photo <em>references</em>, so this generator also provisions what they
/// point at: real browser-renderable PNGs as library photos and tiny valid PDFs as journal-area files. They are
/// returned separately (<see cref="Result.Files"/> / <see cref="Result.LibraryPhotos"/>) because only the worker's
/// demo-file seeder, which has the file volumes, can write them — and the links with them, since they are real FKs. Authors are the seeded non-Guest demo users (Guest is excluded from the module).
/// </summary>
public static class JournalEntryGenerator
{
    /// <summary>
    /// Flat result lists (mirrors the seeder's <c>AddRangeAsync</c> pattern): the journal rows and the
    /// photos and attachment files they reference, kept apart because a different host writes them. Nav collections are left empty so nothing double-inserts.
    /// </summary>
    public sealed record Result(
        IReadOnlyList<JournalEntry> Entries,
        IReadOnlyList<JournalEntryTag> EntryTags,
        IReadOnlyList<JournalEntryContact> EntryContacts,
        IReadOnlyList<JournalEntryPhoto> Photos,
        IReadOnlyList<JournalEntryAttachment> Attachments,
        IReadOnlyList<DemoFile> Files,
        // Photo Library unification (issue #321 v4): a journal entry links a library Photo by PhotoId, so
        // each journal photo is a shared library Photo record.
        IReadOnlyList<DemoPhoto> LibraryPhotos);

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

    /// <summary>The entries linked to a household location (issue #380), by entry key → location key.</summary>
    public static readonly IReadOnlyDictionary<string, string> LinkedPlaces = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kitchen-reno"] = LocationGenerator.Home,
        ["espresso-machine"] = LocationGenerator.Home,
    };

    /// <summary>
    /// Builds the journal-entry graph. <paramref name="anchor"/> is the reference "today"; entry
    /// dates are placed relative to it. Deterministic: the same anchor always yields byte-identical
    /// output (photo/attachment bytes included).
    /// </summary>
    public static Result Generate(DateTime anchor)
    {
        // A LOCAL Randomizer, never the global `Randomizer.Seed`. That static is one
        // System.Random shared process-wide, so two concurrent DemoDataSet.Build() calls — a
        // seeder run racing a test's own Build(), which is routine under xUnit's parallel
        // collections — reseed each other mid-generation and interleave draws. The filler rows
        // below then differ between the two, which surfaced as a seeded-count assertion failing
        // in CI and passing locally. Seeded identically, so the values are unchanged.
        var faker = new Faker { Random = new Randomizer(RandomizerSeed) };

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
        var libraryPhotos = new List<DemoPhoto>();
        var attachments = new List<JournalEntryAttachment>();
        var files = new List<DemoFile>();

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
                // Issue #380: an entry at a known place links it and drops the text, as the conversion does.
                LocationId = LinkedPlaces.TryGetValue(spec.Key, out var place) ? LocationGenerator.LocationIdFor(place) : null,
                Location = LinkedPlaces.ContainsKey(spec.Key) ? null : spec.Location,
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
                // The shared library Photo the journal link points at. A design-system scene gradient per photo
                // (varied but deterministic by key+index), matching the standalone library photos.
                var photoId = DeterministicGuid.From($"photo::journal::{spec.Key}#{i}");
                var seed = spec.Key.Aggregate(i, (acc, ch) => acc * 31 + ch);
                libraryPhotos.Add(DemoFileFactory.Photo(new Photo
                {
                    PhotoId = photoId,
                    Title = $"{spec.Title} — photo {i + 1}",
                    TakenAt = DateTime.SpecifyKind(spec.EntryDate, DateTimeKind.Unspecified),
                    CreatedByUserId = authorId,
                    CreatedAt = spec.EntryDate,
                    UpdatedAt = spec.EntryDate,
                },
                    $"{spec.Key}-photo-{i + 1}.png", "image/png", DemoImages.GradientPng(DemoImages.PhotoSize, seed),
                    DeterministicGuid.From($"journal-photo-file::{spec.Key}#{i}")));

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
                var file = BuildPdfFile(spec.Key, i, authorId, spec.EntryDate, spec.Title);
                files.Add(file);
                attachments.Add(new JournalEntryAttachment
                {
                    JournalEntryAttachmentId = DeterministicGuid.From($"journal-attachment::{spec.Key}#{i}"),
                    JournalEntryId = entryId,
                    FileId = file.Metadata.Id,
                    CreatedAt = spec.EntryDate,
                });
            }
        }

        return new Result(entries, entryTags, entryContacts, photos, attachments, files, libraryPhotos);
    }

    private static string UserId(string role) => DemoUsers.All.First(user => user.Role == role).Id;

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static DemoFile BuildPdfFile(string key, int index, string uploaderId, DateTime uploadedAt, string title)
    {
        var content = MinimalPdf.Create(
            title,
            "Odyssey demo data — journal attachment.",
            $"Reference: {key} (document {index + 1}).");
        return DemoFileFactory.File(
            DeterministicGuid.From($"journal-attachment-file::{key}#{index}"), StoredFileArea.Journal, uploaderId,
            $"{key}-attachment-{index + 1}.pdf", "application/pdf", "Demo journal attachment.", uploadedAt, content);
    }
}
