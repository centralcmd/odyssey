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
