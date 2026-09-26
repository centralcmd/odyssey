namespace Odyssey.Context;

/// <summary>
/// The persistence mirror of <c>Odyssey.Dtos.Finance.PropertyFileType</c> (issue #210 §4). Same members,
/// same ordinals — an ordinal is a wire and persistence contract and is never renumbered or reused.
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
