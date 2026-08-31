using System.Net;
using System.Net.Http.Json;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos;
using BillingInterval = Odyssey.Dtos.Finance.BillingInterval;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for the subscriptions endpoints (issue #293): the authorization matrix, the
/// create→read round-trip with the data-minimised contact projection, the mass-assignment guard
/// (nested contact object + raw paused/archived timestamps ignored), search over external id,
/// the pause/archive toggles, the date rule, and the list contract.
/// </summary>
public class SubscriptionApiTests
{
    private const string Path = "/api/subscriptions";

    private static readonly string[] ReadOnly = [PermissionClaims.SubscriptionsRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.SubscriptionsRead, PermissionClaims.SubscriptionsCreate,
        PermissionClaims.SubscriptionsUpdate, PermissionClaims.SubscriptionsDelete,
    ];

    // ── Authorization matrix (criterion #12) ───────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new OdysseyApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutReadPermission_ReturnsForbidden()
    {
        // A Guest-equivalent token (no subscriptions.read) is forbidden even on GET.
        await using var factory = new OdysseyApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mutations_WithReadOnlyPermission_ReturnForbidden()
    {
        await using var factory = new OdysseyApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewSub());
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var put = await client.PutAsJsonAsync($"{Path}/{Guid.NewGuid()}", UpdateSub());
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        var delete = await client.DeleteAsync($"{Path}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ── Create → read round-trip (criteria #1, #2) ─────────────────────────────

    [Fact]
    public async Task Post_NoContact_CreatesAndIsRetrievable()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewSub());
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.NotNull(post.Headers.Location);
        var created = await post.Content.ReadFromJsonAsync<ExistingSubscription>();
        Assert.NotEqual(Guid.Empty, created!.SubscriptionId);

        var fetched = await client.GetFromJsonAsync<ExistingSubscription>($"{Path}/{created.SubscriptionId}");
        Assert.Equal("Streaming", fetched!.Name);
        Assert.Null(fetched.Contact);
    }

    [Fact]
    public async Task Post_WithContact_ReturnsMinimalProjection()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        var contactId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, contactId);

        // The minimal reference must not leak the richer contact fields gated by contacts.read.
        var json = await client.GetStringAsync($"{Path}/{id}");
        Assert.Contains("Netflix", json);
        Assert.DoesNotContain("organizationNumber", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ORG-12345", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret contact notes", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation (criteria #3, #4, #6) ───────────────────────────────────────

    [Fact]
    public async Task Post_UnknownContact_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewSub(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_ArchivedContact_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        var contactId = await SeedContactAsync(factory, archived: true);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewSub(contactId));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_UnsupportedCurrency_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var request = NewSub();
        request.CurrencyCode = "ZZZ";
        var post = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_MissingFirstBillingDate_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        // Omit firstBillingDate entirely — model validation must reject the required field.
        var body = new
        {
            name = "Streaming",
            startDate = "2026-01-01",
            amount = 9.99m,
            currencyCode = "USD",
            interval = (int)BillingInterval.Monthly,
        };
        var post = await client.PostAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_EndBeforeStart_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var request = NewSub();
        request.EndDate = request.StartDate.AddDays(-1);
        var post = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_IntervalCount_RoundTrips_And_ZeroIsRejected()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        // Quarterly: every 3 months.
        var quarterly = NewSub();
        quarterly.Name = "Quarterly";
        quarterly.Interval = BillingInterval.Monthly;
        quarterly.IntervalCount = 3;
        var post = await client.PostAsJsonAsync(Path, quarterly);
        var created = await post.Content.ReadFromJsonAsync<ExistingSubscription>();
        Assert.Equal(3, created!.IntervalCount);
        var item = Assert.Single((await client.GetFromJsonAsync<PagedResult<SubscriptionListItem>>($"{Path}?search=Quarterly"))!.Items);
        Assert.Equal(3, item.IntervalCount);

        // Zero is rejected by [Range(1, 1000)] model validation.
        var invalid = NewSub();
        invalid.IntervalCount = 0;
        var rejected = await client.PostAsJsonAsync(Path, invalid);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Post_DatesRoundTripWithoutTimeComponent()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        var json = await client.GetStringAsync($"{Path}/{id}");
        Assert.Contains("\"firstBillingDate\":\"2026-01-15\"", json);
        Assert.Contains("\"startDate\":\"2026-01-01\"", json);
    }

    // ── Search over external id (criterion #7) ─────────────────────────────────

    [Fact]
    public async Task List_SearchByExternalId_FindsRow_And_ValueRoundTrips()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var withExternal = NewSub();
        withExternal.Name = "Membership";
        withExternal.ExternalId = "MBR-12345";
        (await client.PostAsJsonAsync(Path, withExternal)).EnsureSuccessStatusCode();
        await CreateAsync(client); // an unrelated row

        var page = await client.GetFromJsonAsync<PagedResult<SubscriptionListItem>>($"{Path}?search=MBR-12345");
        var item = Assert.Single(page!.Items);
        Assert.Equal("Membership", item.Name);
        Assert.Equal("MBR-12345", item.ExternalId);
    }

    // ── Pause / archive (criteria #8, #9) ──────────────────────────────────────

    [Fact]
    public async Task Put_Pause_KeepsRowVisible_And_PreservesTimestamp()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        var pause = await client.PutAsJsonAsync($"{Path}/{id}", UpdateSub(paused: true));
        var paused = await pause.Content.ReadFromJsonAsync<ExistingSubscription>();
        Assert.NotNull(paused!.Paused);
        var firstStamp = paused.Paused;

        // Still visible in the default list and surfaced by the Paused status filter.
        var defaultList = await client.GetFromJsonAsync<PagedResult<SubscriptionListItem>>(Path);
        Assert.Contains(defaultList!.Items, s => s.SubscriptionId == id);
        var pausedList = await client.GetFromJsonAsync<PagedResult<SubscriptionListItem>>($"{Path}?statuses={SubscriptionStatusFilter.Paused}");
        Assert.Contains(pausedList!.Items, s => s.SubscriptionId == id);

        var repause = await client.PutAsJsonAsync($"{Path}/{id}", UpdateSub(paused: true));
        var repaused = await repause.Content.ReadFromJsonAsync<ExistingSubscription>();
        Assert.Equal(firstStamp, repaused!.Paused);

        var resume = await client.PutAsJsonAsync($"{Path}/{id}", UpdateSub(paused: false));
        var resumed = await resume.Content.ReadFromJsonAsync<ExistingSubscription>();
        Assert.Null(resumed!.Paused);
    }

    [Fact]
    public async Task Put_Archive_HidesFromDefaultList_And_UnarchiveRestores()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        // Archiving requires an ended term, so the same PUT carries the lapsed end date.
        var archive = await client.PutAsJsonAsync($"{Path}/{id}", UpdateSub(archived: true, endDate: Lapsed));
        archive.EnsureSuccessStatusCode();
        var active = await client.GetFromJsonAsync<PagedResult<SubscriptionListItem>>($"{Path}?statuses={SubscriptionStatusFilter.Active}");
        Assert.DoesNotContain(active!.Items, s => s.SubscriptionId == id);
        var archivedList = await client.GetFromJsonAsync<PagedResult<SubscriptionListItem>>($"{Path}?statuses={SubscriptionStatusFilter.Archived}");
        Assert.Contains(archivedList!.Items, s => s.SubscriptionId == id);

        await client.PutAsJsonAsync($"{Path}/{id}", UpdateSub(archived: false));
        var restored = await client.GetFromJsonAsync<PagedResult<SubscriptionListItem>>($"{Path}?statuses={SubscriptionStatusFilter.Active}");
        Assert.Contains(restored!.Items, s => s.SubscriptionId == id);
    }

    // ── Mass-assignment guard (criterion #11) ──────────────────────────────────

    [Fact]
    public async Task Post_WithNestedContactAndRawStamps_IgnoresThem()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        var contactId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        // A populated nested "contact" object and raw paused/archived timestamps must be ignored:
        // only the scalar contactId links, and paused/archived stay null (no boolean toggle sent).
        var body = new
        {
            name = "Streaming",
            contactId,
            contact = new { contactId, name = "HIJACKED", organizationNumber = "EVIL" },
            startDate = "2026-01-01",
            amount = 9.99m,
            currencyCode = "USD",
            interval = (int)BillingInterval.Monthly,
            firstBillingDate = "2026-01-15",
            paused = "2020-01-01T00:00:00Z",
            archived = "2020-01-01T00:00:00Z",
        };

        var post = await client.PostAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = await post.Content.ReadFromJsonAsync<ExistingSubscription>();
        Assert.Null(created!.Paused);
        Assert.Null(created.Archived);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var contact = await context.Contacts.Include(c => c.OrganizationDetails).FirstAsync(c => c.ContactId == contactId);
        Assert.Equal("Netflix", contact.OrganizationDetails!.LegalName);
        Assert.Equal("ORG-12345", contact.OrganizationNumber);
    }

    // ── Delete (criterion #10) ─────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesSubscription()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        var delete = await client.DeleteAsync($"{Path}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var fetch = await client.GetAsync($"{Path}/{id}");
        Assert.Equal(HttpStatusCode.NotFound, fetch.StatusCode);
    }

    [Fact]
    public async Task Put_MissingSubscription_ReturnsNotFound()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"{Path}/{Guid.NewGuid()}", UpdateSub());
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task Put_UpdatesFields_And_NotesRoundTripButAreAbsentFromListProjection()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        var request = UpdateSub();
        request.Name = "Renamed";
        request.Amount = 24.00m;
        request.Notes = "billing note";
        var put = await client.PutAsJsonAsync($"{Path}/{id}", request);
        var updated = await put.Content.ReadFromJsonAsync<ExistingSubscription>();
        Assert.Equal("Renamed", updated!.Name);
        Assert.Equal(24.00m, updated.Amount);
        Assert.Equal("billing note", updated.Notes);

        // Notes are on the single-item read model but not the lean list projection.
        var listJson = await client.GetStringAsync($"{Path}?search=Renamed");
        Assert.DoesNotContain("billing note", listJson);
    }

    // ── List contract (criterion #7) ───────────────────────────────────────────

    [Fact]
    public async Task List_UnknownSortKey_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(ReadOnly);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?sortBy=notARealKey");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_OutOfRangeLimit_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(ReadOnly);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?limit=99999999");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_FiltersByIntervalAndSortsByAmountDescending()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        foreach (var (name, amount, interval) in new[]
        {
            ("Cheap", 5m, BillingInterval.Monthly),
            ("Pricey", 30m, BillingInterval.Monthly),
            ("Yearly", 99m, BillingInterval.Yearly),
        })
        {
            var req = NewSub();
            req.Name = name;
            req.Amount = amount;
            req.Interval = interval;
            (await client.PostAsJsonAsync(Path, req)).EnsureSuccessStatusCode();
        }

        var page = await client.GetFromJsonAsync<PagedResult<SubscriptionListItem>>(
            $"{Path}?Intervals=Monthly&sortBy=Amount&sortDir=Desc");
        Assert.Equal(["Pricey", "Cheap"], page!.Items.Select(s => s.Name));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // ── Summary endpoint (run-rate + upcoming renewals follow-up) ───────────────────

    [Fact]
    public async Task Summary_WithoutReadPermission_ReturnsForbidden()
    {
        await using var factory = new OdysseyApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}/summary");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Summary_ReturnsCounts_RunRate_And_Shape()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync(Path, NewSub());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var response = await client.GetAsync($"{Path}/summary?baseCurrency=USD");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<SubscriptionSummary>();
        Assert.NotNull(summary);
        Assert.Equal("USD", summary!.RunRate.BaseCurrency);
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.CountsByStatus.Active);
        // The seeded sub is a live USD $9.99/month → the run-rate round-trips as a real converted figure
        // over HTTP (not just a shape check): monthly 9.99, yearly 119.88.
        Assert.Equal(9.99m, summary.RunRate.ConvertedMonthly);
        Assert.Equal(119.88m, summary.RunRate.ConvertedYearly);
    }

    [Fact]
    public async Task Summary_InvalidBaseCurrency_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        // Over-long baseCurrency violates [StringLength(3)] → 400 via [ApiController] model validation.
        var response = await client.GetAsync($"{Path}/summary?baseCurrency=DOLLARS");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static NewSubscription NewSub(Guid? contactId = null) => new()
    {
        Name = "Streaming",
        ContactId = contactId,
        StartDate = new DateOnly(2026, 1, 1),
        Amount = 9.99m,
        CurrencyCode = "USD",
        Interval = BillingInterval.Monthly,
        FirstBillingDate = new DateOnly(2026, 1, 15),
    };

    private static UpdateSubscription UpdateSub(
        Guid? contactId = null, bool paused = false, bool archived = false, DateOnly? endDate = null) => new()
    {
        Name = "Streaming",
        ContactId = contactId,
        StartDate = new DateOnly(2026, 1, 1),
        EndDate = endDate,
        Amount = 9.99m,
        CurrencyCode = "USD",
        Interval = BillingInterval.Monthly,
        FirstBillingDate = new DateOnly(2026, 1, 15),
        Paused = paused,
        Archived = archived,
    };

    /// <summary>An end date safely in the past for any real "today" — the API runs on the system clock,
    /// so a fixed 2026 date would age into the future relative to nothing; this is anchored backwards
    /// from the actual current date instead.</summary>
    private static DateOnly Lapsed => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

    private static async Task<Guid> CreateAsync(HttpClient client, Guid? contactId = null)
    {
        var post = await client.PostAsJsonAsync(Path, NewSub(contactId));
        post.EnsureSuccessStatusCode();
        var created = await post.Content.ReadFromJsonAsync<ExistingSubscription>();
        return created!.SubscriptionId;
    }

    private static async Task EnsureDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
    }

    private static async Task<Guid> SeedContactAsync(WebApplicationFactory<Program> factory, bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        // Reference currencies live in OdysseyContext (HasData); ensure it exists too (Contact moved to
        // OdysseyContext, so seeding a contact no longer creates OdysseyContext as a side effect).
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "NETFLIX",
            Type = ContactType.Organization,
            OrganizationNumber = "ORG-12345",
            Notes = "secret contact notes",
            Archived = archived ? DateTime.UtcNow : null,
            OrganizationDetails = new() { LegalName = "Netflix", OrganizationNumber = "ORG-12345" },
        });
        await context.SaveChangesAsync();
        return id;
    }
}
