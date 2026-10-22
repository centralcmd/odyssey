using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The conversion audit trail (issue #335 §7.5, AC 24): every successful type change — the conversion
/// endpoint, a type-changing PUT and a vCard re-type — writes one <c>type.converted</c> line naming the
/// actor; a refusal writes one line with counts; and no line carries a name or a field value.
/// </summary>
public class ContactConversionAuditTests
{
    private const string ActorUserId = "conversion-audit-actor";
    private const string First = "Grace";
    private const string Last = "Hopper";
    private const string PropertyName = "Maple Street House";

    private static readonly string[] Claims =
    [
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate, PermissionClaims.ContactsUpdate,
        PermissionClaims.PhotosRead, PermissionClaims.PropertiesRead,
    ];

    [Fact]
    public async Task Convert_WritesOneActorBearingLine_AndEchoesNoName()
    {
        await using var factory = new LoggingApiFactory();
        var (id, updatedAt) = await SeedPersonAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{id}/conversion",
            new ContactConversionRequest { TargetType = ContactType.Organization, ExpectedUpdatedAt = updatedAt });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var line = Assert.Single(AuditLines(factory, "type.converted"));
        Assert.Contains(id.ToString(), line);
        Assert.Contains(ActorUserId, line);
        AssertNothingEchoed(factory);
    }

    [Fact]
    public async Task TypeChangingPut_WritesTheSameLine()
    {
        await using var factory = new LoggingApiFactory();
        var (id, _) = await SeedPersonAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/contacts/{id}", new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new OrganizationDetailsDto { LegalName = $"{First} {Last}" },
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var line = Assert.Single(AuditLines(factory, "type.converted"));
        Assert.Contains(ActorUserId, line);
        AssertNothingEchoed(factory);
    }

    [Fact]
    public async Task BlockedConversion_WritesCountsOnly()
    {
        await using var factory = new LoggingApiFactory();
        var (id, updatedAt) = await SeedOrgAsync(factory);
        await SeedAssociatedPropertyAsync(factory, id);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{id}/conversion",
            new ContactConversionRequest { TargetType = ContactType.Person, ExpectedUpdatedAt = updatedAt });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The 409's detail names the blocker class, never the property — that is the claim-gated payload's.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PropertyName, body[..body.IndexOf("\"homeownerAssociations\"", StringComparison.Ordinal)]);

        var line = Assert.Single(factory.Logs.Entries, e => e.Category == typeof(ContactAuditLog).FullName && e.Message.Contains("type change blocked"));
        Assert.Contains(ActorUserId, line.Message);
        Assert.Empty(AuditLines(factory, "type.converted"));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Message.Contains(PropertyName, StringComparison.OrdinalIgnoreCase));
        AssertNothingEchoed(factory);
    }

    [Fact]
    public async Task VCardReType_WritesTheLineFromTheController_AndKeepsIdsOutOfTheResponse()
    {
        await using var factory = new LoggingApiFactory();
        var (id, _) = await SeedPersonAsync(factory, externalUid: "urn:uuid:audit-retype");
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(
            "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:urn:uuid:audit-retype\r\nKIND:org\r\nORG:Hopper Systems\r\nFN:Hopper Systems\r\nEND:VCARD\r\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/vcard");
        content.Add(file, "file", "contacts.vcf");
        var response = await client.PostAsync("/api/contacts/vcard", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(id.ToString(), await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var line = Assert.Single(AuditLines(factory, "type.converted"));
        Assert.Contains(id.ToString(), line);
        Assert.Contains(ActorUserId, line);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static IEnumerable<string> AuditLines(LoggingApiFactory factory, string action) =>
        factory.Logs.Entries
            .Where(e => e.Category == typeof(ContactAuditLog).FullName && e.Message.Contains(action, StringComparison.Ordinal))
            .Select(e => e.Message);

    private static void AssertNothingEchoed(LoggingApiFactory factory)
    {
        foreach (var entry in factory.Logs.Entries)
        {
            Assert.DoesNotContain(First, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Last, entry.Message, StringComparison.Ordinal);
        }
    }

    private static Task<(Guid Id, DateTime UpdatedAt)> SeedPersonAsync(WebApplicationFactory<Program> factory, string? externalUid = null) =>
        SeedAsync(factory, new Contact
        {
            ExternalUid = externalUid ?? $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = $"{First} {Last}".ToUpperInvariant(),
            Type = ContactType.Person,
            PersonDetails = new PersonDetails { FirstName = First, LastName = Last },
        });

    private static Task<(Guid Id, DateTime UpdatedAt)> SeedOrgAsync(WebApplicationFactory<Program> factory) =>
        SeedAsync(factory, new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "MAPLE HEIGHTS HOA",
            Type = ContactType.Organization,
            OrganizationDetails = new OrganizationDetails { LegalName = "Maple Heights HOA" },
        });

    private static async Task<(Guid Id, DateTime UpdatedAt)> SeedAsync(WebApplicationFactory<Program> factory, Contact contact)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        contact.ContactId = Guid.NewGuid();
        contact.UpdatedAt = DateTime.UtcNow;
        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return (contact.ContactId, contact.UpdatedAt);
    }

    private static async Task SeedAssociatedPropertyAsync(WebApplicationFactory<Program> factory, Guid associationId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var propertyId = Guid.NewGuid();
        context.Properties.Add(new Property
        {
            PropertyId = propertyId, Name = PropertyName, Description = "", Type = PropertyType.RealEstate, CurrencyCode = "NOK",
            RealEstateDetails = new RealEstateDetails { PropertyId = propertyId, Kind = RealEstateKind.House, HomeownerAssociationId = associationId },
        });
        await context.SaveChangesAsync();
    }

    private sealed class LoggingApiFactory() : OdysseyApiFactory(Claims, ActorUserId)
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
        }
    }
}
