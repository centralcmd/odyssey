using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.Api.Tests;

/// <summary>
/// The cross-claim exposure boundary (issue #48 §10.2, AC 17–18).
///
/// <para>
/// <c>ExistingTransaction.Contact</c> is reached through <c>transactions.read</c>, which is <b>not</b>
/// <c>contacts.read</c>. It used to carry the whole <see cref="ExistingContact"/>, so every contact
/// field already reached a caller holding no <c>contacts.*</c> claim at all — and the alias list, the
/// middle name and the three lifecycle dates would have joined them with zero further code change the
/// moment anyone added an <c>Include</c> to the query behind the lookup.
/// </para>
///
/// <para>
/// These assertions are deliberately made against a <b>live response body</b> rather than against a
/// projection's members: a member-level check over <c>ContactRef</c> alone — the projection the
/// obvious guard would have picked — passed green throughout the period the leak existed.
/// </para>
/// </summary>
public class ContactEmbedExposureTests
{
    private const string ActorUserId = "embed-actor-id";

    // AC 18: exactly two members. This is the structural half of the fix — the reason a future
    // Include cannot re-widen it — so the count is asserted, not just the absence of specific fields.
    [Fact]
    public void ContactEmbed_HasExactlyTwoMembers()
    {
        var properties = typeof(ContactEmbed).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();

        Assert.Equal(["ContactId", "ResolvedDisplayName"], properties);
    }

    // Named individually as well, because the count alone would let a rename swap one sensitive
    // member in for a benign one. OrganizationNumber is called out because it is the near miss: an
    // organisasjonsnummer is a real-world join key into the Enhetsregisteret.
    [Theory]
    [InlineData("Aliases")]
    [InlineData("PersonDetails")]
    [InlineData("OrganizationDetails")]
    [InlineData("Type")]
    [InlineData("Archived")]
    [InlineData("Notes")]
    [InlineData("Addresses")]
    [InlineData("EmailAddresses")]
    [InlineData("PhoneNumbers")]
    [InlineData("ExternalUid")]
    public void ContactEmbed_DoesNotCarry(string member)
    {
        Assert.Null(typeof(ContactEmbed).GetProperty(member));
    }

    // AC 17: the live body. A principal with transactions.read and NO contacts.* claim gets the
    // counterparty's name and id, and nothing else — no aliases, no middleName, no dateOfDeath, no
    // establishedDate, no dissolvedDate.
    [Fact]
    public async Task GetTransactions_WithoutAnyContactsClaim_EmbedsOnlyIdAndName()
    {
        await using var factory = new ApiFactory([PermissionClaims.TransactionsRead]);
        var (accountId, contactId) = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/api/transactions");

        // The name IS there — narrowing must not break the surface it serves.
        Assert.Contains("Karoline Hansen", body);

        foreach (var leaked in new[] { "aliases", "middleName", "dateOfDeath", "establishedDate", "dissolvedDate" })
        {
            Assert.DoesNotContain(leaked, body, StringComparison.OrdinalIgnoreCase);
        }

        // And nothing that would have ridden along with the full record.
        Assert.DoesNotContain("Berg", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Marie", body, StringComparison.Ordinal);

        // The embedded object's own shape, read from the wire rather than from a type.
        using var document = JsonDocument.Parse(body);
        var embedded = document.RootElement
            .GetProperty("items")[0]
            .GetProperty("contact");
        Assert.Equal(
            ["contactId", "resolvedDisplayName"],
            embedded.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(contactId, embedded.GetProperty("contactId").GetGuid());
        Assert.Equal(accountId, document.RootElement.GetProperty("items")[0].GetProperty("accountId").GetGuid());
    }

    // The same fields ARE served, in full, to a caller holding contacts.read — the narrowing moved
    // them behind the right claim rather than removing them.
    [Fact]
    public async Task GetContact_WithContactsRead_StillCarriesEverything()
    {
        await using var factory = new ApiFactory([PermissionClaims.ContactsRead]);
        var (_, contactId) = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contact = await client.GetFromJsonAsync<ExistingContact>($"/api/contacts/{contactId}");

        Assert.Equal("Marie", contact!.PersonDetails!.MiddleName);
        Assert.Equal(new DateTime(2024, 3, 11), contact.PersonDetails.DateOfDeath);
        Assert.Equal("Berg", Assert.Single(contact.Aliases).Value);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<(Guid AccountId, Guid ContactId)> SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        var contactId = Guid.NewGuid();

        context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = "Seeded",
            Description = "Seeded account",
            Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
        });

        context.Contacts.Add(new Contact
        {
            ContactId = contactId,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "KAROLINE HANSEN",
            Type = ContactType.Person,
            PersonDetails = new PersonDetails
            {
                ContactId = contactId,
                FirstName = "Karoline",
                LastName = "Hansen",
                MiddleName = "Marie",
                DateOfDeath = new DateOnly(2024, 3, 11),
            },
            Aliases = [new ContactAlias { ContactId = contactId, Value = "Berg", Label = "maiden name" }],
        });

        context.Transactions.Add(new Transaction
        {
            TransactionId = Guid.NewGuid(),
            Description = "Seeded",
            Amount = 100m,
            TimeStamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            AccountId = accountId,
            ContactId = contactId,
            CurrencyCode = "USD",
            Status = TransactionStatus.Approved,
        });

        await context.SaveChangesAsync();
        return (accountId, contactId);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
