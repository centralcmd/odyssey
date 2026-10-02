using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Attachments;

/// <summary>
/// Each finance surface's half of the shared "Attach documents" dialog: the name-based type guess and
/// the item → attach-request mapping. One place, so the mappings are testable without rendering the
/// cards that host them — a wrong fallback here posts a type the server stores as a different member.
/// </summary>
public static class AttachDocumentRequests
{
    // DS afmGuessKind(name, 'account'): a PDF is a statement, anything else Other.
    public static string AccountGuess(string fileName) =>
        fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? nameof(AccountFileType.Statement)
            : nameof(AccountFileType.Other);

    // DS TaxUploadModal guessKind: an assessment by name, else a PDF is the return, else supporting.
    public static string TaxGuess(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (lower.Contains("assess") || lower.Contains("notice") || lower.Contains("vedtak") || lower.Contains("skatteoppgj"))
            return nameof(TaxStatementFileType.TaxAssessment);
        return lower.EndsWith(".pdf", StringComparison.Ordinal)
            ? nameof(TaxStatementFileType.TaxReturn)
            : nameof(TaxStatementFileType.SupportingDocument);
    }

    // DS afmGuessKind(name, 'transaction'): an image is a receipt, a PDF an invoice.
    public static string TransactionGuess(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".heic" or ".webp" or ".gif" or ".tiff" => nameof(TransactionFileType.Receipt),
        ".pdf" => nameof(TransactionFileType.Invoice),
        _ => nameof(TransactionFileType.Other),
    };

    public static AttachAccountFileRequest Account(AttachDocumentItem item) =>
        new AttachAccountFileRequest
        {
            FileId = item.FileId,
            FileType = item.KindAs(AccountFileType.Other),
            ValidFrom = item.ValidFrom,
            ValidTo = item.ValidTo,
            IssuedAt = item.IssuedAt,
            IssuedBy = item.IssuedBy,
        };

    public static AttachTaxStatementFileRequest TaxStatement(AttachDocumentItem item) =>
        new AttachTaxStatementFileRequest { FileId = item.FileId, FileType = item.KindAs(TaxStatementFileType.Other) };

    public static TransactionFileType TransactionType(AttachDocumentItem item) =>
        item.KindAs(TransactionFileType.Other);

    // Signed is the contract vocabulary's zero member and its dialog default, so it is also the fallback.
    public static AttachContractFileRequest Contract(AttachDocumentItem item) => new()
    {
        FileMetadataId = item.FileId,
        FileType = item.KindAs(ContractFileType.Signed),
        ValidFrom = item.ValidFrom,
        ValidTo = item.ValidTo,
        IssuedAt = item.IssuedAt,
        IssuedBy = item.IssuedBy,
    };

    public static AttachPropertyFileRequest Property(AttachDocumentItem item) => new()
    {
        FileMetadataId = item.FileId,
        FileType = item.KindAs(PropertyFileType.Other),
        ValidFrom = item.ValidFrom,
        ValidTo = item.ValidTo,
        IssuedAt = item.IssuedAt,
        IssuedBy = item.IssuedBy,
    };
}
