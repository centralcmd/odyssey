using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The contact surfaces issue #48 changes outside the alias section itself: the picker's lifecycle
/// suffix, the card's deceased/dissolved chip, the announcer's nonce, the widened copy, and the
/// components that must stay UNCHANGED (AC 37, 38, 39, 41, 44).
/// </summary>
public class ContactAliasSurfaceTests
{
    private static ExistingContact Person(DateTime? dateOfDeath = null, string? middleName = null) => new()
    {
        ContactId = Guid.NewGuid(),
        ResolvedDisplayName = "Kari Nordmann",
        NormalizedName = "KARI NORDMANN",
        ExternalUid = "urn:uuid:person",
        Type = ContactType.Person,
        PersonDetails = new PersonDetailsDto
        {
            FirstName = "Kari",
            LastName = "Nordmann",
            MiddleName = middleName,
            DateOfDeath = dateOfDeath,
        },
    };

    private static ExistingContact Organization(DateTime? dissolved = null, DateTime? established = null) => new()
    {
        ContactId = Guid.NewGuid(),
        ResolvedDisplayName = "Pacific Home Insurance Co.",
        NormalizedName = "PACIFIC HOME INSURANCE CO.",
        ExternalUid = "urn:uuid:org",
        Type = ContactType.Organization,
        OrganizationDetails = new OrganizationDetailsDto
        {
            LegalName = "Pacific Home Insurance Co.",
            EstablishedDate = established,
            DissolvedDate = dissolved,
        },
    };

    // ── The picker suffix (AC 38) ─────────────────────────────────────────────

    // It goes into the option's LABEL, not a sub-line. That puts it in the accessible name by
    // construction — and OdsCombobox renders no OdsOption.Sub at all (only OdsTagMultiSelect does),
    // so a sub-line would have been silently invisible.
    [Fact]
    public void A_deceased_contact_carries_its_marker_in_the_option_label()
    {
        var option = OdsContactOptions.From(Person(new DateTime(2024, 3, 11)));

        Assert.Equal("Kari Nordmann · Deceased", option.Label);
        Assert.Null(option.Sub);
    }

    [Fact]
    public void A_dissolved_organization_carries_its_marker_in_the_option_label()
    {
        var option = OdsContactOptions.From(Organization(dissolved: new DateTime(2023, 6, 30)));

        Assert.Equal("Pacific Home Insurance Co. · Dissolved", option.Label);
    }

    // An operating company needs no badge, so an establishment date alone adds nothing.
    [Fact]
    public void An_established_only_organization_gets_no_suffix()
    {
        var option = OdsContactOptions.From(Organization(established: new DateTime(1974, 6, 1)));

        Assert.Equal("Pacific Home Insurance Co.", option.Label);
    }

    [Fact]
    public void A_living_contact_gets_no_suffix()
    {
        Assert.Equal("Kari Nordmann", OdsContactOptions.From(Person()).Label);
    }

    // The contact stays SELECTABLE: recording either date removes no capability, so a deceased
    // person is still offered by the active-contacts projection.
    [Fact]
    public void A_deceased_contact_is_still_offered_by_the_picker()
    {
        var deceased = Person(new DateTime(2024, 3, 11));

        var options = OdsContactOptions.Active([deceased]);

        var option = Assert.Single(options);
        Assert.Equal(deceased.ContactId.ToString(), option.Value);
        Assert.Contains("Deceased", option.Label, StringComparison.Ordinal);
    }

    // The narrow finance embed carries no Type, deliberately, so its option falls back rather than
    // the embed being widened to supply a glyph.
    [Fact]
    public void A_contact_embed_still_yields_a_usable_option()
    {
        var embed = new ContactEmbed { ContactId = Guid.NewGuid(), ResolvedDisplayName = "Kari Nordmann" };

        var option = OdsContactOptions.From(embed);

        Assert.Equal(embed.ContactId.ToString(), option.Value);
        Assert.Equal("Kari Nordmann", option.Label);
    }

    // ── The card, the fields and the copy ─────────────────────────────────────

    // AC 37: the divider is emitted by ContactsCard's Sections slot, and ContactDetailPanel still
    // renders bare — one heading per section. v2 named two different places for this.
    [Fact]
    public void The_aliases_divider_is_emitted_by_the_card_not_by_either_panel()
    {
        var card = ReadClient("Pages/Finance/ContactsCard.razor");
        Assert.Contains("<OdsSectionDivider Label=\"Aliases\"", card, StringComparison.Ordinal);
        Assert.Contains("<ContactAliasSection", card, StringComparison.Ordinal);

        // Both section components render BARE — the card owns the headings, so neither may emit one.
        // The opening tag, not the bare name: both files mention the component in prose explaining
        // exactly this rule.
        foreach (var panel in new[] { "Pages/Finance/ContactAliasSection.razor", "Pages/Finance/ContactDetailPanel.razor" })
        {
            Assert.DoesNotContain("<OdsSectionDivider", ReadClient(panel), StringComparison.Ordinal);
        }
    }

    // AC 41's retraction, pinned. v2 specified a new Id parameter on OdsField; the capability already
    // ships through the CaptureUnmatchedValues splat, and a typed one would give a single component
    // two ways to set one attribute.
    [Fact]
    public void OdsField_gains_no_Id_parameter_and_the_alias_dialog_uses_the_splat()
    {
        var field = ReadClient("Components/OdsField.razor");
        Assert.DoesNotContain("public string? Id { get; set; }", field, StringComparison.Ordinal);
        Assert.Contains("CaptureUnmatchedValues = true", field, StringComparison.Ordinal);

        var dialog = ReadClient("Pages/Finance/ContactAliasDialog.razor");
        Assert.Contains("id=\"@ValueFieldId\"", dialog, StringComparison.Ordinal);
        Assert.Contains("focusById", dialog, StringComparison.Ordinal);
    }

    // AC 44: the announcer region is aria-atomic and will NOT re-announce an identical string, so
    // adding two aliases in succession would otherwise produce ONE announcement. The nonce is what
    // makes the second message distinct.
    [Fact]
    public void Announcements_carry_a_nonce_so_an_identical_message_announces_twice()
    {
        var card = ReadClient("Pages/Finance/ContactsCard.razor.cs");
        Assert.Contains("_announceNonce", card, StringComparison.Ordinal);
        Assert.Contains("\\u200B", card, StringComparison.Ordinal);

        // And there is exactly ONE live region on the page — a component-level Assertive parameter is
        // fixed at render rather than per message, so a second region would buy nothing.
        var markup = ReadClient("Pages/Finance/ContactsCard.razor");
        Assert.Equal(1, CountOccurrences(markup, "<OdsLiveAnnouncer"));
        Assert.DoesNotContain("OdsLiveAnnouncer", ReadClient("Pages/Finance/ContactAliasSection.razor"), StringComparison.Ordinal);
    }

    // Two copy changes the widened behaviour requires. The label is deliberately NOT searched, so the
    // placeholder must not imply otherwise.
    [Fact]
    public void The_search_placeholder_and_the_updated_caption_name_aliases()
    {
        Assert.Contains(
            "Search name, alias or notes…",
            ReadClient("Pages/Finance/ContactsCard.razor"),
            StringComparison.Ordinal);

        Assert.Contains(
            "bumped by any address, email, phone or alias change",
            ReadClient("Pages/Finance/ContactInfoTiles.razor"),
            StringComparison.Ordinal);
    }

    // AC 39's companion: the tile grid goes Dense, because a Person card already rendered up to nine
    // tiles against OdsInfoTileGrid's ceiling of eight and the two new ones take it to eleven.
    [Fact]
    public void The_contact_tile_grid_is_dense()
    {
        Assert.Contains(
            "<OdsInfoTileGrid Dense=\"true\">",
            ReadClient("Pages/Finance/ContactInfoTiles.razor"),
            StringComparison.Ordinal);
    }

    // AC 39 itself: a middle name renders a tile; its absence renders none, per the grid's own
    // "no value renders NO tile" rule.
    [Fact]
    public void The_middle_name_and_lifecycle_tiles_are_conditional()
    {
        var tiles = ReadClient("Pages/Finance/ContactInfoTiles.razor");

        Assert.Contains("Label=\"Middle name\"", tiles, StringComparison.Ordinal);
        Assert.Contains("!string.IsNullOrWhiteSpace(person.MiddleName)", tiles, StringComparison.Ordinal);
        Assert.Contains("person.DateOfDeath is { } dod", tiles, StringComparison.Ordinal);
        Assert.Contains("org.EstablishedDate is { } established", tiles, StringComparison.Ordinal);
        Assert.Contains("org.DissolvedDate is { } dissolved", tiles, StringComparison.Ordinal);
    }

    // Both date controls are OdsDateFields, not bare OdsDatePickers: the birth/death pair is checked
    // from both sides, so each needs its own Error / aria-invalid channel — which OdsDatePicker lacks.
    // That is why DateOfBirth migrated too.
    [Fact]
    public void Every_contact_date_field_has_its_own_error_channel()
    {
        var fields = ReadClient("Pages/Finance/ContactFields.razor");

        Assert.DoesNotContain("<OdsDatePicker", fields, StringComparison.Ordinal);
        foreach (var field in new[] { "dateOfBirth", "dateOfDeath", "establishedDate", "dissolvedDate" })
        {
            Assert.Contains($"FieldErrors.GetValueOrDefault(\"{field}\")", fields, StringComparison.Ordinal);
        }
    }

    // Non-Goal 2: aliases are added from the expanded card once the contact exists, exactly as
    // addresses, emails and phones are — so the create dialog never edits them and NewContact carries
    // no alias member at all.
    [Fact]
    public void The_contact_dialog_does_not_edit_aliases()
    {
        Assert.DoesNotContain("Alias", ReadClient("Pages/Finance/ContactDialog.razor"), StringComparison.Ordinal);
        Assert.DoesNotContain("Alias", ReadClient("Pages/Finance/ContactFields.razor"), StringComparison.Ordinal);
        Assert.Null(typeof(NewContact).GetProperty("Aliases"));
    }

    // The cap is shared from Odyssey.Dtos, which both halves of the stack reference — so there is one
    // number and no client-side literal to drift from the server's.
    [Fact]
    public void The_client_holds_no_literal_copy_of_the_alias_cap()
    {
        Assert.Equal(32, ContactAliasRules.MaxPerContact);

        foreach (var file in new[]
        {
            "Pages/Finance/ContactAliasSection.razor",
            "Pages/Finance/ContactAliasSection.razor.cs",
            "Pages/Finance/ContactAliasDialog.razor",
            "Pages/Finance/ContactAliasText.cs",
        })
        {
            foreach (var line in CodeLines(ReadClient(file)))
            {
                Assert.DoesNotContain("32", line, StringComparison.Ordinal);
                Assert.DoesNotContain("128", line, StringComparison.Ordinal);
                Assert.DoesNotContain("64", line, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// The lines of a source file that are actual CODE — comments dropped. A lint asserting the
    /// absence of a literal has to ignore prose, or the very comment explaining why the literal is
    /// forbidden fails it.
    /// </summary>
    private static IEnumerable<string> CodeLines(string source) =>
        source.Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                && !line.StartsWith("///", StringComparison.Ordinal)
                && !line.StartsWith("@*", StringComparison.Ordinal)
                && !line.StartsWith('*')
                && line.Length > 0);

    /// <summary>Reads a checked-in client source file by its project-relative path.</summary>
    private static string ReadClient(string relativePath) =>
        File.ReadAllText(Path.Combine(ClientSource.Root, relativePath));

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
