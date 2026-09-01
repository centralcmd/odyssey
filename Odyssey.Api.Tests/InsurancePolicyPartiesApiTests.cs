using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.Api.Tests;

/// <summary>
/// The per-party write path: <c>POST …/parties</c>, <c>PUT …/parties/{role}/{targetId}</c> and
/// <c>DELETE …/parties/{role}/{targetId}</c> — one link at a time, carrying the TERM the full-set
/// <c>PUT</c> has nowhere to put (design system, <c>AddPolicyPartyModal</c>).
/// </summary>
/// <remarks>
/// A party is addressed by its ROLE and its TARGET rather than by a link-row id: that pair is the
/// unique index on each of the four tables, and it is what the read model already hands the caller.
/// </remarks>
public class InsurancePolicyPartiesApiTests
{
    private const string ActorUserId = "insurance-parties-actor";
    private const string Path = "/api/insurance-policies";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.InsuranceRead, PermissionClaims.InsuranceCreate,
        PermissionClaims.InsuranceUpdate, PermissionClaims.InsuranceDelete,
    ];

    private static readonly DateTime CoverStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Add ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_Party_LinksTheRecordInThatRoleOnly()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var post = await client.PostAsJsonAsync($"{Path}/{id}/parties", new InsurancePolicyPartyRequest
        {
            Role = InsurancePartyRole.Insurer,
            TargetId = contact,
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var fetched = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}");
        Assert.Equal(contact, Assert.Single(fetched!.Insurers).ContactId);
        Assert.Empty(fetched.InsuredContacts);
        Assert.Empty(fetched.Beneficiaries);
    }

    /// <summary>
    /// Both dates absent is the DEFAULT term — the policy's own extent — not an unset value, so it
    /// round-trips as null rather than being filled in from the policy's periods.
    /// </summary>
    [Fact]
    public async Task Post_PartyWithoutDates_KeepsTheDefaultTerm()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await AddRenewalAsync(client, id);

        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, contact);

        var insurer = Assert.Single((await GetAsync(client, id)).Insurers);
        Assert.Null(insurer.FromDate);
        Assert.Null(insurer.ToDate);
    }

    [Fact]
    public async Task Post_PartyWithDates_RoundTripsTheTerm()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var account = await SeedAccountAsync(factory, "House");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await AddRenewalAsync(client, id);

        var from = CoverStart.AddMonths(2);
        var to = CoverStart.AddMonths(8);
        await AddPartyAsync(client, id, InsurancePartyRole.InsuredAccount, account, from, to);

        var insured = Assert.Single((await GetAsync(client, id)).InsuredAccounts);
        Assert.Equal(from, insured.FromDate);
        Assert.Equal(to, insured.ToDate);
    }

    [Fact]
    public async Task Post_SameRecordTwiceInOneRole_Conflicts()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, contact);

        var again = await client.PostAsJsonAsync($"{Path}/{id}/parties", new InsurancePolicyPartyRequest
        {
            Role = InsurancePartyRole.Insurer,
            TargetId = contact,
        });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    /// <summary>The four collections are independent: one contact can hold two roles at once.</summary>
    [Fact]
    public async Task Post_SameRecordInTwoRoles_IsAllowed()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Alex Rivera");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        await AddPartyAsync(client, id, InsurancePartyRole.InsuredContact, contact);
        await AddPartyAsync(client, id, InsurancePartyRole.Beneficiary, contact);

        var fetched = await GetAsync(client, id);
        Assert.Equal(contact, Assert.Single(fetched.InsuredContacts).ContactId);
        Assert.Equal(contact, Assert.Single(fetched.Beneficiaries).ContactId);
    }

    [Fact]
    public async Task Post_ArchivedContact_IsRejected()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Gone Mutual", archived: true);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var post = await client.PostAsJsonAsync($"{Path}/{id}/parties", new InsurancePolicyPartyRequest
        {
            Role = InsurancePartyRole.Insurer,
            TargetId = contact,
        });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_UnknownPolicy_Is404()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync($"{Path}/{Guid.NewGuid()}/parties", new InsurancePolicyPartyRequest
        {
            Role = InsurancePartyRole.Insurer,
            TargetId = contact,
        });

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    // ── The effective cap ──────────────────────────────────────────────────────

    /// <summary>
    /// The cap is per collection and is read LIVE on every write, so a per-party add is refused with a
    /// 422 once the role's collection is full — and the number the caller is told is the number the
    /// server enforced, interpolated rather than written as a literal.
    /// </summary>
    [Fact]
    public async Task Post_PartyOverTheEffectiveCap_Returns422()
    {
        await using var factory = new ApiFactory([.. ReadWrite,
            PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        var contacts = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            contacts.Add(await SeedContactAsync(factory, $"Insurer {i}"));
        }

        using var client = factory.CreateClient();
        await SetLinkCapAsync(client, 2);
        var id = await CreateAsync(client);

        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, contacts[0]);
        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, contacts[1]);

        var over = await client.PostAsJsonAsync($"{Path}/{id}/parties", new InsurancePolicyPartyRequest
        {
            Role = InsurancePartyRole.Insurer,
            TargetId = contacts[2],
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Contains("2", await over.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The cap counts one collection, not all four: a policy at the cap for insurers can still take a
    /// beneficiary. Counting across them would make the four collections share a budget they were
    /// deliberately split to avoid.
    /// </summary>
    [Fact]
    public async Task Post_PartyInAnotherRole_IsNotBlockedByAFullCollection()
    {
        await using var factory = new ApiFactory([.. ReadWrite,
            PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        var insurer = await SeedContactAsync(factory, "Acme Insurance");
        var beneficiary = await SeedContactAsync(factory, "Sam Rivera");

        using var client = factory.CreateClient();
        await SetLinkCapAsync(client, 1);
        var id = await CreateAsync(client);

        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, insurer);

        var other = await client.PostAsJsonAsync($"{Path}/{id}/parties", new InsurancePolicyPartyRequest
        {
            Role = InsurancePartyRole.Beneficiary,
            TargetId = beneficiary,
        });

        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    /// <summary>
    /// Re-dating a party at the cap is not "one more row": the edit re-uses the row it is replacing,
    /// so a collection sitting exactly at its cap stays editable. Counting the insert without
    /// discounting the removal would make a full collection permanently frozen.
    /// </summary>
    [Fact]
    public async Task Put_PartyAtTheCap_IsStillEditable()
    {
        await using var factory = new ApiFactory([.. ReadWrite,
            PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        var insurer = await SeedContactAsync(factory, "Acme Insurance");

        using var client = factory.CreateClient();
        await SetLinkCapAsync(client, 1);
        var id = await CreateAsync(client);
        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, insurer);

        var put = await client.PutAsJsonAsync(
            $"{Path}/{id}/parties/{InsurancePartyRole.Insurer}/{insurer}",
            new InsurancePolicyPartyRequest
            {
                Role = InsurancePartyRole.Insurer,
                TargetId = insurer,
                ToDate = CoverStart.AddYears(1),
            });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(CoverStart.AddYears(1), Assert.Single((await GetAsync(client, id)).Insurers).ToDate);
    }

    // ── Term validation ────────────────────────────────────────────────────────

    /// <summary>The one tie between a party's own term and the policy's: it cannot start before
    /// cover ever did.</summary>
    [Fact]
    public async Task Post_FromBeforeCoverBegan_IsRejected()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await AddRenewalAsync(client, id);

        var post = await client.PostAsJsonAsync($"{Path}/{id}/parties", new InsurancePolicyPartyRequest
        {
            Role = InsurancePartyRole.Insurer,
            TargetId = contact,
            FromDate = CoverStart.AddDays(-1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    /// <summary>A policy with no period yet has no floor to check against.</summary>
    [Fact]
    public async Task Post_FromDateOnAPolicyWithNoPeriods_IsAccepted()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, contact, CoverStart.AddYears(-5));

        Assert.NotNull(Assert.Single((await GetAsync(client, id)).Insurers).FromDate);
    }

    [Fact]
    public async Task Post_ToBeforeFrom_IsRejected()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var post = await client.PostAsJsonAsync($"{Path}/{id}/parties", new InsurancePolicyPartyRequest
        {
            Role = InsurancePartyRole.Insurer,
            TargetId = contact,
            FromDate = CoverStart.AddMonths(6),
            ToDate = CoverStart.AddMonths(3),
        });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    // ── Edit ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_Party_RewritesTheTermInPlace()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, contact);

        var to = CoverStart.AddMonths(9);
        var put = await client.PutAsJsonAsync(
            $"{Path}/{id}/parties/{InsurancePartyRole.Insurer}/{contact}",
            new InsurancePolicyPartyRequest
            {
                Role = InsurancePartyRole.Insurer,
                TargetId = contact,
                ToDate = to,
            });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var insurer = Assert.Single((await GetAsync(client, id)).Insurers);
        Assert.Equal(to, insurer.ToDate);
    }

    /// <summary>
    /// A party moved to another role stays ONE party: the old row is dropped and the new one written,
    /// never left as two.
    /// </summary>
    [Fact]
    public async Task Put_PartyIntoAnotherRole_MovesItRatherThanDuplicating()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Alex Rivera");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await AddPartyAsync(client, id, InsurancePartyRole.InsuredContact, contact);

        var put = await client.PutAsJsonAsync(
            $"{Path}/{id}/parties/{InsurancePartyRole.InsuredContact}/{contact}",
            new InsurancePolicyPartyRequest
            {
                Role = InsurancePartyRole.Beneficiary,
                TargetId = contact,
            });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var fetched = await GetAsync(client, id);
        Assert.Empty(fetched.InsuredContacts);
        Assert.Equal(contact, Assert.Single(fetched.Beneficiaries).ContactId);
    }

    /// <summary>
    /// Re-dating a beneficiary never rewrites who named it: the designation's author is written once,
    /// at insert, and an edit that leaves the person in the role carries it across.
    /// </summary>
    [Fact]
    public async Task Put_BeneficiaryTerm_KeepsTheOriginalAuthor()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Sam Rivera");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await AddPartyAsync(client, id, InsurancePartyRole.Beneficiary, contact);

        var author = await BeneficiaryAuthorAsync(factory, id, contact);

        await client.PutAsJsonAsync(
            $"{Path}/{id}/parties/{InsurancePartyRole.Beneficiary}/{contact}",
            new InsurancePolicyPartyRequest
            {
                Role = InsurancePartyRole.Beneficiary,
                TargetId = contact,
                ToDate = CoverStart.AddYears(1),
            });

        Assert.Equal(author, await BeneficiaryAuthorAsync(factory, id, contact));
        Assert.Equal(ActorUserId, author);
    }

    [Fact]
    public async Task Put_UnknownParty_Is404()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var put = await client.PutAsJsonAsync(
            $"{Path}/{id}/parties/{InsurancePartyRole.Insurer}/{contact}",
            new InsurancePolicyPartyRequest { Role = InsurancePartyRole.Insurer, TargetId = contact });

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    // ── Remove ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Party_DetachesTheLinkAndLeavesTheContact()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, contact);

        var delete = await client.DeleteAsync($"{Path}/{id}/parties/{InsurancePartyRole.Insurer}/{contact}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Empty((await GetAsync(client, id)).Insurers);
        Assert.True(await ContactExistsAsync(factory, contact));
    }

    /// <summary>
    /// Unlike an omission from the full-set write, an explicit DELETE naming the link works even when
    /// the target is archived: an omission cannot be told apart from a caller that never saw the
    /// member, but a DELETE says exactly what it means.
    /// </summary>
    [Fact]
    public async Task Delete_PartyWhoseContactIsArchived_Succeeds()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, contact);
        await ArchiveContactAsync(factory, contact);

        var delete = await client.DeleteAsync($"{Path}/{id}/parties/{InsurancePartyRole.Insurer}/{contact}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty((await GetAsync(client, id)).Insurers);
    }

    [Fact]
    public async Task Delete_UnknownParty_Is404()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var delete = await client.DeleteAsync(
            $"{Path}/{id}/parties/{InsurancePartyRole.Beneficiary}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    // ── Interaction with the full-set write ────────────────────────────────────

    /// <summary>
    /// The Edit-policy dialog omits every collection, which means "leave unchanged" — so a policy edit
    /// can neither add nor drop a party, and a term written per party survives it.
    /// </summary>
    [Fact]
    public async Task Put_PolicyWithoutCollections_LeavesPartiesAndTheirTermsAlone()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        var to = CoverStart.AddMonths(4);
        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, contact, toDate: to);

        var put = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateInsurancePolicy
        {
            Name = "Renamed cover",
            Type = Odyssey.Dtos.Finance.InsurancePolicyType.Home,
        });
        put.EnsureSuccessStatusCode();

        var fetched = await GetAsync(client, id);
        Assert.Equal("Renamed cover", fetched.Name);
        var insurer = Assert.Single(fetched.Insurers);
        Assert.Equal(contact, insurer.ContactId);
        Assert.Equal(to, insurer.ToDate);
    }

    /// <summary>
    /// A member dropped by the full-set write takes its term with it, and a member it keeps keeps
    /// its own — the two write paths agree on one set of rows.
    /// </summary>
    [Fact]
    public async Task Put_PolicyWithCollections_KeepsTheTermOfARetainedMember()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var kept = await SeedContactAsync(factory, "Acme Insurance");
        var dropped = await SeedContactAsync(factory, "Beta Mutual");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        var to = CoverStart.AddMonths(4);
        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, kept, toDate: to);
        await AddPartyAsync(client, id, InsurancePartyRole.Insurer, dropped);

        var put = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateInsurancePolicy
        {
            Name = "Home cover",
            InsurerIds = [kept],
        });
        put.EnsureSuccessStatusCode();

        var insurer = Assert.Single((await GetAsync(client, id)).Insurers);
        Assert.Equal(kept, insurer.ContactId);
        Assert.Equal(to, insurer.ToDate);
    }

    // ── Authorization ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PartyWrites_RequireInsuranceUpdate()
    {
        await using var writer = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(writer, "Acme Insurance");
        Guid id;
        using (var client = writer.CreateClient())
        {
            id = await CreateAsync(client);
        }

        await using var reader = new ApiFactory([PermissionClaims.InsuranceRead], writer);
        using var readerClient = reader.CreateClient();

        var post = await readerClient.PostAsJsonAsync($"{Path}/{id}/parties", new InsurancePolicyPartyRequest
        {
            Role = InsurancePartyRole.Insurer,
            TargetId = contact,
        });
        var put = await readerClient.PutAsJsonAsync(
            $"{Path}/{id}/parties/{InsurancePartyRole.Insurer}/{contact}",
            new InsurancePolicyPartyRequest { Role = InsurancePartyRole.Insurer, TargetId = contact });
        var delete = await readerClient.DeleteAsync($"{Path}/{id}/parties/{InsurancePartyRole.Insurer}/{contact}");

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<Guid> CreateAsync(HttpClient client, string name = "Home cover")
    {
        var post = await client.PostAsJsonAsync(Path, new NewInsurancePolicy { Name = name });
        post.EnsureSuccessStatusCode();
        var created = await post.Content.ReadFromJsonAsync<ExistingInsurancePolicy>();
        return created!.InsurancePolicyId;
    }

    private static async Task<ExistingInsurancePolicy> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}"))!;

    private static async Task AddRenewalAsync(HttpClient client, Guid policyId)
    {
        var post = await client.PostAsJsonAsync($"{Path}/{policyId}/renewals", new NewPolicyRenewal
        {
            FromDate = CoverStart,
            ToDate = CoverStart.AddYears(1),
            Premium = 100m,
            CoverageAmount = 10000m,
        });
        post.EnsureSuccessStatusCode();
    }

    private static async Task AddPartyAsync(
        HttpClient client, Guid policyId, InsurancePartyRole role, Guid targetId,
        DateTime? fromDate = null, DateTime? toDate = null)
    {
        var post = await client.PostAsJsonAsync($"{Path}/{policyId}/parties", new InsurancePolicyPartyRequest
        {
            Role = role,
            TargetId = targetId,
            FromDate = fromDate,
            ToDate = toDate,
        });
        post.EnsureSuccessStatusCode();
    }

    private static async Task SetLinkCapAsync(HttpClient client, int value)
    {
        var put = await client.PutAsJsonAsync("/api/system-settings", new { insuranceMaxLinksPerPolicy = value });
        put.EnsureSuccessStatusCode();
    }

    private static async Task EnsureCreatedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
    }

    private static async Task<Guid> SeedContactAsync(
        WebApplicationFactory<Program> factory, string name, bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        var parts = name.Split(' ', 2);
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = name.ToUpperInvariant(),
            Type = Odyssey.Dtos.ContactType.Person,
            Archived = archived ? DateTime.UtcNow : null,
            PersonDetails = new() { ContactId = id, FirstName = parts[0], LastName = parts.Length > 1 ? parts[1] : "X" },
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedAccountAsync(WebApplicationFactory<Program> factory, string name)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.Accounts.Add(new Account
        {
            AccountId = id,
            Name = name,
            Description = "Insured asset",
            Opened = DateTime.UtcNow,
            AccountType = ContextAccountType.Property,
            CurrencyCode = "USD",
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task ArchiveContactAsync(WebApplicationFactory<Program> factory, Guid contactId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var contact = await context.Contacts.SingleAsync(c => c.ContactId == contactId);
        contact.Archived = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    private static async Task<bool> ContactExistsAsync(WebApplicationFactory<Program> factory, Guid contactId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Contacts.AnyAsync(c => c.ContactId == contactId);
    }

    private static async Task<string?> BeneficiaryAuthorAsync(
        WebApplicationFactory<Program> factory, Guid policyId, Guid contactId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return (await context.InsurancePolicyBeneficiaries.AsNoTracking()
            .SingleAsync(b => b.InsurancePolicyId == policyId && b.ContactId == contactId)).CreatedByUserId;
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(IReadOnlyCollection<string>? permissions)
            : base(permissions, ActorUserId)
        {
        }

        /// <summary>A second principal over the SAME in-memory store, so a claim check can be
        /// exercised against a policy another caller created.</summary>
        public ApiFactory(IReadOnlyCollection<string>? permissions, OdysseyApiFactory sharing)
            : base(permissions, ActorUserId, sharingStoreWith: sharing)
        {
        }
    }
}
