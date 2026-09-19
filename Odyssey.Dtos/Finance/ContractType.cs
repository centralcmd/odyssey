namespace Odyssey.Dtos.Finance;

/// <summary>
/// What kind of agreement a contract records. <b>Ordinals are a wire and persistence contract and are
/// never renumbered</b>: the four members added after the original set append at 4–7, so
/// <see cref="Other"/> keeps ordinal 3 even though it reads last everywhere a human sees the list.
/// The reading order lives in the client's <c>OdsTypeRegistries.ContractTypes</c>, not here.
/// </summary>
public enum ContractType
{
    Employment = 0,
    Service = 1,
    Rental = 2,
    Other = 3,
    Insurance = 4,
    Subscription = 5,
    Purchase = 6,
    Membership = 7,
}
