using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The value-free audit trail (issue #48 §10.9, AC 22, AC 49).
///
/// <para>
/// <c>Contact.UpdatedAt</c> records <i>that</i> something changed and never <i>who</i> or
/// <i>what</i>, which cannot answer "who recorded this?" or, after an incident, "whose maiden names
/// were read?" — exactly what GDPR Art. 33 breach scoping and Art. 5(2) accountability require. The
/// "consistent with the siblings" argument is weaker for an alias than for a phone number, so these
/// writes emit a structured event.
/// </para>
///
/// <para>
/// Every assertion here has two halves, and the second is the load-bearing one: the event fires,
/// <b>and it carries no alias value and no label</b>. An audit line has a wider audience and a longer
/// retention than the contacts table, and an alias is frequently a maiden name.
/// </para>
/// </summary>
public class ContactAliasAuditTests
{
    private const string ActorUserId = "audit-actor-id";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate,
        PermissionClaims.ContactsUpdate, PermissionClaims.ContactsDelete,
    ];

    private const string Value = "Hansen";
    private const string Label = "maiden name";

    [Fact]
    public async Task EveryAliasMutation_EmitsAValueFreeEvent()
    {
        await using var factory = new LoggingApiFactory(ReadWrite);
        var contactId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(
            $"/api/contacts/{contactId}/aliases", new NewContactAlias { Value = Value, Label = Label });
        var alias = (await created.Content.ReadFromJsonAsync<ExistingContactAlias>())!;

        await client.PutAsJsonAsync(
            $"/api/contacts/{contactId}/aliases/{alias.Id}", new NewContactAlias { Value = Value, Label = "nickname" });
        await client.DeleteAsync($"/api/contacts/{contactId}/aliases/{alias.Id}");

        foreach (var action in new[] { "alias.created", "alias.updated", "alias.deleted" })
        {
            var line = Assert.Single(AuditLines(factory, action));
            Assert.Contains(contactId.ToString(), line);
            Assert.Contains(ActorUserId, line);
        }

        AssertNothingEchoed(factory);
    }

    // Recording a death and clearing it are distinguished, because only the prior value can say which
    // happened — which is why the PUT reads the contact before it writes. The DATE itself is not in
    // the line: the event names the transition, not the value.
    [Fact]
    public async Task RecordingAndClearingADateOfDeath_EmitSeparateValueFreeEvents()
    {
        await using var factory = new LoggingApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/contacts", Person(dateOfDeath: null));
        var contactId = await OnlyContactIdAsync(client);
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        Assert.Empty(AuditLines(factory, "dateOfDeath.set"));

        await client.PutAsJsonAsync($"/api/contacts/{contactId}", Person(new DateTime(2024, 3, 11)));
        Assert.Single(AuditLines(factory, "dateOfDeath.set"));

        await client.PutAsJsonAsync($"/api/contacts/{contactId}", Person(dateOfDeath: null));
        Assert.Single(AuditLines(factory, "dateOfDeath.cleared"));

        // An unchanged save emits neither — the event marks a transition, not a request.
        await client.PutAsJsonAsync($"/api/contacts/{contactId}", Person(dateOfDeath: null));
        Assert.Single(AuditLines(factory, "dateOfDeath.cleared"));

        foreach (var line in ContactLines(factory))
        {
            Assert.DoesNotContain("2024", line, StringComparison.Ordinal);
            Assert.DoesNotContain("03-11", line, StringComparison.Ordinal);
        }
    }

    // §10.11: a contacts.read holder — Guest included — can download the whole corpus, maiden names
    // and dates of death with it, in one request. The line records the row count and whether filters
    // were applied; no names and no values.
    [Fact]
    public async Task ExportingVCards_EmitsAValueFreeEvent()
    {
        await using var factory = new LoggingApiFactory(ReadWrite);
        var contactId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync(
            $"/api/contacts/{contactId}/aliases", new NewContactAlias { Value = Value, Label = Label });

        (await client.GetAsync("/api/contacts/vcard")).EnsureSuccessStatusCode();

        var line = Assert.Single(
            ContactLines(factory),
            message => message.Contains("vCard export", StringComparison.Ordinal));
        Assert.Contains(ActorUserId, line);
        Assert.Contains("(all)", line);

        AssertNothingEchoed(factory);
    }

    // A REFUSED write emits no event and, critically, no line carrying the submitted value — the
    // 1062 path is caught in the service precisely so MariaDB's "Duplicate entry 'xxx-Hansen'" never
    // reaches the logging global handler.
    [Fact]
    public async Task ADuplicateRejection_LogsNothingContainingTheValue()
    {
        await using var factory = new LoggingApiFactory(ReadWrite);
        var contactId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync(
            $"/api/contacts/{contactId}/aliases", new NewContactAlias { Value = Value, Label = Label });

        var rejected = await client.PostAsJsonAsync(
            $"/api/contacts/{contactId}/aliases", new NewContactAlias { Value = "hansen" });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, rejected.StatusCode);

        Assert.Single(AuditLines(factory, "alias.created"));
        foreach (var entry in factory.Logs.Entries)
        {
            Assert.DoesNotContain(Value, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Value, entry.Exception?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertNothingEchoed(LoggingApiFactory factory)
    {
        foreach (var entry in factory.Logs.Entries)
        {
            Assert.DoesNotContain(Value, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Label, entry.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static List<string> ContactLines(LoggingApiFactory factory) =>
        [.. factory.Logs.ForCategory("ContactController").Select(entry => entry.Message)];

    private static List<string> AuditLines(LoggingApiFactory factory, string action) =>
        [.. ContactLines(factory).Where(message => message.Contains($" {action} by ", StringComparison.Ordinal))];

    private static NewContact Person(DateTime? dateOfDeath) => new()
    {
        Type = ContactType.Person,
        Archived = false,
        PersonDetails = new PersonDetailsDto
        {
            FirstName = "Karoline",
            LastName = "Hansen",
            DateOfDeath = dateOfDeath,
        },
    };

    private static async Task<Guid> OnlyContactIdAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<PagedResult<ExistingContact>>("/api/contacts"))!.Items.Single().ContactId;

    private static async Task EnsureDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
    }

    private static async Task<Guid> SeedContactAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "ACME",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Acme" },
        });
        await context.SaveChangesAsync();
        return id;
    }

    private sealed class LoggingApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId)
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
        }
    }
}
