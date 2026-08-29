namespace Odyssey.Dtos.Finance;

public sealed record AttachTaxStatementFileRequest(
    Guid FileId,
    TaxStatementFileType FileType = TaxStatementFileType.Other
);
