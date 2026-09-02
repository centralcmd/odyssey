using Odyssey.Dtos;
using Odyssey.Context;
using Odyssey.Dtos.Finance;

namespace Odyssey.TestData.Catalog;

/// <summary>
/// Deterministic contact roster (issue #325 shape): a base <see cref="Contact"/> plus a 1:1
/// <see cref="PersonDetails"/>/<see cref="OrganizationDetails"/> sub-record discriminated by
/// <see cref="ContactType"/>. Names double as stable keys for transaction-stream references
/// (<see cref="IdFor"/>). The legacy Merchant/Company/Institution/Other kinds collapse to
/// <see cref="ContactType.Organization"/>; the one landlord is a <see cref="ContactType.Person"/>.
/// </summary>
public static class Contacts
{
    public const string WholeFoods = "Whole Foods Market";
    public const string TraderJoes = "Trader Joe's";
    public const string Starbucks = "Starbucks";
    public const string CornerBistro = "The Corner Bistro";
    public const string Shell = "Shell";
    public const string Uber = "Uber";
    public const string Hm = "H&M";
    public const string Netflix = "Netflix";
    public const string Spotify = "Spotify";
    public const string Delta = "Delta Air Lines";
    public const string StateFarm = "State Farm";
    public const string Globex = "Globex Corporation";
    public const string CityPowerWater = "City Power & Water";
    public const string FirstNationalBank = "First National Bank";
    public const string Vanguard = "Vanguard";
    public const string BlueCross = "BlueCross Health";
    public const string Irs = "Internal Revenue Service";
    public const string Landlord = "Jane Smith (Landlord)";
    public const string CashWithdrawal = "Cash Withdrawal";

    // The household, and one former member. These exist so the insurance link collections have real
    // people to name — insured contacts and beneficiaries (issue #27) — including the ARCHIVED case,
    // which is the one a demo cannot fabricate at read time: the seeded policy keeps the link, and the
    // read path returns it with no name.
    public const string PolicyHolder = "Alex Rivera";
    public const string Spouse = "Sam Rivera";
    public const string FormerBeneficiary = "Chris Rivera";

    /// <summary>A second insurance provider, so a policy can be placed across co-insurers.</summary>
    public const string Allstate = "Allstate";

    private static readonly DateTime SeededAt = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // (key, notes, legalName, organizationNumber) — every entry an Organization; the people are below.
    private static readonly (string Name, string Notes, string? OrganizationNumber)[] Organizations =
    [
        (WholeFoods, "Grocery store", null),
        (TraderJoes, "Grocery store", null),
        (Starbucks, "Coffee shop", null),
        (CornerBistro, "Neighbourhood restaurant", null),
        (Shell, "Fuel station", null),
        (Uber, "Rideshare", null),
        (Hm, "Clothing retailer", null),
        (Netflix, "Streaming service", null),
        (Spotify, "Music streaming service", null),
        (Delta, "Airline", "58-1845724"),
        (StateFarm, "Insurance provider", "37-0533100"),
        (Allstate, "Insurance provider", "36-0724180"),
        (Globex, "Employer", "98-7654321"),
        (CityPowerWater, "Utility provider", "94-0742640"),
        (FirstNationalBank, "Retail bank", null),
        (Vanguard, "Investment broker", null),
        (BlueCross, "Healthcare network", null),
        (Irs, "Tax authority", null),
        (CashWithdrawal, "Uncategorized cash", null),
    ];

    public static Guid IdFor(string name) => DeterministicGuid.From($"contact::{name}");

    // Deterministic per issue #338 §6 — a real, persisted ExternalUid every seeded row needs since the
    // column is required; the urn:uuid form matches what an ordinary create/import would produce.
    private static string ExternalUidFor(string name) => $"urn:uuid:{DeterministicGuid.From($"contact-external-uid::{name}")}";

    private static Contact Person(
        string key, string firstName, string lastName, RelationshipType relationship, string notes,
        DateTime? archived = null) => new()
    {
        ContactId = IdFor(key),
        ExternalUid = ExternalUidFor(key),
        NormalizedName = Normalize($"{firstName} {lastName}"),
        Type = ContactType.Person,
        Notes = notes,
        Archived = archived,
        CreatedAt = SeededAt,
        UpdatedAt = SeededAt,
        PersonDetails = new PersonDetails
        {
            ContactId = IdFor(key),
            FirstName = firstName,
            LastName = lastName,
            RelationshipType = relationship,
        },
    };

    private static string Normalize(string value) =>
        string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    public static List<Contact> Build()
    {
        var contacts = Organizations
            .Select(definition => new Contact
            {
                ContactId = IdFor(definition.Name),
                ExternalUid = ExternalUidFor(definition.Name),
                NormalizedName = Normalize(definition.Name),
                Type = ContactType.Organization,
                OrganizationNumber = definition.OrganizationNumber,
                Notes = definition.Notes,
                Archived = null,
                CreatedAt = SeededAt,
                UpdatedAt = SeededAt,
                OrganizationDetails = new OrganizationDetails
                {
                    ContactId = IdFor(definition.Name),
                    LegalName = definition.Name,
                    OrganizationNumber = definition.OrganizationNumber,
                    Website = null,
                },
            })
            .ToList();

        // The Person contacts. The landlord predates the household, which exists so the insurance
        // link collections have real people to name.
        contacts.Add(Person(Landlord, "Jane", "Smith", RelationshipType.Landlord, "Property landlord"));
        contacts.Add(Person(PolicyHolder, "Alex", "Rivera", RelationshipType.Family, "Policyholder on the household policies"));
        contacts.Add(Person(Spouse, "Sam", "Rivera", RelationshipType.Family, "Named on the household policies"));
        // Archived on purpose: this is the demo's UNNAMED-member case. The Term Life policy keeps the
        // beneficiary link, and the read path returns it with its id and type but NO name — the state
        // an ordinary write can neither remove nor accidentally delete (issue #27 §9).
        contacts.Add(Person(FormerBeneficiary, "Chris", "Rivera", RelationshipType.Family,
            "Former beneficiary — archived", archived: SeededAt.AddYears(1)));

        return contacts;
    }
}
