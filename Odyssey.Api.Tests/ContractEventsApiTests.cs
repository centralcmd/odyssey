using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextContractType = Odyssey.Context.ContractType;
using ContractEventType = Odyssey.Dtos.Finance.ContractEventType;
using ContractEventSource = Odyssey.Dtos.Finance.ContractEventSource;

namespace Odyssey.Api.Tests;

/// <summary>
/// The four contract-scoped event endpoints (issue #138), over real HTTP against the shared
/// <c>OdysseyApiFactory</c> fixture.
/// </summary>
/// <remarks>
/// Three things this tier reaches that the unit tier cannot: the claim gates (the list on
/// <c>contracts.read</c>, all three writes on <c>contracts.update</c> — and notably <b>not</b>
/// <c>contracts.delete</c> for the event delete), the attribution label the API edge resolves, and
/// what model binding actually does with a body carrying fields the DTO does not declare.
/// </remarks>
public class ContractEventsApiTests
{
    private const string ActorUserId = "contract-events-actor-id";
    private const string Path = "/api/contracts";

    private static readonly DateTime FixedNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ReadOnly = [PermissionClaims.ContractsRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate, PermissionClaims.ContractsUpdate,
    ];

    /// <summary>
    /// Every claim in the vocabulary EXCEPT the two the event endpoints gate on. It includes
    /// <c>contracts.create</c> and <c>contracts.delete</c>, so a principal that may create and destroy
    /// whole contracts is shown to have no reach into one's log.
    /// </summary>
    private static readonly string[] EverythingButContractReadAndUpdate =
        [.. RolePermissions.AllClaims.Where(c =>
            c is not (PermissionClaims.ContractsRead or PermissionClaims.ContractsUpdate))];

    // ── Create (AC 1) ────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_ValidBody_Returns201WithTheStampedFieldsAndTheListLocation()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        await factory.SeedActorUserAsync(displayName: "Jane Doe");
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Events(contractId), New());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains(
            $"{Path}/{contractId}/events", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

        var created = await response.Content.ReadFromJsonAsync<ExistingContractEvent>();
        Assert.NotEqual(Guid.Empty, created!.ContractEventId);
        Assert.Equal(contractId, created.ContractId);
        Assert.Equal(FixedNow, created.CreatedAtUtc);
        Assert.Equal("Jane Doe", created.CreatedBy);
    }

    [Fact]
    public async Task Post_ToAMissingContract_Returns404()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync(Events(Guid.NewGuid()), New())).StatusCode);
    }

    /// <summary>
    /// AC 9 — the two provenance fields are server-stamped. A body supplying them (under either the
    /// wire name or the column name) is ignored rather than honoured.
    /// </summary>
    [Fact]
    public async Task Post_BodySupplyingTheAttributionFields_DoesNotOverrideTheServerStampedValues()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        await factory.SeedActorUserAsync(displayName: "Jane Doe");
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Events(contractId), new
        {
            type = (int)ContractEventType.EmailSent,
            title = "Emailed landlord",
            occurredAt = new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Utc),
            createdBy = "Someone Else",
            createdByUserId = "forged-user-id",
            createdAtUtc = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<ExistingContractEvent>())!;
        Assert.Equal("Jane Doe", created.CreatedBy);
        Assert.Equal(FixedNow, created.CreatedAtUtc);

        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<OdysseyContext>()
            .ContractEvents.AsNoTracking().SingleAsync();
        Assert.Equal(ActorUserId, stored.CreatedByUserId);
    }

    /// <summary>
    /// AC 10 — a body carrying properties from an earlier draft of the spec (the four link columns v5
    /// removed) is ignored, and creates no row in any other table. The request DTO declares none of
    /// them, so there is nothing for them to bind to: the mass-assignment surface is absent rather
    /// than merely guarded (§5.2).
    /// </summary>
    [Fact]
    public async Task Post_BodyCarryingUnrecognisedProperties_IgnoresThemAndTouchesNoOtherTable()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var contactId = await SeedContactAsync(factory);

        var response = await client.PostAsJsonAsync(Events(contractId), new
        {
            title = "Emailed landlord",
            occurredAt = new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Utc),
            primaryContactId = contactId,
            secondaryContactId = contactId,
            primaryAccountId = Guid.NewGuid(),
            secondaryAccountId = Guid.NewGuid(),
            contractEventId = Guid.NewGuid(),
            contractId = Guid.NewGuid(),
        });

        response.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var stored = await context.ContractEvents.AsNoTracking().SingleAsync();

        // The route names the owner, and the id is generated — neither is over-postable.
        Assert.Equal(contractId, stored.ContractId);
        Assert.Empty(await context.ContractParties.AsNoTracking().ToListAsync());
        Assert.Empty(await context.ContractFiles.AsNoTracking().ToListAsync());
        // The contact is untouched: this feature references none (Non-Goal 5).
        Assert.True(await context.Contacts.AsNoTracking().AnyAsync(c => c.ContactId == contactId));
    }

    /// <summary>AC 8 — the future bound, keyed to the field so a form can mark the control.</summary>
    [Fact]
    public async Task Post_WithAFutureOccurredAt_Is400KeyedToOccurredAt()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(
            Events(contractId), New(occurredAt: FixedNow.AddHours(1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiProblemBody>();
        Assert.True(problem!.Errors!.ContainsKey("occurredAt"));
    }

    [Fact]
    public async Task Post_WithAnOccurredAtInsideTheTolerance_Succeeds()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(
            Events(contractId), New(occurredAt: FixedNow.AddSeconds(30)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>Data-annotation failures are rejected by model validation before the service runs.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Post_WithNoTitle_Is400(string? title)
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Events(contractId), new
        {
            title,
            occurredAt = new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithAnOverLongTitle_Is400()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Events(contractId), New(title: new string('x', 257)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Read (AC 2, 4) ───────────────────────────────────────────────────────

    /// <summary>
    /// AC 4 — all three free-text fields reach every caller holding <c>contracts.read</c>, notes
    /// included. The timeline not rendering notes is a presentation rule and never an access one, so a
    /// projection that dropped it here would be a defect in three directions at once (§4.1).
    /// </summary>
    [Fact]
    public async Task Get_ReturnsAllThreeFreeTextFieldsToAPlainContractsReader()
    {
        await using var writerFactory = await NewFactoryAsync(ReadWrite);
        using var writer = writerFactory.CreateClient();
        var contractId = await CreateContractAsync(writer);
        (await writer.PostAsJsonAsync(Events(contractId), New())).EnsureSuccessStatusCode();

        // A SECOND principal, holding contracts.read alone, over the first one's data.
        await using var readerFactory = new ApiFactory(ReadOnly, sharingStoreWith: writerFactory);
        using var reader = readerFactory.CreateClient();

        var items = await reader.GetPagedItemsAsync<ExistingContractEvent>(Events(contractId));

        var only = Assert.Single(items);
        Assert.Equal("Emailed landlord about the rent increase", only.Title);
        Assert.Equal("Asked for the CPI basis in writing.", only.Description);
        Assert.Equal("Chase on the 21st if no reply.", only.Notes);
    }

    /// <summary>
    /// §7.3 — the response carries a display LABEL and never the raw user id. This is a deliberate
    /// improvement on <c>ExistingTransactionFile.AttachedByUserId</c> and friends, and the DTO having
    /// no id-shaped property at all is what keeps it that way.
    /// </summary>
    [Fact]
    public async Task Get_ExposesADisplayLabelAndNeverTheRawUserId()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        await factory.SeedActorUserAsync(displayName: "Jane Doe");
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        (await client.PostAsJsonAsync(Events(contractId), New())).EnsureSuccessStatusCode();

        var raw = await client.GetStringAsync(Events(contractId));

        Assert.Contains("Jane Doe", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ActorUserId, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("createdByUserId", raw, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A reader without <c>users.read</c> whose author has no profile name gets "Unknown user", never
    /// the author's email — the shared resolver's data-minimisation rule, inherited unchanged.
    /// </summary>
    [Fact]
    public async Task Get_AuthorWithNoProfileNameAndNoUsersRead_IsUnknownUserNotAnEmail()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var userName = await factory.SeedActorUserAsync();
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        (await client.PostAsJsonAsync(Events(contractId), New())).EnsureSuccessStatusCode();

        var items = await client.GetPagedItemsAsync<ExistingContractEvent>(Events(contractId));

        Assert.Equal("Unknown user", Assert.Single(items).CreatedBy);
        Assert.NotEqual(userName, Assert.Single(items).CreatedBy);
    }

    /// <summary>
    /// AC 13's read half — an event whose author row is gone reads back as "Unknown user" rather than
    /// as a blank or a leaked id. (The column actually nulling on user delete is a foreign-key
    /// behaviour and lives in <c>Odyssey.IntegrationTests</c>, where a real engine enforces it.)
    /// </summary>
    [Fact]
    public async Task Get_AnEventWhoseAuthorIsGone_ReadsBackAsUnknownUser()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        (await client.PostAsJsonAsync(Events(contractId), New())).EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var stored = await context.ContractEvents.SingleAsync();
            stored.CreatedByUserId = null;
            await context.SaveChangesAsync();
        }

        var items = await client.GetPagedItemsAsync<ExistingContractEvent>(Events(contractId));

        Assert.Equal("Unknown user", Assert.Single(items).CreatedBy);
    }

    [Fact]
    public async Task Get_OnAMissingContract_Returns404()
    {
        await using var factory = await NewFactoryAsync(ReadOnly);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Events(Guid.NewGuid()))).StatusCode);
    }

    /// <summary>AC 11's list half — an event is only ever visible through the contract it is on.</summary>
    [Fact]
    public async Task Get_DoesNotReturnAnotherContractsEvents()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var otherContractId = await CreateContractAsync(client, name: "Other agreement");
        (await client.PostAsJsonAsync(Events(contractId), New())).EnsureSuccessStatusCode();

        Assert.Empty(await client.GetPagedItemsAsync<ExistingContractEvent>(Events(otherContractId)));
    }

    /// <summary>
    /// The list-query binding model follows the DTO annotation rule, so an unbindable sort key,
    /// direction or enum filter — and an out-of-range window — is a 400 from model validation rather
    /// than a silently coerced default.
    /// </summary>
    [Theory]
    [InlineData("sortBy=notAKey")]
    [InlineData("sortDir=sideways")]
    [InlineData("types=NotAType")]
    [InlineData("offset=-1")]
    [InlineData("limit=100000")]
    [InlineData("from=notADate")]
    public async Task Get_WithAnUnbindableOrOutOfRangeQuery_Is400(string query)
    {
        await using var factory = await NewFactoryAsync(ReadOnly);
        using var client = factory.CreateClient();
        var contractId = await SeedContractDirectlyAsync(factory);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync($"{Events(contractId)}?{query}")).StatusCode);
    }

    /// <summary>
    /// The events are NOT inlined on the single-contract read — an event log grows without bound, so
    /// inlining it would make every contract load pay for the whole history (§5).
    /// </summary>
    [Fact]
    public async Task GetOneContract_DoesNotInlineItsEvents()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        (await client.PostAsJsonAsync(Events(contractId), New())).EnsureSuccessStatusCode();

        var raw = await client.GetStringAsync($"{Path}/{contractId}");

        Assert.DoesNotContain("Emailed landlord", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"events\"", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ── Update (AC 3, 5, 11) ─────────────────────────────────────────────────

    [Fact]
    public async Task Put_ReplacesEveryFieldAndClearsTheOmittedOnes()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var created = await PostEventAsync(client, contractId);

        var response = await client.PutAsJsonAsync($"{Events(contractId)}/{created.ContractEventId}", new
        {
            title = "Just the title now",
            occurredAt = new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<ExistingContractEvent>())!;
        Assert.Equal("Just the title now", updated.Title);
        Assert.Null(updated.Description);
        Assert.Null(updated.Notes);
        Assert.Equal(ContractEventType.Other, updated.Type);
    }

    [Fact]
    public async Task Put_DoesNotChangeTheAttributionOrTheCreationTimestamp()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        await factory.SeedActorUserAsync(displayName: "Jane Doe");
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var created = await PostEventAsync(client, contractId);

        var response = await client.PutAsJsonAsync(
            $"{Events(contractId)}/{created.ContractEventId}", Update(title: "Edited"));

        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<ExistingContractEvent>())!;
        Assert.Equal(created.CreatedBy, updated.CreatedBy);
        Assert.Equal(created.CreatedAtUtc, updated.CreatedAtUtc);
    }

    [Fact]
    public async Task Put_AnEventOnAnotherContract_Is404()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var otherContractId = await CreateContractAsync(client, name: "Other agreement");
        var created = await PostEventAsync(client, contractId);

        var response = await client.PutAsJsonAsync(
            $"{Events(otherContractId)}/{created.ContractEventId}", Update(title: "Hijacked"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_AMissingEvent_Is404()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PutAsJsonAsync($"{Events(contractId)}/{Guid.NewGuid()}", Update());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Delete (AC 6, 11) ────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesTheEvent()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var created = await PostEventAsync(client, contractId);

        var response = await client.DeleteAsync($"{Events(contractId)}/{created.ContractEventId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await client.GetPagedItemsAsync<ExistingContractEvent>(Events(contractId)));
    }

    [Fact]
    public async Task Delete_AnEventOnAnotherContract_Is404AndKeepsIt()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var otherContractId = await CreateContractAsync(client, name: "Other agreement");
        var created = await PostEventAsync(client, contractId);

        var response = await client.DeleteAsync($"{Events(otherContractId)}/{created.ContractEventId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await client.GetPagedItemsAsync<ExistingContractEvent>(Events(contractId)));
    }

    // ── The delete cascade, InMemory half (AC 12) ────────────────────────────

    /// <summary>
    /// AC 12 on the tier the database does NOT do the work. The EF InMemory provider enforces no
    /// cascade at all, so what removes the rows here is <c>ContractService.Delete</c>'s
    /// <c>.Include(c =&gt; c.Events)</c> — and this is the assertion that fails if that line is lost.
    /// </summary>
    /// <remarks>
    /// Its absence was a real hole, not a hypothetical one: with the <c>Include</c> commented out, all
    /// 2204 tests in this project passed. The sibling guard for <c>Terms</c>
    /// (<c>ContractTermsApiTests.DeleteContract_RemovesItsTermsAndLeavesAccountTermsUntouched</c>)
    /// exists for exactly this reason and this is its counterpart. The database-enforced half lives in
    /// <c>Odyssey.IntegrationTests</c>, where a real engine does the cascade.
    /// </remarks>
    [Fact]
    public async Task DeleteContract_RemovesItsEventsAndLeavesAnotherContractsUntouched()
    {
        await using var factory = await NewFactoryAsync([.. ReadWrite, PermissionClaims.ContractsDelete]);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var survivingContractId = await CreateContractAsync(client, name: "Survivor");

        await PostEventAsync(client, contractId);
        (await client.PostAsJsonAsync(Events(contractId), New(title: "A second entry"))).EnsureSuccessStatusCode();
        await PostEventAsync(client, survivingContractId);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{contractId}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.ContractEvents.Where(e => e.ContractId == contractId).ToListAsync());
        Assert.Single(await context.ContractEvents.Where(e => e.ContractId == survivingContractId).ToListAsync());
    }

    // ── Authorization (AC 7) ─────────────────────────────────────────────────

    [Fact]
    public async Task Get_WithoutContractsRead_Is403()
    {
        await using var factory = await NewFactoryAsync(EverythingButContractReadAndUpdate);
        using var client = factory.CreateClient();
        var contractId = await SeedContractDirectlyAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Events(contractId))).StatusCode);
    }

    /// <summary>
    /// Every write is gated on <c>contracts.update</c> alone. The principal here holds
    /// <c>contracts.read</c>, <c>contracts.create</c> and <c>contracts.delete</c> — so the delete
    /// assertion in particular shows that <c>contracts.delete</c> is neither sufficient nor required:
    /// removing an entry is editing the contract's log, not deleting a contract (§5.4).
    /// </summary>
    [Fact]
    public async Task EveryWrite_WithoutContractsUpdate_Is403()
    {
        string[] readAndDeleteButNotUpdate =
            [PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate, PermissionClaims.ContractsDelete];

        await using var factory = await NewFactoryAsync(readAndDeleteButNotUpdate);
        using var client = factory.CreateClient();
        var contractId = await SeedContractDirectlyAsync(factory);
        var eventId = Guid.NewGuid();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(Events(contractId), New())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"{Events(contractId)}/{eventId}", Update())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"{Events(contractId)}/{eventId}")).StatusCode);
    }

    /// <summary>
    /// The gate fires before the resource is resolved: a caller without the claim gets 403 on a
    /// contract that does not exist, never a 404 that would confirm or deny it.
    /// </summary>
    [Fact]
    public async Task Get_WithoutTheClaimOnAMissingContract_Is403Not404()
    {
        await using var factory = await NewFactoryAsync(EverythingButContractReadAndUpdate);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Events(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task EveryEndpoint_Anonymous_Is401()
    {
        await using var factory = await NewFactoryAsync(permissions: null);
        using var client = factory.CreateClient();
        var contractId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Events(contractId))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Events(contractId), New())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PutAsJsonAsync($"{Events(contractId)}/{eventId}", Update())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized, (await client.DeleteAsync($"{Events(contractId)}/{eventId}")).StatusCode);
    }

    // ── AC 14 / Non-Goal 5: the event links to nothing ───────────────────────

    /// <summary>
    /// AC 14 and Non-Goal 5, pinned structurally. Drafts v1–v4 gave an event four <c>RESTRICT</c> link
    /// columns, and v5 removed them along with every blocker payload, detach valve and delete-path
    /// change they dragged with them — which is why <c>DELETE /api/contacts/{id}</c> and
    /// <c>DELETE /api/accounts/{id}</c> are untouched by this feature.
    ///
    /// <para>
    /// The spec says the columns must not be re-added opportunistically during implementation, so this
    /// asserts the entity's shape rather than a behaviour: the only <c>Guid</c> an event carries is its
    /// own id and its owner's. A link column added later fails here, at the point the decision is cheap,
    /// instead of surfacing as a foreign key nobody decided the delete behaviour for.
    /// </para>
    ///
    /// <para>
    /// The behavioural half — a contact delete actually succeeding with an event that merely names it
    /// in prose — lives in <c>Odyssey.IntegrationTests</c>: the contact-delete cascade is relational-only
    /// (<c>ContactReferenceGuard</c> uses <c>ExecuteDeleteAsync</c>), so no full contact delete runs on
    /// this tier at all.
    /// </para>
    /// </summary>
    [Fact]
    public void AContractEvent_CarriesNoLinkToAnyOtherEntity()
    {
        var guidProperties = typeof(ContractEvent)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?))
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal([nameof(ContractEvent.ContractEventId), nameof(ContractEvent.ContractId)], guidProperties);

        // The navigation set is the same claim from the other side: one parent, nothing else. Entity
        // types only — the Type column's enum lives in the same namespace and is not a navigation.
        var navigations = typeof(ContractEvent)
            .GetProperties()
            .Where(p => !p.PropertyType.IsValueType
                && p.PropertyType.Namespace == typeof(ContractEvent).Namespace)
            .Select(p => p.Name)
            .ToList();
        Assert.Equal([nameof(ContractEvent.Contract)], navigations);
    }


    // ── Automation: the source field and the recorded rows (issue #154) ──────

    /// <summary>
    /// AC 21 — a body carrying <c>"source": 1</c> comes back <c>User</c>. The field is not bound and
    /// cannot be over-posted, because <see cref="NewContractEvent"/> does not declare it at all —
    /// forging a <c>System</c> row is a compile-time impossibility rather than a validation rule
    /// (#154 §7.4). Asserted over HTTP because model binding is where an over-post would actually
    /// happen.
    /// </summary>
    [Fact]
    public async Task Post_WithASourceInTheBody_IgnoresIt_AndReturnsUser()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Events(contractId), new
        {
            type = (int)ContractEventType.EmailSent,
            title = "Hand-written",
            occurredAt = new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Utc),
            source = 1,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ExistingContractEvent>();
        Assert.Equal(ContractEventSource.User, created!.Source);
    }

    /// <summary>
    /// AC 22 — editing a system event replaces every editable field and leaves <c>source</c> alone.
    /// <c>Source</c> records how the row came to exist, which an edit does not change; flipping it to
    /// <c>User</c> would make the log's one provenance signal depend on whether anyone had since fixed
    /// a typo, and would erase the fact that the transition really did occur.
    /// </summary>
    [Fact]
    public async Task Put_OnASystemEvent_ReplacesTheBody_AndKeepsSourceSystem()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var recorded = await RecordASystemEventAsync(client, contractId);

        var response = await client.PutAsJsonAsync(
            $"{Events(contractId)}/{recorded.ContractEventId}", Update("Shortened by hand"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<ExistingContractEvent>();
        Assert.Equal(ContractEventSource.System, updated!.Source);
        Assert.Equal("Shortened by hand", updated.Title);
        Assert.Equal(ContractEventType.Amended, updated.Type);
        // Full replacement: an omitted description and note CLEAR, on a system row exactly as on any
        // other. Nothing regenerates the server's original prose.
        Assert.Null(updated.Description);
        Assert.Null(updated.Notes);
    }

    /// <summary>AC 23 — a system event is deletable like any other. It is not an audit record.</summary>
    [Fact]
    public async Task Delete_OnASystemEvent_Returns204AndRemovesIt()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var recorded = await RecordASystemEventAsync(client, contractId);

        var response = await client.DeleteAsync($"{Events(contractId)}/{recorded.ContractEventId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var remaining = await client.GetFromJsonAsync<PagedResult<ExistingContractEvent>>(Events(contractId));
        Assert.DoesNotContain(remaining!.Items, e => e.ContractEventId == recorded.ContractEventId);
    }

    /// <summary>AC 24 — the <c>source</c> filter, both values, omitted, and an unbindable one.</summary>
    [Fact]
    public async Task Get_WithASourceFilter_ReturnsOnlyThatKind_AndRejectsNonsense()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        await RecordASystemEventAsync(client, contractId);
        await PostEventAsync(client, contractId);

        var both = await client.GetFromJsonAsync<PagedResult<ExistingContractEvent>>(Events(contractId));
        Assert.Equal(2, both!.TotalCount);

        var system = await client.GetFromJsonAsync<PagedResult<ExistingContractEvent>>(
            $"{Events(contractId)}?source=System");
        Assert.All(system!.Items, e => Assert.Equal(ContractEventSource.System, e.Source));
        Assert.Single(system.Items);

        var user = await client.GetFromJsonAsync<PagedResult<ExistingContractEvent>>(
            $"{Events(contractId)}?source=User");
        Assert.All(user!.Items, e => Assert.Equal(ContractEventSource.User, e.Source));
        Assert.Single(user.Items);

        // An unbindable value is a 400 from [ApiController] model validation, like every other enum
        // filter on this surface — never a silently-ignored filter returning everything.
        var rejected = await client.GetAsync($"{Events(contractId)}?source=Nonsense");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    /// <summary>
    /// AC 17 — a <c>TermChanged</c> event recorded by a term write is attributed to the CALLER, not
    /// to a service identity. Without the §5.6 plumbing every one would read "Unknown user", which is
    /// indistinguishable from a deleted author — so the defect would look like correct behaviour.
    /// Asserted for all three verbs, over HTTP, because the claim is about the controller's plumbing.
    /// </summary>
    [Fact]
    public async Task TermWrites_RecordEventsAttributedToTheCallingUser()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        await factory.SeedActorUserAsync(displayName: "Jane Doe");
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var termId = await CreateTermAsync(client, contractId, 14500m);
        await AssertLatestPriceChangeAttributedAsync(client, contractId, "Jane Doe");

        (await client.PutAsJsonAsync(
            $"{Path}/{contractId}/terms/{termId}", NewTermBody(15000m))).EnsureSuccessStatusCode();
        await AssertLatestPriceChangeAttributedAsync(client, contractId, "Jane Doe");

        (await client.DeleteAsync($"{Path}/{contractId}/terms/{termId}")).EnsureSuccessStatusCode();
        await AssertLatestPriceChangeAttributedAsync(client, contractId, "Jane Doe");
    }

    /// <summary>
    /// AC 27 — the gating claims are unchanged. A caller holding every claim in the vocabulary
    /// <em>except</em> <c>contracts.read</c> and <c>contracts.update</c> cannot read the log that now
    /// contains recorded rows, and cannot write to it.
    /// </summary>
    [Fact]
    public async Task SystemEvents_AreGatedByTheSameTwoClaimsAsEveryOtherRow()
    {
        await using var owner = await NewFactoryAsync(ReadWrite);
        using var ownerClient = owner.CreateClient();
        var contractId = await CreateContractAsync(ownerClient);
        var recorded = await RecordASystemEventAsync(ownerClient, contractId);

        await using var stranger = new ApiFactory(EverythingButContractReadAndUpdate, sharingStoreWith: owner);
        using var strangerClient = stranger.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await strangerClient.GetAsync(Events(contractId))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await strangerClient.PutAsJsonAsync(
                $"{Events(contractId)}/{recorded.ContractEventId}", Update())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await strangerClient.DeleteAsync($"{Events(contractId)}/{recorded.ContractEventId}")).StatusCode);
    }

    /// <summary>
    /// AC 28 — a system event whose author has since been deleted still reads back, as "Unknown user".
    /// <c>CreatedByUserId</c> is <c>SET NULL</c>, so the shared record survives its author's departure.
    /// </summary>
    [Fact]
    public async Task ASystemEventWhoseAuthorIsGone_ReadsBackAsUnknownUser()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        await factory.SeedActorUserAsync(displayName: "Jane Doe");
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var recorded = await RecordASystemEventAsync(client, contractId);
        Assert.Equal("Jane Doe", recorded.CreatedBy);

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var row = await context.ContractEvents.FirstAsync(e => e.ContractEventId == recorded.ContractEventId);
            row.CreatedByUserId = null;
            await context.SaveChangesAsync();
        }

        var reread = await client.GetFromJsonAsync<PagedResult<ExistingContractEvent>>(Events(contractId));
        var still = Assert.Single(reread!.Items, e => e.ContractEventId == recorded.ContractEventId);
        Assert.Equal("Unknown user", still.CreatedBy);
        Assert.Equal(ContractEventSource.System, still.Source);
    }

    // ── Automation helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Makes the server record one event, by doing something to the contract rather than by inserting
    /// a row: pausing it. That is what makes these tests exercise the feature rather than the fixture.
    /// </summary>
    private static async Task<ExistingContractEvent> RecordASystemEventAsync(HttpClient client, Guid contractId)
    {
        // Ready + Signed first, so the contract derives Active and EnsurePausable permits the pause.
        // Those two writes record their own transitions, which is why the pause is issued last and the
        // Paused row is the one selected below.
        (await client.PutAsJsonAsync($"{Path}/{contractId}", ContractWrite(
            ready: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            signed: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"{Path}/{contractId}", ContractWrite(
            ready: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            signed: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            isPaused: true))).EnsureSuccessStatusCode();

        // Then remove the other recorded rows, so a caller of this helper starts from exactly one.
        var page = await client.GetFromJsonAsync<PagedResult<ExistingContractEvent>>(Events(contractId));
        var paused = page!.Items.Single(e => e.Type == ContractEventType.Paused);
        foreach (var other in page.Items.Where(e => e.ContractEventId != paused.ContractEventId))
        {
            (await client.DeleteAsync($"{Events(contractId)}/{other.ContractEventId}")).EnsureSuccessStatusCode();
        }

        Assert.Equal(ContractEventSource.System, paused.Source);
        return paused;
    }

    private static UpdateContract ContractWrite(
        DateTime? ready = null, DateTime? signed = null, bool isPaused = false) => new()
    {
        Name = "Maple St lease",
        Type = Odyssey.Dtos.Finance.ContractType.Rental,
        StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IsPaused = isPaused,
        Ready = ready,
        Signed = signed,
    };

    private static NewTerm NewTermBody(decimal value) => new()
    {
        Label = "Monthly rent",
        ValueUnit = Odyssey.Dtos.Finance.TermValueUnit.Amount,
        Value = value,
        CurrencyCode = "USD",
        Interval = Odyssey.Dtos.Finance.Interval.Monthly,
        EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static async Task<Guid> CreateTermAsync(HttpClient client, Guid contractId, decimal value)
    {
        var response = await client.PostAsJsonAsync($"{Path}/{contractId}/terms", NewTermBody(value));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingTerm>())!.TermId;
    }

    private static async Task AssertLatestPriceChangeAttributedAsync(
        HttpClient client, Guid contractId, string expected)
    {
        var page = await client.GetFromJsonAsync<PagedResult<ExistingContractEvent>>(
            $"{Events(contractId)}?types={(int)ContractEventType.TermChanged}&sortBy=CreatedAtUtc&sortDir=desc");
        var latest = page!.Items[0];
        Assert.Equal(ContractEventSource.System, latest.Source);
        Assert.Equal(expected, latest.CreatedBy);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Events(Guid contractId) => $"{Path}/{contractId}/events";

    private static NewContractEvent New(
        string title = "Emailed landlord about the rent increase",
        ContractEventType type = ContractEventType.EmailSent,
        string? description = "Asked for the CPI basis in writing.",
        string? notes = "Chase on the 21st if no reply.",
        DateTime? occurredAt = null) => new()
    {
        Type = type,
        Title = title,
        Description = description,
        Notes = notes,
        OccurredAt = occurredAt ?? new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Utc),
    };

    private static UpdateContractEvent Update(string title = "Edited title") => new()
    {
        Type = ContractEventType.Amended,
        Title = title,
        OccurredAt = new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Utc),
    };

    /// <summary>
    /// The collapsed card's counts strip carries the log's size, so a section that is present in the
    /// record body is never absent from its table of contents — a count that is missing reads as a
    /// section that is empty.
    ///
    /// <para>
    /// Counted the same way <c>TermCount</c> is: one correlated subquery in the list read. Asserted
    /// over HTTP because that is where the projection actually runs — nothing on the list path loads
    /// the log's rows, so the count is the only thing that can be wrong.
    /// </para>
    /// </summary>
    [Fact]
    public async Task List_CarriesTheEventCount()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var withEvents = await CreateContractAsync(client);
        var without = await CreateContractAsync(client, name: "Harbor Point parking");

        // A contract with no log at all is a real state, and zero is the honest answer for it.
        var before = (await client.GetFromJsonAsync<PagedResult<ContractListItem>>(Path))!;
        Assert.Equal(0, before.Items.Single(c => c.ContractId == withEvents).EventCount);

        await PostEventAsync(client, withEvents);
        await PostEventAsync(client, withEvents);

        var after = (await client.GetFromJsonAsync<PagedResult<ContractListItem>>(Path))!;
        Assert.Equal(2, after.Items.Single(c => c.ContractId == withEvents).EventCount);
        // The subquery is correlated, not a table-wide count leaking across rows.
        Assert.Equal(0, after.Items.Single(c => c.ContractId == without).EventCount);
    }

    private static async Task<ExistingContractEvent> PostEventAsync(HttpClient client, Guid contractId)
    {
        var response = await client.PostAsJsonAsync(Events(contractId), New());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingContractEvent>())!;
    }

    private static async Task<Guid> CreateContractAsync(HttpClient client, string name = "Maple St lease")
    {
        var response = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = name,
            Type = Odyssey.Dtos.Finance.ContractType.Rental,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingContract>())!.ContractId;
    }

    /// <summary>A contract seeded straight into the context, for a principal that cannot create one.</summary>
    private static async Task<Guid> SeedContractDirectlyAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var contract = new Contract
        {
            Name = "Seeded lease",
            Type = ContextContractType.Rental,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = FixedNow,
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    private static async Task<Guid> SeedContactAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "THE LANDLORD",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "The landlord" },
        };
        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return contact.ContactId;
    }

    private static async Task<ApiFactory> NewFactoryAsync(IReadOnlyCollection<string>? permissions)
    {
        var factory = new ApiFactory(permissions);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
        return factory;
    }

    /// <summary>The per-field shape of a ProblemDetails body, for the field-keyed assertions.</summary>
    private sealed record ApiProblemBody
    {
        public Dictionary<string, string[]>? Errors { get; init; }
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(IReadOnlyCollection<string>? permissions, OdysseyApiFactory? sharingStoreWith = null)
            : base(
                permissions,
                ActorUserId,
                configuration: null,
                configureServices: services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedNow));
                },
                sharingStoreWith: sharingStoreWith)
        {
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
