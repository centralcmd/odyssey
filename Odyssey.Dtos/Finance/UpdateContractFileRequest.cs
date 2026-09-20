using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Replaces an attached contract document's type and validity metadata (issue #146 §5.2). The body is
/// a <b>full replacement</b>: an omitted date or issuer is <c>null</c> and <b>clears</b> the stored
/// value. <see cref="FileType"/> is the exception and may not be omitted.
/// </summary>
public sealed record UpdateContractFileRequest
{
    /// <summary>
    /// The document's type. Carries the C# <c>required</c> modifier rather than <c>[Required]</c>,
    /// deliberately (issue #146 §8.1.1): this verb is a full replacement, so it has no "leave
    /// unchanged" value, and <c>[Required]</c> is a <b>no-op on a non-nullable value type</c> — after
    /// deserialization the property always holds a value, so validation would see
    /// <see cref="ContractFileType.Signed"/> rather than "missing". Because
    /// <c>ContractFileType.Signed == 0</c>, an omitted key would silently mark an arbitrary attachment
    /// as the signed copy of the contract. System.Text.Json enforces required members at
    /// deserialization, which <c>[ApiController]</c> surfaces as a <c>400</c>.
    ///
    /// <para>
    /// <see cref="EnumDataType"/> stays alongside it: the two check different things — presence
    /// versus range — and both are needed. The asymmetry with <c>UpdateAccountFileRequest</c>, which
    /// leaves its <c>FileType</c> unmarked, is deliberate and not a divergence to "fix": that enum's
    /// zero value is <c>Other</c>, so the same omission degrades harmlessly there.
    /// </para>
    /// </summary>
    [EnumDataType(typeof(ContractFileType))]
    public required ContractFileType FileType { get; set; }

    /// <summary>When the document takes effect (e.g. agreement start date). Optional; null clears.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>When the document expires (e.g. agreement end, warranty expiry). Optional; null clears.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>Date the document was issued/signed. Optional; null clears.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Issuing contact id (e.g. bank, insurer). Optional; null clears.</summary>
    public Guid? IssuedBy { get; set; }
}
