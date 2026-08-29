using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record UpdateAccountFileRequest
{
    [EnumDataType(typeof(AccountFileType))]
    public AccountFileType FileType { get; set; }

    /// <summary>When the document takes effect (e.g. policy start date). Optional.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>When the document expires (e.g. policy end, warranty expiry). Optional.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>Date the document was issued/signed. Optional.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Issuing contact id (e.g. bank, insurer). Optional.</summary>
    public Guid? IssuedBy { get; set; }
}
