using System.Security.Cryptography;
using Bogus;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic to-do items for the Journal module's shared task list (issue #311). Anchored to
/// <see cref="DemoDataDefaults.AnchorDate"/> so deadlines and completion timestamps are stable.
///
/// The set spans the kanban lifecycle — <see cref="JournalTaskStatus.Backlog"/>, <see cref="JournalTaskStatus.Doing"/>,
/// <see cref="JournalTaskStatus.Done"/> (with <c>CompletedAt</c> stamped) and one <see cref="JournalTaskStatus.Archived"/>
/// item — with gap-free <c>Position</c> values within each column (0-based, in spec order). Items are
/// tagged via <see cref="JournalTaskTagGenerator"/> ids and some carry a file attachment; the backing
/// <see cref="FileBlob"/> + <see cref="FileMetadata"/> are returned separately so the seeder can write
/// them with the rest of the Files store, which it must do first since the reference is a real FK. Authors are the seeded
/// non-Guest demo users (Guest is excluded from the module).
/// </summary>
public static class JournalTaskGenerator
{
    /// <summary>
    /// Flat result lists (mirrors the seeder's <c>AddRangeAsync</c> pattern): the task rows and the
    /// attachment blobs + metadata they reference, kept apart so the seeder can write the Files store
    /// first. Nav collections are left empty so nothing double-inserts.
    /// </summary>
    public sealed record Result(
        IReadOnlyList<JournalTask> Items,
        IReadOnlyList<JournalTaskTagLink> ItemTags,
        IReadOnlyList<JournalTaskAttachment> Attachments,
        IReadOnlyList<FileBlob> FileBlobs,
        IReadOnlyList<FileMetadata> FileMetadata);

    private sealed record TaskSpec(
        string Key,
        string Title,
        string? Content,
        int? DeadlineOffsetDays,
        JournalTaskStatus Status,
        int? CompletedOffsetDays,
        string AuthorRole,
        string? EditorRole,
        string[] TagNames,
        int AttachmentCount);

    public static Guid IdFor(string key) => DeterministicGuid.From($"journal-task::{key}");

    /// <summary>
    /// Builds the to-do graph. <paramref name="anchor"/> is the reference "today"; deadlines and
    /// completion timestamps are placed relative to it. Deterministic for a given anchor.
    /// </summary>
    public static Result Generate(DateTime anchor)
    {
        // Seed Bogus once so the filler backlog items below are reproducible.
        Randomizer.Seed = new Random(RandomizerSeed);

        var specs = new List<TaskSpec>
        {
            // ── Backlog ──
            new(
                "insurance-renewal", "Review home insurance renewal",
                "Compare this year's quote against last year's before it auto-renews.",
                DeadlineOffsetDays: 21, JournalTaskStatus.Backlog, CompletedOffsetDays: null,
                AuthorRole: "Owner", EditorRole: null, TagNames: ["Finance"], AttachmentCount: 0),
            new(
                "auto-savings", "Set up automatic savings transfer",
                "Move the monthly savings transfer to fire the day after payday.",
                DeadlineOffsetDays: null, JournalTaskStatus.Backlog, CompletedOffsetDays: null,
                AuthorRole: "User", EditorRole: null, TagNames: ["Finance", "Home"], AttachmentCount: 0),
            new(
                "index-funds", "Research low-cost index funds",
                "Shortlist two or three broad-market funds and note the fees.",
                DeadlineOffsetDays: null, JournalTaskStatus.Backlog, CompletedOffsetDays: null,
                AuthorRole: "Admin", EditorRole: null, TagNames: ["Finance", "Waiting"], AttachmentCount: 0),

            // ── Doing ──
            new(
                "file-taxes", "File quarterly taxes",
                "Reconcile the statements and submit before the deadline. Draft attached.",
                DeadlineOffsetDays: 10, JournalTaskStatus.Doing, CompletedOffsetDays: null,
                AuthorRole: "Owner", EditorRole: "Admin", TagNames: ["Finance", "Urgent"], AttachmentCount: 1),
            new(
                "declutter-garage", "Declutter the garage",
                "Sort the boxes from the move; bin, donate or keep.",
                DeadlineOffsetDays: null, JournalTaskStatus.Doing, CompletedOffsetDays: null,
                AuthorRole: "User", EditorRole: null, TagNames: ["Home"], AttachmentCount: 0),

            // ── Done ──
            new(
                "renew-passport", "Renew passport",
                "Application submitted and new passport received.",
                DeadlineOffsetDays: null, JournalTaskStatus.Done, CompletedOffsetDays: -14,
                AuthorRole: "User", EditorRole: null, TagNames: ["Errands"], AttachmentCount: 0),
            new(
                "electricity-bill", "Pay the electricity bill",
                "Settled the outstanding balance.",
                DeadlineOffsetDays: null, JournalTaskStatus.Done, CompletedOffsetDays: -3,
                AuthorRole: "Owner", EditorRole: null, TagNames: ["Home", "Finance"], AttachmentCount: 0),

            // ── Archived (hidden from the default board) ──
            new(
                "old-shopping-list", "Old shopping list",
                "Superseded weekly shopping list, kept for reference.",
                DeadlineOffsetDays: null, JournalTaskStatus.Archived, CompletedOffsetDays: null,
                AuthorRole: "User", EditorRole: null, TagNames: ["Someday"], AttachmentCount: 0),
        };

        // A few Bogus-generated filler backlog items for realistic board volume/pagination.
        var faker = new Faker();
        string[] fillerTags = ["Home", "Errands", "Urgent", "Finance"];
        for (var i = 0; i < 4; i++)
        {
            specs.Add(new TaskSpec(
                $"filler-{i}",
                Clamp(faker.Hacker.Verb() + " " + faker.Commerce.ProductName(), 200),
                faker.Random.Bool(0.6f) ? Clamp(faker.Lorem.Sentence(), 4096) : null,
                DeadlineOffsetDays: null,
                JournalTaskStatus.Backlog,
                CompletedOffsetDays: null,
                AuthorRole: faker.PickRandom("Admin", "Owner", "User"),
                EditorRole: null,
                TagNames: [faker.PickRandom(fillerTags)],
                AttachmentCount: 0));
        }

        var items = new List<JournalTask>();
        var itemTags = new List<JournalTaskTagLink>();
        var attachments = new List<JournalTaskAttachment>();
        var blobs = new List<FileBlob>();
        var metadata = new List<FileMetadata>();

        // Gap-free 0-based position per status column, honouring spec order.
        var nextPosition = new Dictionary<JournalTaskStatus, int>();

        foreach (var spec in specs)
        {
            var itemId = IdFor(spec.Key);
            var authorId = UserId(spec.AuthorRole);
            var createdAt = anchor.AddDays(-45);

            var position = nextPosition.GetValueOrDefault(spec.Status);
            nextPosition[spec.Status] = position + 1;

            items.Add(new JournalTask
            {
                JournalTaskId = itemId,
                // Stable external identity (issue #337 §6), deterministic but distinct from the PK so demo
                // data never encodes the internal id into an exported VTODO UID.
                ExternalUid = $"urn:uuid:{DeterministicGuid.From($"journal-task-uid::{spec.Key}")}",
                Title = spec.Title,
                Content = spec.Content,
                Deadline = spec.DeadlineOffsetDays is null
                    ? null
                    : DateOnly.FromDateTime(anchor).AddDays(spec.DeadlineOffsetDays.Value),
                Position = position,
                CreatedByUserId = authorId,
                UpdatedByUserId = spec.EditorRole is null ? null : UserId(spec.EditorRole),
                CreatedAt = createdAt,
                UpdatedAt = spec.EditorRole is null ? createdAt : createdAt.AddDays(1),
                // Status is derived from these timestamps (issue #311): a started task carries StartedAt,
                // a finished one CompletedAt, an archived one Archived; a Backlog task carries none.
                StartedAt = spec.Status is JournalTaskStatus.Doing or JournalTaskStatus.Done ? createdAt.AddDays(5) : null,
                CompletedAt = spec.CompletedOffsetDays is null
                    ? null
                    : anchor.AddDays(spec.CompletedOffsetDays.Value),
                Archived = spec.Status is JournalTaskStatus.Archived ? anchor.AddDays(-20) : null,
            });

            foreach (var tagName in spec.TagNames.Distinct())
            {
                itemTags.Add(new JournalTaskTagLink
                {
                    JournalTaskId = itemId,
                    JournalTaskTagId = JournalTaskTagGenerator.IdFor(tagName),
                });
            }

            for (var i = 0; i < spec.AttachmentCount; i++)
            {
                var (blob, meta) = BuildPdfFile(spec.Key, i, authorId, createdAt, spec.Title);
                blobs.Add(blob);
                metadata.Add(meta);
                attachments.Add(new JournalTaskAttachment
                {
                    JournalTaskAttachmentId = DeterministicGuid.From($"journal-task-attachment::{spec.Key}#{i}"),
                    JournalTaskId = itemId,
                    FileId = meta.Id,
                    CreatedAt = createdAt,
                });
            }
        }

        return new Result(items, itemTags, attachments, blobs, metadata);
    }

    private static string UserId(string role) => DemoUsers.All.First(user => user.Role == role).Id;

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static (FileBlob Blob, FileMetadata Metadata) BuildPdfFile(
        string key, int index, string uploaderId, DateTime uploadedAt, string title)
    {
        var blobId = DeterministicGuid.From($"journal-task-attachment-blob::{key}#{index}");
        var metadataId = DeterministicGuid.From($"journal-task-attachment-file::{key}#{index}");

        var content = MinimalPdf.Create(
            title,
            "Odyssey demo data — task attachment.",
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
            Description = "Demo task attachment.",
            UploadedAtUtc = uploadedAt,
        };
        return (blob, metadata);
    }
}
