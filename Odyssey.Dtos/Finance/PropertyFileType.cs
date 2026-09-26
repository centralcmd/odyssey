namespace Odyssey.Dtos.Finance;

/// <summary>
/// What a document attached to a property is (issue #210 §4). Ordinals are a wire and persistence
/// contract — never renumbered, never reused; a new type is appended.
///
/// <para>
/// <see cref="Other"/> is ordinal 0 on purpose — the <see cref="AccountFileType"/> shape, not the
/// <see cref="ContractFileType"/> one — so an omitted <c>fileType</c> on attach degrades harmlessly to
/// <see cref="Other"/> instead of classifying an arbitrary document as the deed.
/// </para>
/// </summary>
public enum PropertyFileType
{
    Other = 0,
    Deed = 1,
    PurchaseAgreement = 2,
    Valuation = 3,
    Inspection = 4,
    Registration = 5,
    Insurance = 6,
    Warranty = 7,
    Receipt = 8,
    Maintenance = 9,
    Tax = 10,
    Drawing = 11,
}
