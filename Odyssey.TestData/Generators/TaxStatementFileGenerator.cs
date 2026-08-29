using System.Security.Cryptography;
using Odyssey.Context;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic document attachments for the seeded tax statements (issue #173): a real
/// <see cref="FileBlob"/> (a tiny valid PDF), its <see cref="FileMetadata"/> and the
/// <see cref="TaxStatementFile"/> link. Only the closed/filed years carry documents — a draft
/// statement has none. Attached by the demo Owner user.
///
/// This is the one part of the demo set that provisions stored file content, so the tax-statement
/// files section, downloads and previews all have working data.
/// </summary>
public static class TaxStatementFileGenerator
{
    private static readonly string OwnerUserId = DemoUsers.All.First(u => u.Role == "Owner").Id;

    private sealed record FileSpec(int FiscalYear, TaxStatementFileType Type, string FileName, string Title, DateTime AttachedAtUtc);

    public static (List<FileBlob> Blobs, List<FileMetadata> Metadata, List<TaxStatementFile> Links) Build()
    {
        var specs = new List<FileSpec>
        {
            new(2023, TaxStatementFileType.TaxReturn, "tax-return-2023.pdf", "Tax Return 2023", D(2024, 3, 20)),
            new(2023, TaxStatementFileType.TaxAssessment, "tax-assessment-2023.pdf", "Tax Assessment 2023", D(2024, 5, 1)),

            new(2024, TaxStatementFileType.TaxReturn, "tax-return-2024.pdf", "Tax Return 2024", D(2025, 3, 18)),
            new(2024, TaxStatementFileType.TaxAssessment, "tax-assessment-2024.pdf", "Tax Assessment 2024", D(2025, 5, 20)),

            new(2025, TaxStatementFileType.TaxReturn, "tax-return-2025.pdf", "Tax Return 2025", D(2026, 3, 15)),
            new(2025, TaxStatementFileType.SupportingDocument, "brokerage-statement-2025.pdf", "Brokerage Statement 2025", D(2026, 3, 15)),
        };

        var blobs = new List<FileBlob>();
        var metadata = new List<FileMetadata>();
        var links = new List<TaxStatementFile>();

        foreach (var spec in specs)
        {
            var content = MinimalPdf.Create(
                spec.Title,
                $"Fiscal year: {spec.FiscalYear}",
                $"Document type: {spec.Type}",
                "Odyssey demo data — sample document.");

            var blobId = DeterministicGuid.From($"file-blob::tax::{spec.FiscalYear}::{spec.FileName}");
            var metadataId = DeterministicGuid.From($"file-metadata::tax::{spec.FiscalYear}::{spec.FileName}");

            blobs.Add(new FileBlob
            {
                Id = blobId,
                Content = content,
            });

            metadata.Add(new FileMetadata
            {
                Id = metadataId,
                UploadedByUserId = OwnerUserId,
                FileName = spec.FileName,
                ContentType = "application/pdf",
                SizeBytes = content.LongLength,
                Sha256Hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                FileBlobId = blobId,
                Description = $"{spec.Title} (demo).",
                UploadedAtUtc = spec.AttachedAtUtc,
            });

            links.Add(new TaxStatementFile
            {
                Id = DeterministicGuid.From($"tax-statement-file::{spec.FiscalYear}::{spec.FileName}"),
                TaxStatementId = TaxStatementGenerator.IdFor(spec.FiscalYear),
                FileMetadataId = metadataId,
                AttachedByUserId = OwnerUserId,
                AttachedAtUtc = spec.AttachedAtUtc,
                FileType = spec.Type,
            });
        }

        return (blobs, metadata, links);
    }

    private static DateTime D(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
