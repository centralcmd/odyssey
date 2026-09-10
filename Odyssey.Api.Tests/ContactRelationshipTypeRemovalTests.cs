using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The removal of <c>PersonDetails.RelationshipType</c> (issue #52), asserted at the HTTP tier: the
/// read contract no longer carries the key, the write path still accepts a stale client that sends
/// it, the vCard export no longer emits <c>X-ODYSSEY-RELATIONSHIP</c>, and the vCard import tolerates
/// an incoming one.
/// </summary>
/// <remarks>
/// These are compatibility assertions, which is why they are worth having for a field nothing
/// rendered: the property is gone from the managed code, so nothing else would notice if the API
/// started rejecting the two stale inputs above instead of ignoring them.
/// </remarks>
public class ContactRelationshipTypeRemovalTests
{
    private const string ActorUserId = "contact-relationship-removal-actor";
    private const string BasePath = "/api/contacts";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate, PermissionClaims.ContactsUpdate,
    ];

    /// <summary>
    /// AC #5 — asserted on the RAW JSON. A deserialized <see cref="PersonDetailsDto"/> cannot detect a
    /// stray key, so round-tripping through the DTO would pass even if the server still emitted one.
    /// </summary>
    [Fact]
    public async Task Get_PersonContact_CarriesNoRelationshipTypeKey()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreatePersonAsync(client);

        var response = await client.GetAsync($"{BasePath}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var person = document.RootElement.GetProperty("personDetails");
        Assert.False(person.TryGetProperty("relationshipType", out _));

        // The neighbours the removal must not have taken with it.
        Assert.Equal("Ada", person.GetProperty("firstName").GetString());
        Assert.Equal("Lovelace", person.GetProperty("lastName").GetString());
        Assert.Equal("Countess", person.GetProperty("title").GetString());
    }

    /// <summary>
    /// AC #6 — a stale client sending <c>relationshipType</c> is ACCEPTED, value ignored, not rejected
    /// with a <c>400</c>.
    /// </summary>
    /// <remarks>
    /// This depends on the framework default staying <c>JsonUnmappedMemberHandling.Skip</c>: neither
    /// <see cref="PersonDetailsDto"/> nor <c>Program.cs</c> opts into <c>Disallow</c>. If the API is
    /// ever hardened to <c>Disallow</c> globally this flips to <c>400</c> — which should surface here
    /// as a deliberate contract change (issue #52 §9), not as a mystery failure.
    /// </remarks>
    [Fact]
    public async Task Put_WithStaleRelationshipTypeMember_IsAcceptedAndIgnored()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreatePersonAsync(client);

        var stale = """
            {
              "type": 1,
              "displayName": "Ada L.",
              "notes": "Analytical Engine",
              "archived": false,
              "personDetails": {
                "firstName": "Augusta",
                "lastName": "King",
                "relationshipType": 2,
                "title": "Mathematician",
                "company": "Analytical Engine"
              }
            }
            """;

        using var body = new StringContent(stale, Encoding.UTF8, "application/json");
        var response = await client.PutAsync($"{BasePath}/{id}", body);

        // The point of the assertion is that it is NOT a 400 — an unmapped member does not fail model
        // binding. The success code on this route is 204, not the 200 issue #52 §7 loosely says.
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var reread = await client.GetFromJsonAsync<ExistingContact>($"{BasePath}/{id}");
        Assert.Equal("Ada L.", reread!.DisplayName);
        Assert.Equal("Analytical Engine", reread.Notes);
        Assert.Equal("Augusta", reread.PersonDetails!.FirstName);
        Assert.Equal("King", reread.PersonDetails.LastName);
        Assert.Equal("Mathematician", reread.PersonDetails.Title);
        Assert.Equal("Analytical Engine", reread.PersonDetails.Company);
    }

    /// <summary>
    /// AC #9 — the served vCard no longer carries the extension property, and the properties around it
    /// are unaffected.
    /// </summary>
    /// <remarks>
    /// The contact is seeded by IMPORTING a vCard that carries the property, which is what makes this
    /// a test the removal is needed to pass: on the previous build the parser stored the value and the
    /// exporter emitted it straight back. Seeding through the API instead would assert nothing, since
    /// no write path can set the field any more.
    /// </remarks>
    [Fact]
    public async Task ExportOne_AfterImportingAVCardCarryingTheProperty_OmitsItOnTheWayOut()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var import = await PostVCardAsync(client, PersonVcard("relationship-round-trip-1"));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var id = await SingleContactIdAsync(factory);

        var response = await client.GetAsync($"{BasePath}/{id}/vcard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/vcard", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("X-ODYSSEY-RELATIONSHIP", body, StringComparison.Ordinal);
        Assert.Contains("FN:Ada Lovelace", body, StringComparison.Ordinal);
        Assert.Contains("N:Lovelace;Ada;;;", body, StringComparison.Ordinal);
        Assert.Contains("BDAY:18151210", body, StringComparison.Ordinal);
        Assert.Contains("GENDER:F", body, StringComparison.Ordinal);
        Assert.Contains("TITLE:Countess", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC #7 — an incoming <c>X-ODYSSEY-RELATIONSHIP</c> is ignored, never rejected, and does not turn
    /// into a whole-entry skip. RFC 6350 §6.10 requires a consumer to tolerate unknown extension
    /// properties, and the parser resolves properties by name out of a dictionary, so an unrecognised
    /// one is structurally unreachable by the skip collector.
    /// </summary>
    [Fact]
    public async Task Import_VCardCarryingTheRelationshipProperty_IsToleratedNotSkipped()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await PostVCardAsync(client, PersonVcard("relationship-tolerance-1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<VCardImportResult>();
        Assert.Equal(1, result!.CreatedCount);
        Assert.Empty(result.Skipped);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var person = context.PersonDetails.Single();
        Assert.Equal("Ada", person.FirstName);
        Assert.Equal("Lovelace", person.LastName);
        Assert.Equal(new DateOnly(1815, 12, 10), person.DateOfBirth);
        Assert.Equal(Sex.Female, person.Sex);
        Assert.Equal("Countess", person.Title);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task<Guid> CreatePersonAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(BasePath, new NewContact
        {
            Type = ContactType.Person,
            Archived = false,
            PersonDetails = new PersonDetailsDto
            {
                FirstName = "Ada",
                LastName = "Lovelace",
                DateOfBirth = new DateTime(1815, 12, 10),
                Sex = Sex.Female,
                Title = "Countess",
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // POST returns 201 with an EMPTY body and the id only in the Location header, so the id has
        // to come from there rather than from a deserialized response.
        var location = response.Headers.Location!.ToString();
        return Guid.Parse(location[(location.LastIndexOf('/') + 1)..]);
    }

    /// <summary>A person vCard carrying the retired extension property alongside the real ones.</summary>
    private static string PersonVcard(string uid) => Vcard(
        $"UID:{uid}",
        "FN:Ada Lovelace",
        "N:Lovelace;Ada;;;",
        "BDAY:18151210",
        "GENDER:F",
        "TITLE:Countess",
        "X-ODYSSEY-RELATIONSHIP:Family");

    private static async Task<Guid> SingleContactIdAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return context.Contacts.Single().ContactId;
    }

    private static string Vcard(params string[] lines) =>
        "BEGIN:VCARD\r\nVERSION:4.0\r\n" + string.Join("\r\n", lines) + "\r\nEND:VCARD\r\n";

    private static async Task<HttpResponseMessage> PostVCardAsync(HttpClient client, string vcf)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(vcf));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/vcard");
        content.Add(file, "file", "contacts.vcf");
        return await client.PostAsync($"{BasePath}/vcard", content);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
