using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record AttachTaxStatementFileRequest(
    Guid FileId,
    [EnumDataType(typeof(TaxStatementFileType))] TaxStatementFileType FileType = TaxStatementFileType.Other
);
