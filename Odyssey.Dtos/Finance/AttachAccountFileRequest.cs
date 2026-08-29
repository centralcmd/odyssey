namespace Odyssey.Dtos.Finance;

public sealed record AttachAccountFileRequest(
    Guid FileId,
    AccountFileType FileType = AccountFileType.Other,
    DateTime? ValidFrom = null,
    DateTime? ValidTo = null,
    DateTime? IssuedAt = null,
    Guid? IssuedBy = null
);
