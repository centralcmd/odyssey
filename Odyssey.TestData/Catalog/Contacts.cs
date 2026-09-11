using Odyssey.Dtos;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

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

    /// <summary>A deceased person — the issue #48 case that must NOT read as archived.</summary>
    public const string LatePartner = "Morgan Rivera";

    /// <summary>
    /// The near-cap contact (issue #48 §15). §12's <c>ListAllAsync</c> budget measures payload growth
    /// from inline aliases, and that measurement is meaningless unless something in the seed actually
    /// carries a full set.
    /// </summary>
    public const string AliasHeavy = "Northgate Mutual";

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
        (AliasHeavy, "Former insurer — dissolved; kept for policy history", "31-4455667"),
    ];

    public static Guid IdFor(string name) => DeterministicGuid.From($"contact::{name}");

    // Deterministic per issue #338 §6 — a real, persisted ExternalUid every seeded row needs since the
    // column is required; the urn:uuid form matches what an ordinary create/import would produce.
    private static string ExternalUidFor(string name) => $"urn:uuid:{DeterministicGuid.From($"contact-external-uid::{name}")}";

    private static Contact Person(
        string key, string firstName, string lastName, RelationshipType relationship, string notes,
        DateTime? archived = null, string? middleName = null,
        DateOnly? dateOfBirth = null, DateOnly? dateOfDeath = null) => new()
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
            MiddleName = middleName,
            DateOfBirth = dateOfBirth,
            DateOfDeath = dateOfDeath,
            RelationshipType = relationship,
        },
    };

    /// <summary>
    /// Aliases for a seeded contact (issue #48). Ids are deterministic like every other seeded key, so
    /// a reseed produces the same rows and a test can name one.
    /// </summary>
    private static List<ContactAlias> Aliases(string key, params (string Value, string? Label)[] aliases) =>
        [.. aliases.Select(alias => new ContactAlias
        {
            Id = DeterministicGuid.From($"contact-alias::{key}::{alias.Value}"),
            ContactId = IdFor(key),
            Value = alias.Value,
            Label = alias.Label,
        })];

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
        //
        // The landlord carries a MAIDEN NAME and a middle name — the two additions of issue #48 that
        // are searchable — so the alias and middle-name search arms have something real to match.
        contacts.Add(Person(Landlord, "Jane", "Smith", RelationshipType.Landlord, "Property landlord",
            middleName: "Elisabeth", dateOfBirth: new DateOnly(1968, 3, 14)));
        contacts.Add(Person(PolicyHolder, "Alex", "Rivera", RelationshipType.Family, "Policyholder on the household policies"));
        contacts.Add(Person(Spouse, "Sam", "Rivera", RelationshipType.Family, "Named on the household policies"));
        // Archived on purpose: this is the demo's UNNAMED-member case. The Term Life policy keeps the
        // beneficiary link, and the read path returns it with its id and type but NO name — the state
        // an ordinary write can neither remove nor accidentally delete (issue #27 §9).
        contacts.Add(Person(FormerBeneficiary, "Chris", "Rivera", RelationshipType.Family,
            "Former beneficiary — archived", archived: SeededAt.AddYears(1)));

        // The DECEASED case (issue #48). Recording a death date archives nothing and removes no
        // capability, so this contact keeps its policy links and stays selectable everywhere — which
        // is exactly what the demo has to show, since the archived case above looks superficially
        // similar and is not the same thing at all.
        contacts.Add(Person(LatePartner, "Morgan", "Rivera", RelationshipType.Family,
            "Late partner — the record stays live; finance rows still reference it",
            dateOfBirth: new DateOnly(1951, 9, 2), dateOfDeath: new DateOnly(2024, 3, 11)));

        AttachAliases(contacts);

        return contacts;
    }

    /// <summary>
    /// The demo's alias and organization-lifecycle set (issue #48 §15): a labelled alias, an
    /// unlabelled one, a dissolved organization, and one contact at the 32-alias cap.
    /// </summary>
    private static void AttachAliases(List<Contact> contacts)
    {
        var byId = contacts.ToDictionary(contact => contact.ContactId);

        void Attach(string key, params (string Value, string? Label)[] aliases)
        {
            if (byId.TryGetValue(IdFor(key), out var contact))
            {
                contact.Aliases = Aliases(key, aliases);
            }
        }

        // A maiden name — the labelled case, and the one §10.4 calls special-category-adjacent.
        Attach(Landlord, ("Jane Hawthorne", "maiden name"), ("Janey", "nickname"));
        // The UNLABELLED case: an abbreviation everyone uses, with nothing to say about it.
        Attach(FirstNationalBank, ("FNB", null));
        Attach(StateFarm, ("State Farm Mutual", "legal name"));
        Attach(LatePartner, ("Mo", "nickname"));

        // A DISSOLVED organization, and an established date on an operating one — the latter gets no
        // card chip, deliberately: an operating company needs no badge.
        if (byId.TryGetValue(IdFor(AliasHeavy), out var dissolved) && dissolved.OrganizationDetails is { } dissolvedOrg)
        {
            dissolvedOrg.EstablishedDate = new DateOnly(1974, 6, 1);
            dissolvedOrg.DissolvedDate = new DateOnly(2023, 6, 30);
            // At the cap. §12's ListAllAsync budget is measured against this row, so the number here
            // is the cap itself, read from the shared constant rather than written as a literal.
            dissolved.Aliases = Aliases(
                AliasHeavy,
                [.. Enumerable.Range(1, ContactAliasRules.MaxPerContact)
                    .Select(index => ($"Northgate Mutual {index:00}", index % 3 == 0 ? "pre-merger name" : (string?)null))]);
        }

        if (byId.TryGetValue(IdFor(Globex), out var employer) && employer.OrganizationDetails is { } employerOrg)
        {
            employerOrg.EstablishedDate = new DateOnly(1998, 11, 4);
        }
    }
}
