using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record AttachAccountFileRequest(
    Guid FileId,
    [EnumDataType(typeof(AccountFileType))] AccountFileType FileType = AccountFileType.Other,
    DateTime? ValidFrom = null,
    DateTime? ValidTo = null,
    DateTime? IssuedAt = null,
    Guid? IssuedBy = null
);
