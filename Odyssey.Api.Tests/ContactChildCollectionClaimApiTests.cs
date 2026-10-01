using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// A contact's child collections — aliases, addresses, emails and phones — are written under
/// <c>contacts.update</c> on every verb, like every other sub-resource (issue #287 M3). Adding or
/// removing a phone number edits the contact; it neither creates nor deletes one, so
/// <c>contacts.create</c> and <c>contacts.delete</c> confer nothing here.
/// </summary>
public class ContactChildCollectionClaimApiTests
{
    private static readonly string[] UpdateOnly = [PermissionClaims.ContactsRead, PermissionClaims.ContactsUpdate];

    private static readonly string[] CreateAndDeleteWithoutUpdate =
        [PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate, PermissionClaims.ContactsDelete];

    public static TheoryData<string> Collections => new() { "aliases", "addresses", "emails", "phones" };

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task UpdateClaimAlone_AddsReplacesAndRemovesAChild(string collection)
    {
        await using var factory = new OdysseyApiFactory(UpdateOnly);
        var contactId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();
        var path = $"/api/contacts/{contactId}/{collection}";

        var created = await client.PostAsJsonAsync(path, Body(collection));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var childId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"{path}/{childId}", Body(collection))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{path}/{childId}")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task CreateAndDeleteWithoutUpdate_AreForbiddenOnEveryWriteVerb(string collection)
    {
        await using var factory = new OdysseyApiFactory(CreateAndDeleteWithoutUpdate);
        var contactId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();
        var path = $"/api/contacts/{contactId}/{collection}";

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, Body(collection))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"{path}/{Guid.NewGuid()}", Body(collection))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{path}/{Guid.NewGuid()}")).StatusCode);
    }

    private static object Body(string collection) => collection switch
    {
        "aliases" => new NewContactAlias { Value = "Kari" },
        "addresses" => new NewAddress { Label = AddressLabel.Other, Line1 = "Storgata 55", City = "Oslo", CountryCode = "NO" },
        "emails" => new NewEmailAddress { Label = EmailLabel.Other, Value = "post@example.com" },
        "phones" => new NewPhoneNumber { Label = PhoneLabel.Other, Value = "+47 22 00 00 00" },
        _ => throw new ArgumentOutOfRangeException(nameof(collection)),
    };

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
}
