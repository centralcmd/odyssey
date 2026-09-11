using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-tier coverage for the contact-method label scope (issue #47 §7, §16.1–5, §16.9, §16.18,
/// §16.23): the <c>422</c> and what distinguishes it from the <c>400</c> above it.
/// </summary>
public class ContactMethodLabelScopeApiTests
{
    private const string ActorUserId = "label-scope-actor";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate,
        PermissionClaims.ContactsUpdate, PermissionClaims.ContactsDelete,
    ];

    // ── The happy path, and the ordinal that goes on the wire (§16.1) ─────────

    [Fact]
    public async Task Posting_an_organization_label_to_an_organization_creates_it_and_persists_the_ordinal()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory, ContactType.Organization);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync($"/api/contacts/{id}/phones",
            new NewPhoneNumber { Label = PhoneLabel.Switchboard, Value = "+47 22 00 00 00" });

        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        // The enums serialize as integers (no JsonStringEnumConverter), so the ORDINAL is the wire
        // contract — read it off the raw JSON rather than through the typed DTO, which would hide a
        // renumbering behind its own deserialization.
        var raw = await client.GetStringAsync($"/api/contacts/{id}/phones");
        Assert.Contains("\"label\":20", raw);
    }

    // ── The rejection, in both directions (§16.2, §16.3) ─────────────────────

    [Fact]
    public async Task Posting_a_person_label_to_an_organization_is_refused_with_422_on_the_label_field()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory, ContactType.Organization);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync($"/api/contacts/{id}/phones",
            new NewPhoneNumber { Label = PhoneLabel.Home, Value = "+47 22 00 00 00" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);

        var message = await LabelErrorAsync(post);
        Assert.Contains("Home", message);
        Assert.Contains("Organization", message);
        Assert.Contains("Switchboard", message);
    }

    [Fact]
    public async Task Posting_an_organization_label_to_a_person_is_refused_with_the_mirror_422()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory, ContactType.Person);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync($"/api/contacts/{id}/phones",
            new NewPhoneNumber { Label = PhoneLabel.Switchboard, Value = "+47 22 00 00 00" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);

        var message = await LabelErrorAsync(post);
        Assert.Contains("Switchboard", message);
        Assert.Contains("Person", message);
    }

    [Fact]
    public async Task The_scope_rejection_covers_all_three_contact_method_kinds()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory, ContactType.Organization);
        using var client = factory.CreateClient();

        var email = await client.PostAsJsonAsync($"/api/contacts/{id}/emails",
            new NewEmailAddress { Label = EmailLabel.Home, Value = "post@acme.example" });
        var address = await client.PostAsJsonAsync($"/api/contacts/{id}/addresses",
            new NewAddress { Label = AddressLabel.Home, Line1 = "Storgata 55", City = "Oslo", CountryCode = "NO" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, email.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, address.StatusCode);
        Assert.NotNull(await LabelErrorAsync(email));
        Assert.NotNull(await LabelErrorAsync(address));
    }

    // ── 400 vs 422: an undefined ordinal is malformed, not unprocessable (§16.4) ──

    /// <summary>
    /// An ordinal the enum does not define is rejected by <c>[EnumDataType]</c> in model validation,
    /// <i>before</i> the service runs — so it is a <c>400</c>, and nothing about the parent contact's
    /// type is consulted or disclosed.
    /// </summary>
    [Fact]
    public async Task Posting_an_undefined_ordinal_is_a_400_not_a_422()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory, ContactType.Organization);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync($"/api/contacts/{id}/phones",
            new { label = 99, isPrimary = false, value = "+47 22 00 00 00" });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    // ── PUT and POST agree (§16.5) ───────────────────────────────────────────

    /// <summary>
    /// The check does not consult the stored value, so it fires on a <c>PUT</c> that merely resubmits a
    /// label the row already holds. That is what removes the ordering trap: <c>Update*</c> assigns
    /// <c>Label</c> before it assigns anything else, and a check written as "did the label change?"
    /// would have been silently dead on all three <c>PUT</c>s while every <c>POST</c> test still passed.
    /// </summary>
    [Fact]
    public async Task Putting_an_invalid_label_is_refused_even_when_the_row_already_stores_it()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory, ContactType.Organization);
        var phoneId = await SeedPhoneAsync(factory, id, PhoneLabel.Home);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"/api/contacts/{id}/phones/{phoneId}",
            new NewPhoneNumber { Label = PhoneLabel.Home, Value = "+47 22 00 00 00" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Contains("Home", await LabelErrorAsync(put));
    }

    /// <summary>
    /// The residual, server-side (§16.23): "Set as primary" rebuilds the whole body from the stored row
    /// and re-<c>PUT</c>s it, bypassing the picker — so a row left invalid by the concurrent race in §9
    /// fails there with a message the user can act on, not a generic failure.
    /// </summary>
    [Fact]
    public async Task Resubmitting_a_stored_invalid_label_as_primary_is_refused_with_an_explicable_message()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory, ContactType.Organization);
        var phoneId = await SeedPhoneAsync(factory, id, PhoneLabel.Home);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"/api/contacts/{id}/phones/{phoneId}",
            new NewPhoneNumber { Label = PhoneLabel.Home, IsPrimary = true, Value = "+47 22 00 00 00" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);

        // ApiInteropExtensions renders "{lead}: {Problem.Message}", i.e. the problem DETAIL — so the
        // detail, not only the errors dictionary, has to carry the explanation.
        var problem = await put.Content.ReadFromJsonAsync<ProblemShape>();
        Assert.Contains("Home", problem!.Detail ?? "");
        Assert.Contains("Valid labels", problem.Detail ?? "");
    }

    /// <summary>A <c>PUT</c> at a row that does not exist is still a <c>404</c> — the scope check runs
    /// after the row lookup, so a bad label cannot mask a missing record.</summary>
    [Fact]
    public async Task A_missing_row_still_404s_even_with_an_invalid_label()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory, ContactType.Organization);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"/api/contacts/{id}/phones/{Guid.NewGuid()}",
            new NewPhoneNumber { Label = PhoneLabel.Home, Value = "+47 22 00 00 00" });

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    /// <summary>Authorization runs first: a principal without the write claim never reaches the
    /// <c>422</c>, so the rejection cannot become an existence-and-type oracle for it (§16.7).</summary>
    [Fact]
    public async Task Authorization_is_reached_before_the_scope_check()
    {
        await using var factory = new ApiFactory([PermissionClaims.ContactsRead]);
        var id = await SeedContactAsync(factory, ContactType.Organization);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync($"/api/contacts/{id}/phones",
            new NewPhoneNumber { Label = PhoneLabel.Home, Value = "+47 22 00 00 00" });

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    // ── vCard import survives the new exception type (§16.18) ────────────────

    /// <summary>
    /// <c>ContactVCardService</c> caught <c>DomainValidationException</c> at four sites. A
    /// <c>DomainUnprocessableException</c> would escape all four — aborted transaction, <c>500</c> on
    /// the endpoint, and a dirty change tracker for every later entry in the same file. The clamp means
    /// the scope check should not fire on this path at all; this pins the outcome either way.
    /// </summary>
    [Fact]
    public async Task Importing_a_vcard_whose_labels_are_out_of_scope_stays_a_2xx_and_keeps_every_row()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var vcf = string.Concat(
            "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:org-tokens\r\nKIND:org\r\nFN:Acme\r\nORG:Acme\r\n",
            "TEL;TYPE=home:+47 22 00 00 00\r\nEMAIL;TYPE=work:post@acme.example\r\n",
            "ADR;TYPE=home:;;Storgata 55;Oslo;;0184;NO\r\nEND:VCARD\r\n",
            "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:person-after\r\nKIND:individual\r\nFN:Ada Lovelace\r\n",
            "N:Lovelace;Ada;;;\r\nTEL;TYPE=cell:+47 900 00 000\r\nEND:VCARD\r\n");

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(vcf));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/vcard");
        content.Add(file, "file", "contacts.vcf");

        var post = await client.PostAsync("/api/contacts/vcard", content);

        Assert.True((int)post.StatusCode is >= 200 and < 300, $"Expected 2xx, got {(int)post.StatusCode}.");

        var result = await post.Content.ReadFromJsonAsync<VCardImportResult>();
        Assert.Equal(2, result!.CreatedCount);
        Assert.Empty(result.Skipped);

        // The entry AFTER the organization one still landed — proof the change tracker stayed clean.
        var contacts = await client.GetStringAsync("/api/contacts?page=1&pageSize=50");
        Assert.Contains("Lovelace", contacts);
    }

    // ── Mass-assignment surface (§16.9) ──────────────────────────────────────

    /// <summary>
    /// A positive allow-list over the write DTOs' property <i>types</i>, not a request-body test: these
    /// DTOs expose no navigation property, so a nested-object over-post test could never fail and would
    /// be the decorative-ceiling pattern CLAUDE.md warns about. This fails the moment someone adds an
    /// entity reference or a collection to one of them.
    /// </summary>
    [Theory]
    [InlineData(typeof(NewAddress))]
    [InlineData(typeof(NewEmailAddress))]
    [InlineData(typeof(NewPhoneNumber))]
    public void The_contact_method_write_dtos_expose_only_scalar_properties(Type dto)
    {
        foreach (var property in dto.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var permitted = type == typeof(string) || type == typeof(bool) || type == typeof(Guid) || type.IsEnum;

            Assert.True(permitted, $"{dto.Name}.{property.Name} is {type.Name}, which is not a permitted scalar type.");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record ProblemShape(string? Title, int? Status, string? Detail);

    private static async Task<string> LabelErrorAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemShape>();
        Assert.NotNull(problem?.Errors);

        var key = problem!.Errors!.Keys.FirstOrDefault(k => string.Equals(k, "label", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(key);
        return Assert.Single(problem.Errors[key!]);
    }

    private sealed record ValidationProblemShape(IDictionary<string, string[]>? Errors);

    private static async Task EnsureDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
    }

    private static async Task<Guid> SeedContactAsync(WebApplicationFactory<Program> factory, ContactType type)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        var contact = new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "ACME",
            Type = type,
        };

        if (type == ContactType.Person)
            contact.PersonDetails = new() { FirstName = "Ada", LastName = "Lovelace" };
        else
            contact.OrganizationDetails = new() { LegalName = "Acme" };

        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return id;
    }

    // Written straight to the context, deliberately bypassing ContactService: this seeds the state the
    // concurrent race in §9 produces, which no supported write path can reach.
    private static async Task<Guid> SeedPhoneAsync(WebApplicationFactory<Program> factory, Guid contactId, PhoneLabel label)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var phone = new PhoneNumber { ContactId = contactId, Label = label, Value = "+47 22 00 00 00" };
        context.PhoneNumbers.Add(phone);
        await context.SaveChangesAsync();
        return phone.Id;
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
