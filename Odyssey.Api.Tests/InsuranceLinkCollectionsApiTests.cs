using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.Api.Tests;

/// <summary>
/// The four link collections on an insurance policy (issue #27): insurers, insured accounts, insured
/// contacts and beneficiaries. Covers the write semantics (<c>null</c> = unchanged, <c>[]</c> = clear,
/// dedupe, the effective cap), the read projection (an archived link keeps its row and loses its
/// name), the blocked contact delete and its transactional detach path.
/// </summary>
public class InsuranceLinkCollectionsApiTests
{
    private const string ActorUserId = "insurance-links-actor";
    private const string Path = "/api/insurance-policies";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.InsuranceRead, PermissionClaims.InsuranceCreate,
        PermissionClaims.InsuranceUpdate, PermissionClaims.InsuranceDelete,
    ];

    // ── Write semantics ────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_WithAllFourCollections_ReturnsThemExactly()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var insurerA = await SeedContactAsync(factory, "Acme Insurance");
        var insurerB = await SeedContactAsync(factory, "Beta Mutual");
        var accountA = await SeedAccountAsync(factory, "House");
        var accountB = await SeedAccountAsync(factory, "Outbuilding");
        var insured = await SeedContactAsync(factory, "Alex Rivera");
        var beneficiaryA = await SeedContactAsync(factory, "Sam Rivera");
        var beneficiaryB = await SeedContactAsync(factory, "Chris Rivera");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewInsurancePolicy
        {
            Name = "Home & contents",
            InsurerIds = [insurerA, insurerB],
            InsuredAccountIds = [accountA, accountB],
            InsuredContactIds = [insured],
            BeneficiaryIds = [beneficiaryA, beneficiaryB],
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var created = await post.Content.ReadFromJsonAsync<ExistingInsurancePolicy>();
        var fetched = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{created!.InsurancePolicyId}");

        Assert.Equal(new[] { insurerA, insurerB }.Order(), fetched!.Insurers.Select(i => i.ContactId).Order());
        Assert.Equal(new[] { accountA, accountB }.Order(), fetched.InsuredAccounts.Select(a => a.AccountId).Order());
        Assert.Equal(insured, Assert.Single(fetched.InsuredContacts).ContactId);
        Assert.Equal(new[] { beneficiaryA, beneficiaryB }.Order(), fetched.Beneficiaries.Select(b => b.ContactId).Order());
        Assert.All(fetched.Insurers, i => Assert.Equal(LinkAvailability.Available, i.Availability));
    }

    /// <summary>Zero members is a valid, healthy state for all four — including zero insurers.</summary>
    [Fact]
    public async Task Post_WithNoLinksAtAll_Succeeds()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewInsurancePolicy { Name = "Draft policy" });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var created = await post.Content.ReadFromJsonAsync<ExistingInsurancePolicy>();
        var fetched = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{created!.InsurancePolicyId}");

        Assert.Empty(fetched!.Insurers);
        Assert.Empty(fetched.InsuredAccounts);
        Assert.Empty(fetched.InsuredContacts);
        Assert.Empty(fetched.Beneficiaries);
        // NoCoverage because it has no RENEWALS — coverage derives from renewals alone and is
        // unrelated to link emptiness.
        Assert.Equal(CoverageStatus.NoCoverage, fetched.CoverageStatus);
    }

    [Fact]
    public async Task Put_ReplacingACollection_LeavesExactlyTheSubmittedSet()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var a = await SeedContactAsync(factory, "A");
        var b = await SeedContactAsync(factory, "B");
        var c = await SeedContactAsync(factory, "C");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurers: [a, b]);
        var beforeLinkId = await LinkIdAsync(factory, id, b);

        var put = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateInsurancePolicy
        {
            Name = "Home",
            InsurerIds = [b, c],
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var fetched = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}");
        Assert.Equal(new[] { b, c }.Order(), fetched!.Insurers.Select(i => i.ContactId).Order());

        // The surviving member is the SAME row, not a delete-and-reinsert: the diff touches only what
        // actually changed, which is what lets a link table carry per-row data (attribution today,
        // shares later) without a save quietly resetting it.
        Assert.Equal(beforeLinkId, await LinkIdAsync(factory, id, b));
    }

    /// <summary>
    /// The semantic the whole write shape rests on: <c>null</c> leaves a collection alone, <c>[]</c>
    /// clears it. Both directions asserted, because a partially-constructed body that wiped a
    /// beneficiary designation silently is exactly what this prevents.
    /// </summary>
    [Fact]
    public async Task Put_OmittedCollectionIsUnchanged_EmptyArrayClearsIt()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var insurer = await SeedContactAsync(factory, "Acme");
        var beneficiary = await SeedContactAsync(factory, "Sam");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurers: [insurer], beneficiaries: [beneficiary]);

        // Omitted → unchanged.
        var rename = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateInsurancePolicy { Name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var afterRename = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}");
        Assert.Equal(beneficiary, Assert.Single(afterRename!.Beneficiaries).ContactId);
        Assert.Single(afterRename.Insurers);

        // Explicit [] → cleared.
        var clear = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateInsurancePolicy
        {
            Name = "Renamed",
            BeneficiaryIds = [],
        });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var afterClear = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}");
        Assert.Empty(afterClear!.Beneficiaries);
        // The untouched collection is still untouched.
        Assert.Single(afterClear.Insurers);
    }

    /// <summary>A set-valued field is naturally idempotent — duplicates dedupe rather than 400.</summary>
    [Fact]
    public async Task Post_WithTheSameIdTwice_KeepsItOnce()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var insurer = await SeedContactAsync(factory, "Acme");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewInsurancePolicy
        {
            Name = "Home",
            InsurerIds = [insurer, insurer],
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var created = await post.Content.ReadFromJsonAsync<ExistingInsurancePolicy>();
        Assert.Equal(insurer, Assert.Single(created!.Insurers).ContactId);
    }

    [Fact]
    public async Task Post_NamingAnArchivedContact_ReturnsBadRequestEchoingTheId()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var archived = await SeedContactAsync(factory, "Gone", archived: true);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewInsurancePolicy
        {
            Name = "Home",
            BeneficiaryIds = [archived],
        });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        var body = await post.Content.ReadAsStringAsync();
        Assert.Contains(archived.ToString(), body, StringComparison.OrdinalIgnoreCase);
        // Attributed to the offending field, so the dialog can mark and focus that picker.
        Assert.Contains(nameof(UpdateInsurancePolicy.BeneficiaryIds), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_NamingAnUnknownAccount_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewInsurancePolicy
        {
            Name = "Home",
            InsuredAccountIds = [Guid.NewGuid()],
        });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    // ── The effective cap ──────────────────────────────────────────────────────

    /// <summary>
    /// The cap is read LIVE on every write: lowering it takes effect on the next request, and raising
    /// it does too — which is only true because the registry evicts the cache entry the caps are
    /// actually served from.
    /// </summary>
    [Fact]
    public async Task Post_OverTheEffectiveCap_Returns422_AndRaisingItTakesEffectImmediately()
    {
        await using var factory = new ApiFactory([.. ReadWrite,
            PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        var contacts = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            contacts.Add(await SeedContactAsync(factory, $"Insurer {i}"));
        }

        using var client = factory.CreateClient();
        await SetLinkCapAsync(client, 3);

        var over = await client.PostAsJsonAsync(Path, new NewInsurancePolicy
        {
            Name = "Home",
            InsurerIds = contacts,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        // The number the user is told is the number the server enforced — interpolated, never a literal.
        Assert.Contains("3", await over.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await SetLinkCapAsync(client, 5);

        var within = await client.PostAsJsonAsync(Path, new NewInsurancePolicy
        {
            Name = "Home",
            InsurerIds = contacts,
        });
        Assert.Equal(HttpStatusCode.Created, within.StatusCode);
    }

    [Fact]
    public async Task SettingsPut_RaisingTheCapAboveTheCompileTimeCeiling_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory([.. ReadWrite,
            PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/system-settings", new
        {
            insuranceMaxLinksPerPolicy = InsuranceLinkLimits.MaxLinksPerPolicy + 1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains(
            InsuranceLinkLimits.MaxLinksPerPolicy.ToString(),
            await put.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // ── The archived-link read path, and the blocker regression ────────────────

    /// <summary>
    /// The blocker Draft v2 introduced and Draft v3/v4 fixed. A policy holds two beneficiaries; one's
    /// contact is archived. The read must return <b>both</b> — the archived one without its name — so
    /// an ordinary read-modify-write round trip cannot silently delete the link the user never saw.
    /// </summary>
    [Fact]
    public async Task Get_ArchivedBeneficiary_KeepsItsRowAndLosesItsName()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var b1 = await SeedContactAsync(factory, "Present Person");
        var b2 = await SeedContactAsync(factory, "Archived Person");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, beneficiaries: [b1, b2]);
        await ArchiveContactAsync(factory, b2);

        var response = await client.GetAsync($"{Path}/{id}");
        var json = await response.Content.ReadAsStringAsync();
        var fetched = JsonSerializer.Deserialize<ExistingInsurancePolicy>(json, Web);

        Assert.Equal(2, fetched!.Beneficiaries.Count);
        var archived = fetched.Beneficiaries.Single(b => b.ContactId == b2);
        Assert.Null(archived.Name);
        Assert.Equal(LinkAvailability.Archived, archived.Availability);
        // The type survives — it is strictly redundant for anyone who can resolve the id.
        Assert.Equal(ContactType.Person, archived.Type);
        // The name is the personal data, and it appears NOWHERE in the response. Asserted against the
        // serialized JSON, because the minimisation lives in the mapping rather than in the query.
        Assert.DoesNotContain("Archived Person", json, StringComparison.OrdinalIgnoreCase);

        // The count on the list row counts ROWS, so it counts the unnamed one too.
        var list = await client.GetFromJsonAsync<PagedResult<InsurancePolicyListItem>>(Path);
        Assert.Equal(2, list!.Items.Single(p => p.InsurancePolicyId == id).BeneficiaryCount);
    }

    /// <summary>
    /// An echo-it-back save round-trips the archived link untouched — the ordinary edit path — while a
    /// hand-built body that OMITS it is refused with a 422 rather than silently ignored, and changes
    /// nothing at all.
    /// </summary>
    [Fact]
    public async Task Put_EchoingAnArchivedLink_Succeeds_ButOmittingItIsRefused()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var b1 = await SeedContactAsync(factory, "Present Person");
        var b2 = await SeedContactAsync(factory, "Archived Person");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, beneficiaries: [b1, b2]);
        await ArchiveContactAsync(factory, b2);

        // The dialog's own save: it re-sends what the read handed it, including the unnamed member.
        var echo = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateInsurancePolicy
        {
            Name = "Renamed policy",
            BeneficiaryIds = [b1, b2],
        });
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        Assert.Equal(2, await BeneficiaryRowCountAsync(factory, id));

        // A hand-built body omitting it. Refused loudly: a 200 would misdescribe what happened, and
        // silently keeping it would swallow a genuinely deliberate removal in the archive race.
        var omit = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateInsurancePolicy
        {
            Name = "Name that must not stick",
            BeneficiaryIds = [b1],
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, omit.StatusCode);

        var body = await omit.Content.ReadAsStringAsync();
        Assert.Contains(b2.ToString(), body, StringComparison.OrdinalIgnoreCase);
        // Both routes that DO work are named, detach first.
        Assert.Contains("Detach", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unarchive", body, StringComparison.OrdinalIgnoreCase);

        // Nothing was applied — not the link removal, and not the rename that rode along with it.
        Assert.Equal(2, await BeneficiaryRowCountAsync(factory, id));
        var fetched = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}");
        Assert.Equal("Renamed policy", fetched!.Name);
    }

    /// <summary>
    /// The cap counts the RESULTING rows, not the submitted array: a collection holding retained
    /// unnamed links cannot be topped up to more rows than the cap allows — a state the client could
    /// then never round-trip, since it is also past <c>[MaxLength]</c>.
    /// </summary>
    [Fact]
    public async Task Put_SubmittedPlusRetained_IsCheckedAgainstTheCap()
    {
        await using var factory = new ApiFactory([.. ReadWrite,
            PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        var archivedOne = await SeedContactAsync(factory, "Archived One");
        var fresh = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            fresh.Add(await SeedContactAsync(factory, $"Fresh {i}"));
        }

        using var client = factory.CreateClient();
        await SetLinkCapAsync(client, 3);

        var id = await CreateAsync(client, beneficiaries: [archivedOne]);
        await ArchiveContactAsync(factory, archivedOne);

        // Three submitted plus one retained = four rows, past a cap of three.
        var put = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateInsurancePolicy
        {
            Name = "Home",
            BeneficiaryIds = [.. fresh, archivedOne],
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal(1, await BeneficiaryRowCountAsync(factory, id));
    }

    // ── The contactIds filter, and search ──────────────────────────────────────

    [Fact]
    public async Task List_ContactIdsFilter_MatchesAnyOfTheThreeContactCollections_ButSearchDoesNot()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var insurer = await SeedContactAsync(factory, "Acme Insurance");
        var beneficiary = await SeedContactAsync(factory, "Findable Beneficiary");
        using var client = factory.CreateClient();

        var withBeneficiary = await CreateAsync(client, insurers: [insurer], beneficiaries: [beneficiary]);
        await CreateAsync(client, insurers: [insurer]);

        var filtered = await client.GetFromJsonAsync<PagedResult<InsurancePolicyListItem>>(
            $"{Path}?contactIds={beneficiary}");
        Assert.Equal(withBeneficiary, Assert.Single(filtered!.Items).InsurancePolicyId);

        // Free-text search keeps today's semantics: policy name + INSURER name only. Searching a
        // beneficiary's name would make the contacts surface queryable through a second door.
        var searched = await client.GetFromJsonAsync<PagedResult<InsurancePolicyListItem>>(
            $"{Path}?search=Findable");
        Assert.Empty(searched!.Items);

        // The insurer's name still matches, as it always has.
        var byInsurer = await client.GetFromJsonAsync<PagedResult<InsurancePolicyListItem>>(
            $"{Path}?search=Acme");
        Assert.Equal(2, byInsurer!.Items.Count);
    }

    // ── Mass assignment ────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_WithANestedInsurerObject_LinksFromTheIdsAndLeavesTheContactAlone()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var insurer = await SeedContactAsync(factory, "Acme Insurance");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new
        {
            name = "Home",
            insurerIds = new[] { insurer },
            // Not a bindable member of the write DTO at any depth: the four collections are lists of
            // scalar ids, so a policy write can never create or rename a linked record.
            insurers = new[] { new { contactId = insurer, name = "Renamed by mass assignment" } },
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var contact = await context.Contacts.Include(c => c.PersonDetails)
            .AsNoTracking().SingleAsync(c => c.ContactId == insurer);
        Assert.Equal("Acme", contact.PersonDetails!.FirstName);
        Assert.Equal("Insurance", contact.PersonDetails.LastName);
    }

    // ── Read minimisation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Get_LinkProjections_CarryNameAndTypeOnly()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var beneficiary = await SeedContactAsync(factory, "Sam Rivera");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, beneficiaries: [beneficiary]);

        var json = await (await client.GetAsync($"{Path}/{id}")).Content.ReadAsStringAsync();

        // Asserted against the SERIALIZED response, not the DTO type: the minimisation lives in the
        // mapping code, and the query behind it still materialises the whole contact.
        Assert.Contains("Sam Rivera", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ORG-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("normalizedName", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── Blocked contact delete + the detach path ───────────────────────────────

    [Fact]
    public async Task DeleteContact_NamedAsBeneficiary_Returns409NamingTheKindsAndPolicies()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.ContactsDelete]);
        var beneficiary = await SeedContactAsync(factory, "Sam Rivera");
        using var client = factory.CreateClient();

        await CreateAsync(client, beneficiaries: [beneficiary], name: "Term life");

        var delete = await client.DeleteAsync($"/api/contacts/{beneficiary}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        var problem = JsonSerializer.Deserialize<JsonElement>(await delete.Content.ReadAsStringAsync(), Web);
        var blockers = problem.GetProperty("insuranceLinks").Deserialize<ContactInsuranceLinkBlockers>(Web);

        Assert.Equal(InsuranceLinkKind.Beneficiary, Assert.Single(blockers!.Kinds).Kind);
        Assert.Equal(1, blockers.Kinds[0].Count);
        Assert.Equal(1, blockers.TotalLinks);
        // This caller holds insurance.read, so the blocking policy is named.
        Assert.Equal("Term life", Assert.Single(blockers.Policies).Name);
    }

    /// <summary>
    /// The claim conditional, exercised through HTTP because it lives in the controller and cannot be
    /// reached through the service at all.
    /// </summary>
    [Fact]
    public async Task DeleteContact_WithoutInsuranceRead_GetsKindsAndCountsButNoPolicyIdentifiers()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.ContactsDelete]);
        var beneficiary = await SeedContactAsync(factory, "Sam Rivera");
        using (var writer = factory.CreateClient())
        {
            await CreateAsync(writer, beneficiaries: [beneficiary], name: "Term life");
        }

        // A second client whose principal holds contacts.delete and nothing insurance-side.
        await using var deleterFactory = new ApiFactory([PermissionClaims.ContactsDelete], factory);
        using var deleter = deleterFactory.CreateClient();

        var delete = await deleter.DeleteAsync($"/api/contacts/{beneficiary}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        var body = await delete.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(body, Web);
        var blockers = problem.GetProperty("insuranceLinks").Deserialize<ContactInsuranceLinkBlockers>(Web);

        Assert.Equal(InsuranceLinkKind.Beneficiary, Assert.Single(blockers!.Kinds).Kind);
        Assert.Equal(1, blockers.PolicyCount);
        Assert.Empty(blockers.Policies);
        Assert.DoesNotContain("Term life", body, StringComparison.Ordinal);
    }

    // The detach path's SUCCESS case is not reachable from this tier and lives in
    // Odyssey.IntegrationTests (issue #27 §16 #17). Two reasons, both structural: the transaction it
    // turns on is a no-op under EF InMemory, so the atomicity being asserted would not be under test;
    // and ContactReferenceGuard's six ExecuteUpdate/ExecuteDelete statements live in
    // EntityFrameworkCore.Relational and throw on InMemory, so no full contact delete runs here at
    // all. What this tier CAN cover is everything decided before the service is reached — the
    // claim-conditional 409 above and the composed gate below, both of which live in the controller.

    /// <summary>The composed gate: <c>contacts.delete</c> alone gets a 403, never a silent downgrade
    /// to the refused delete.</summary>
    [Fact]
    public async Task DeleteContact_WithDetach_WithoutInsuranceUpdate_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.ContactsDelete]);
        var contact = await SeedContactAsync(factory, "Sam Rivera");
        using (var writer = factory.CreateClient())
        {
            await CreateAsync(writer, beneficiaries: [contact]);
        }

        await using var deleterFactory = new ApiFactory([PermissionClaims.ContactsDelete], factory);
        using var deleter = deleterFactory.CreateClient();

        var delete = await deleter.DeleteAsync($"/api/contacts/{contact}?detachInsuranceLinks=true");

        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == contact));
        Assert.True(await context.InsurancePolicyBeneficiaries.AnyAsync(l => l.ContactId == contact));
    }

    // ── Deletes and the InMemory cascade ───────────────────────────────────────

    /// <summary>
    /// The account-side cascade under EF InMemory, which enforces no foreign keys — so this is the
    /// application-code half of a behaviour MariaDB provides with an FK. Real MariaDB is covered in
    /// Odyssey.IntegrationTests; the two use different mechanisms, so both are asserted.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_RemovesItsInsuredAccountLinks_AndLeavesThePoliciesStanding()
    {
        await using var factory = new ApiFactory([.. ReadWrite,
            PermissionClaims.AccountsRead, PermissionClaims.AccountsDelete]);
        var account = await SeedAccountAsync(factory, "House");
        using var client = factory.CreateClient();

        var one = await CreateAsync(client, accounts: [account]);
        var two = await CreateAsync(client, accounts: [account]);

        var delete = await client.DeleteAsync($"/api/accounts/{account}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.InsurancePolicyInsuredAccounts.Where(l => l.AccountId == account).ToListAsync());
        Assert.Equal(2, await context.InsurancePolicies.CountAsync(p => p.InsurancePolicyId == one || p.InsurancePolicyId == two));
    }

    [Fact]
    public async Task DeletePolicy_RemovesItsLinkRows()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contact = await SeedContactAsync(factory, "Acme");
        var account = await SeedAccountAsync(factory, "House");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurers: [contact], accounts: [account],
            insuredContacts: [contact], beneficiaries: [contact]);

        var delete = await client.DeleteAsync($"{Path}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.InsurancePolicyInsurers.Where(l => l.InsurancePolicyId == id).ToListAsync());
        Assert.Empty(await context.InsurancePolicyInsuredAccounts.Where(l => l.InsurancePolicyId == id).ToListAsync());
        Assert.Empty(await context.InsurancePolicyInsuredContacts.Where(l => l.InsurancePolicyId == id).ToListAsync());
        Assert.Empty(await context.InsurancePolicyBeneficiaries.Where(l => l.InsurancePolicyId == id).ToListAsync());
    }

    // ── Beneficiary attribution ────────────────────────────────────────────────

    /// <summary>
    /// The one link that records who created it. A later save by a DIFFERENT user that leaves the
    /// designation in place must not rewrite the author — re-saving a policy is not re-naming a
    /// beneficiary.
    /// </summary>
    [Fact]
    public async Task Beneficiary_RecordsItsAuthor_AndALaterSaveByAnotherUserDoesNotRewriteIt()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var beneficiary = await SeedContactAsync(factory, "Sam Rivera");
        Guid policyId;
        using (var client = factory.CreateClient())
        {
            policyId = await CreateAsync(client, beneficiaries: [beneficiary]);
        }

        Guid linkId;
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var link = await context.InsurancePolicyBeneficiaries.AsNoTracking()
                .SingleAsync(l => l.InsurancePolicyId == policyId);
            Assert.Equal(ActorUserId, link.CreatedByUserId);
            linkId = link.Id;
        }

        await using var otherFactory = new ApiFactory(ReadWrite, factory, actorUserId: "someone-else");
        using (var other = otherFactory.CreateClient())
        {
            var put = await other.PutAsJsonAsync($"{Path}/{policyId}", new UpdateInsurancePolicy
            {
                Name = "Renamed by someone else",
                BeneficiaryIds = [beneficiary],
            });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var link = await context.InsurancePolicyBeneficiaries.AsNoTracking().SingleAsync(l => l.Id == linkId);
            Assert.Equal(ActorUserId, link.CreatedByUserId);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<Guid> CreateAsync(
        HttpClient client,
        IReadOnlyCollection<Guid>? insurers = null,
        IReadOnlyCollection<Guid>? accounts = null,
        IReadOnlyCollection<Guid>? insuredContacts = null,
        IReadOnlyCollection<Guid>? beneficiaries = null,
        string name = "Home cover")
    {
        var post = await client.PostAsJsonAsync(Path, new NewInsurancePolicy
        {
            Name = name,
            InsurerIds = insurers?.ToList(),
            InsuredAccountIds = accounts?.ToList(),
            InsuredContactIds = insuredContacts?.ToList(),
            BeneficiaryIds = beneficiaries?.ToList(),
        });
        post.EnsureSuccessStatusCode();
        var created = await post.Content.ReadFromJsonAsync<ExistingInsurancePolicy>();
        return created!.InsurancePolicyId;
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

        // A Person, because the people-shaped collections are what this feature adds — and because a
        // person's type is what the read path returns alongside a withheld name.
        var id = Guid.NewGuid();
        var parts = name.Split(' ', 2);
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = name.ToUpperInvariant(),
            Type = ContactType.Person,
            Notes = "secret contact notes",
            Archived = archived ? DateTime.UtcNow : null,
            PersonDetails = new() { ContactId = id, FirstName = parts[0], LastName = parts.Length > 1 ? parts[1] : "X" },
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

    private static async Task<Guid> LinkIdAsync(WebApplicationFactory<Program> factory, Guid policyId, Guid contactId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return (await context.InsurancePolicyInsurers.AsNoTracking()
            .SingleAsync(l => l.InsurancePolicyId == policyId && l.ContactId == contactId)).Id;
    }

    private static async Task<int> BeneficiaryRowCountAsync(WebApplicationFactory<Program> factory, Guid policyId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.InsurancePolicyBeneficiaries.CountAsync(l => l.InsurancePolicyId == policyId);
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(IReadOnlyCollection<string>? permissions)
            : base(permissions, ActorUserId)
        {
        }

        /// <summary>
        /// A second principal over the SAME in-memory store, so a test can exercise one caller's write
        /// against another caller's claims — which is the only way to reach the claim-conditional 409
        /// and the composed detach gate.
        /// </summary>
        public ApiFactory(
            IReadOnlyCollection<string>? permissions,
            OdysseyApiFactory sharing,
            string actorUserId = ActorUserId)
            : base(permissions, actorUserId, sharingStoreWith: sharing)
        {
        }
    }
}
