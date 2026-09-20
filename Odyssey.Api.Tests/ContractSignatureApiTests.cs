using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContractType = Odyssey.Dtos.Finance.ContractType;
using TermKind = Odyssey.Dtos.Finance.TermKind;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using Interval = Odyssey.Dtos.Finance.Interval;

namespace Odyssey.Api.Tests;

/// <summary>
/// The signature lifecycle over real HTTP (issue #145): the two request fields, the three guards and
/// their problem-details shape, the roll-up exclusion, the status filter, the unchanged claim gating
/// and the unwidened write surface.
///
/// <para>
/// The service-level derivation cases live in <c>Odyssey.Core.Tests.ContractSignatureTests</c>; what
/// is here is what only the pipeline can answer — model binding, status codes, the <c>errors</c>
/// extension, and the assembled summary body.
/// </para>
/// </summary>
public class ContractSignatureApiTests
{
    private const string ActorUserId = "contract-signature-actor";
    private const string Path = "/api/contracts";

    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ReadyOn = FixedToday.Date.AddDays(-30);
    private static readonly DateTime SignedOn = FixedToday.Date.AddDays(-28);

    private static readonly string[] ReadOnly = [PermissionClaims.ContractsRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate, PermissionClaims.ContractsDelete,
    ];

    // ── Create and read (AC 1–3) ─────────────────────────────────────────────────

    /// <summary>
    /// AC 1 — both fields omitted is the normal create path: <c>201</c>, both null on the wire, and a
    /// <c>Draft</c>.
    /// </summary>
    [Fact]
    public async Task Post_WithNeitherStamp_Returns201_AndADraft()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, New());

        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = (await post.Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.Null(created.Ready);
        Assert.Null(created.Signed);
        Assert.Equal(ContractStatus.Draft, created.Status);

        // And it round-trips through a fresh GET rather than only on the create response.
        var fetched = (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{created.ContractId}"))!;
        Assert.Equal(ContractStatus.Draft, fetched.Status);
    }

    /// <summary>
    /// AC 2–3 — the two fields round-trip through <c>PUT</c> and the derived status follows: ready
    /// alone reads <c>Ready</c> regardless of the term dates, ready + signed hands the row back to the
    /// date chain. Both are asserted on a fresh <c>GET</c> and on the list row, so no read path is
    /// carrying a stale projection.
    /// </summary>
    [Fact]
    public async Task Put_SetsTheStamps_AndEveryReadPathAgrees()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var ready = await client.PutAsJsonAsync($"{Path}/{id}", Update(ready: ReadyOn));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(ContractStatus.Ready, (await ready.Content.ReadFromJsonAsync<ExistingContract>())!.Status);

        var signed = await client.PutAsJsonAsync($"{Path}/{id}", Update(ReadyOn, SignedOn));
        var body = (await signed.Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.Equal(ReadyOn, body.Ready);
        Assert.Equal(SignedOn, body.Signed);
        Assert.Equal(ContractStatus.Active, body.Status);

        var fetched = (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{id}"))!;
        Assert.Equal(SignedOn, fetched.Signed);

        var row = Assert.Single(await client.GetPagedItemsAsync<ContractListItem>(Path) ?? []);
        Assert.Equal(ReadyOn, row.Ready);
        Assert.Equal(SignedOn, row.Signed);
        Assert.Equal(ContractStatus.Active, row.Status);
    }

    /// <summary>
    /// <b>AC 4 over HTTP</b> — the signature layer outranks the date chain end to end, not only at the
    /// service level. Issue #145 §12 asks for this at both tiers on purpose: the derivation runs in
    /// three separate read paths (the create response, the detail <c>GET</c> and the list projection),
    /// and a layer wired into one of them and not the others is exactly the defect a service-level
    /// test cannot see.
    ///
    /// <para>
    /// The two readings being refused are an unsigned contract with a FUTURE start reading
    /// <c>Upcoming</c> — which would assert a commitment nobody has made and put it back into the
    /// upcoming charges — and one whose end has PASSED reading <c>Expired</c>, which would describe a
    /// stalled negotiation as an agreement that ran its course.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(5, 100, false, ContractStatus.Draft)]
    [InlineData(5, 100, true, ContractStatus.Ready)]
    [InlineData(-100, -1, false, ContractStatus.Draft)]
    [InlineData(-100, -1, true, ContractStatus.Ready)]
    public async Task AnUnsignedContract_OutranksTheDateChain_OnEveryReadPath(
        int startOffset, int endOffset, bool markedReady, ContractStatus expected)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = "Agreement",
            Type = ContractType.Service,
            StartDate = FixedToday.Date.AddDays(startOffset),
            EndDate = FixedToday.Date.AddDays(endOffset),
            Ready = markedReady ? ReadyOn : null,
        });

        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = (await post.Content.ReadFromJsonAsync<ExistingContract>())!;

        // All three read paths agree: the create response, the detail GET and the list row.
        Assert.Equal(expected, created.Status);
        Assert.Equal(
            expected,
            (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{created.ContractId}"))!.Status);
        Assert.Equal(
            expected,
            Assert.Single(await client.GetPagedItemsAsync<ContractListItem>(Path) ?? []).Status);

        // And the two readings it is NOT: neither the commitment nobody made nor the term that never ran.
        Assert.NotEqual(ContractStatus.Upcoming, created.Status);
        Assert.NotEqual(ContractStatus.Expired, created.Status);
    }

    // ── The guards over HTTP (AC 7) ──────────────────────────────────────────────

    /// <summary>
    /// AC 7 — each guard is a <c>400</c> carrying its stable <c>code</c> and an <c>errors</c> entry
    /// naming the offending field, on <c>POST</c> and on <c>PUT</c> alike. The field key is what lets
    /// a form render the message on the control that caused it rather than only in a toast.
    /// </summary>
    [Theory]
    [InlineData(null, -1, "contract_signed_requires_ready", "signed")]
    [InlineData(-1, -2, "contract_signed_before_ready", "signed")]
    [InlineData(1, null, "contract_signature_date_in_future", "ready")]
    [InlineData(-1, 1, "contract_signature_date_in_future", "signed")]
    public async Task Guards_Return400_WithTheirCodeAndField(
        int? readyOffset, int? signedOffset, string code, string field)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ready = readyOffset is { } r ? FixedToday.Date.AddDays(r) : (DateTime?)null;
        var signed = signedOffset is { } g ? FixedToday.Date.AddDays(g) : (DateTime?)null;

        var post = await client.PostAsJsonAsync(Path, New(ready, signed));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal(code, await CodeAsync(post));
        Assert.True(await HasErrorKeyAsync(post, field));

        var id = await CreateAsync(client);
        var put = await client.PutAsJsonAsync($"{Path}/{id}", Update(ready, signed));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(code, await CodeAsync(put));
        Assert.True(await HasErrorKeyAsync(put, field));

        // A refused write changes nothing.
        Assert.Null((await client.GetFromJsonAsync<ExistingContract>($"{Path}/{id}"))!.Signed);
    }

    /// <summary>
    /// AC 7 — a signature dated TODAY but a few hours ahead of the server clock is accepted: G3
    /// compares at date granularity precisely so an ordinary "signed just now" from a slightly fast
    /// client is not a 400.
    /// </summary>
    [Fact]
    public async Task ASignatureAheadOfTheServerClockButStillToday_IsAccepted()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ahead = FixedToday.AddHours(6);
        var post = await client.PostAsJsonAsync(Path, New(ahead, ahead));

        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
    }

    /// <summary>
    /// AC 8 — clearing is never refused, and AC 15 — a body that OMITS both fields clears them. The
    /// second is the full-replacement convention this DTO already has for <c>isArchived</c> and
    /// <c>isPaused</c>, and it is what makes "clearing Signed is always allowed" expressible.
    /// </summary>
    [Fact]
    public async Task APutOmittingTheStamps_ClearsThemAndDerivesDraft()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        (await client.PutAsJsonAsync($"{Path}/{id}", Update(ReadyOn, SignedOn))).EnsureSuccessStatusCode();

        // Clearing Signed alone: back to Ready.
        var unsigned = await client.PutAsJsonAsync($"{Path}/{id}", Update(ready: ReadyOn));
        Assert.Equal(HttpStatusCode.OK, unsigned.StatusCode);
        Assert.Equal(ContractStatus.Ready, (await unsigned.Content.ReadFromJsonAsync<ExistingContract>())!.Status);

        // A body that names neither field at all: both cleared, Draft.
        var omitted = await client.PutAsJsonAsync($"{Path}/{id}", new
        {
            name = "Agreement",
            type = ContractType.Service,
            startDate = FixedToday.Date.AddDays(-10),
        });

        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        var body = (await omitted.Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.Null(body.Ready);
        Assert.Null(body.Signed);
        Assert.Equal(ContractStatus.Draft, body.Status);
    }

    // ── The two amended guards (AC 9, AC 21) ─────────────────────────────────────

    /// <summary>
    /// AC 9 — pausing an unsigned contract is a <c>400</c> under the EXISTING code, and archiving one
    /// with no end date at all is a <c>200</c> under the widened rule. Both are the amendments, in one
    /// place, on one contract.
    /// </summary>
    [Fact]
    public async Task AnUnsignedContract_CannotBePaused_ButCanBeArchived()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var pause = await client.PutAsJsonAsync($"{Path}/{id}", Update(isPaused: true));
        Assert.Equal(HttpStatusCode.BadRequest, pause.StatusCode);
        Assert.Equal("contract_pause_requires_active", await CodeAsync(pause));

        // No end date, so the un-widened archive rule would have stranded it forever.
        var archive = await client.PutAsJsonAsync($"{Path}/{id}", Update(isArchived: true));
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        var archived = (await archive.Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.NotNull(archived.Archived);
        Assert.Equal(ContractStatus.Archived, archived.Status);
    }

    /// <summary>
    /// AC 21 — sign and pause in ONE call succeeds. The pause guard reads the request's stamps, not
    /// the stored ones, so a body that makes the contract Active may pause it in the same write.
    /// </summary>
    [Fact]
    public async Task SignAndPause_InOneCall_Returns200()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var response = await client.PutAsJsonAsync($"{Path}/{id}", Update(ReadyOn, SignedOn, isPaused: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.Equal(ContractStatus.Paused, body.Status);
        Assert.NotNull(body.Paused);
        Assert.Equal(SignedOn, body.Signed);
    }

    /// <summary>
    /// AC 9 — the widened rule is a BRANCH, not a removal: a signed contract that has not ended is
    /// still refused.
    /// </summary>
    [Fact]
    public async Task ArchivingASignedRunningContract_IsStillRefused()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);
        (await client.PutAsJsonAsync($"{Path}/{id}", Update(ReadyOn, SignedOn))).EnsureSuccessStatusCode();

        var archive = await client.PutAsJsonAsync(
            $"{Path}/{id}", Update(ReadyOn, SignedOn, isArchived: true, endDate: FixedToday.Date.AddDays(30)));

        Assert.Equal(HttpStatusCode.BadRequest, archive.StatusCode);
    }

    // ── The roll-up (AC 11–12) ───────────────────────────────────────────────────

    /// <summary>
    /// AC 11–12 — read off the assembled SUMMARY body rather than by inspecting the gate expression.
    /// A <c>Draft</c> carrying an in-force periodic fee contributes nothing to the run rate, its
    /// by-type split or the upcoming charges, and is not counted in <c>endingSoon</c> even with an end
    /// date inside the window — while being counted in <c>countsByType</c> and in the new
    /// <c>draft</c> bucket, with all seven real buckets still summing to <c>totalContracts</c>.
    /// </summary>
    [Fact]
    public async Task ADraftWithAnInForceFee_LeavesTheMoney_ButIsCountedOnFile()
    {
        await using var factory = new ApiFactory(ReadWrite);
        // The reference currencies are seeded by the model, so the schema has to exist before a term
        // names one — a fee with no supported currency is refused before the roll-up is ever reached.
        await EnsureSchemaAsync(factory);
        using var client = factory.CreateClient();

        // The draft: an end date inside the ending-soon window, and a priced monthly fee.
        var draft = await CreateAsync(client, end: FixedToday.Date.AddDays(5));
        (await client.PostAsJsonAsync($"{Path}/{draft}/terms", new NewTerm
        {
            TermKind = TermKind.Fee,
            Label = "Quoted rate",
            ValueUnit = TermValueUnit.Amount,
            Value = 100m,
            CurrencyCode = "USD",
            Interval = Interval.Monthly,
            IntervalCount = 1,
            EffectiveFrom = FixedToday.Date.AddDays(-60),
        })).EnsureSuccessStatusCode();

        // A Ready one, and a signed Active one carrying the only fee that should be priced.
        var ready = await CreateAsync(client, name: "Ready agreement");
        (await client.PutAsJsonAsync($"{Path}/{ready}", Update(ready: ReadyOn, name: "Ready agreement")))
            .EnsureSuccessStatusCode();

        var active = await CreateAsync(client, name: "Active agreement");
        (await client.PutAsJsonAsync($"{Path}/{active}", Update(ReadyOn, SignedOn, name: "Active agreement")))
            .EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"{Path}/{active}/terms", new NewTerm
        {
            TermKind = TermKind.Fee,
            Label = "Plan fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 50m,
            CurrencyCode = "USD",
            Interval = Interval.Monthly,
            IntervalCount = 1,
            EffectiveFrom = FixedToday.Date.AddDays(-60),
        })).EnsureSuccessStatusCode();

        var summary = (await client.GetFromJsonAsync<ContractSummary>($"{Path}/summary"))!;

        // The money covers the ACTIVE contract alone: 50, not 150.
        Assert.Equal(50m, summary.RunRate.Monthly);
        Assert.Equal(50m, Assert.Single(summary.RunRate.ByType).Monthly);
        Assert.All(summary.UpcomingCharges, charge => Assert.NotEqual(draft, charge.ContractId));

        // The draft's end date is inside the window and it is still not "ending soon" — that slice is
        // a slice of Active.
        Assert.Equal(0, summary.CountsByStatus.EndingSoon);

        // Counted on file: the by-type headcount covers all three.
        Assert.Equal(3, summary.CountsByType.Sum(t => t.Count));

        // And the seven real buckets partition the set.
        var counts = summary.CountsByStatus;
        Assert.Equal(1, counts.Draft);
        Assert.Equal(1, counts.Ready);
        Assert.Equal(1, counts.Active);
        Assert.Equal(
            summary.TotalContracts,
            counts.Active + counts.Upcoming + counts.Expired + counts.Archived + counts.Paused
                + counts.Draft + counts.Ready);
    }

    // ── The status filter (AC 13) ────────────────────────────────────────────────

    /// <summary>
    /// AC 13 — the two new members bind on the query string and select exactly the unsigned set; a
    /// value outside the enum and an over-length array are both rejected by model validation, before
    /// the service runs.
    /// </summary>
    [Fact]
    public async Task StatusFilter_BindsTheNewMembers_AndStillRejectsBadInput()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var draft = await CreateAsync(client, name: "Draft agreement");
        var ready = await CreateAsync(client, name: "Ready agreement");
        (await client.PutAsJsonAsync($"{Path}/{ready}", Update(ready: ReadyOn, name: "Ready agreement")))
            .EnsureSuccessStatusCode();
        var active = await CreateAsync(client, name: "Active agreement");
        (await client.PutAsJsonAsync($"{Path}/{active}", Update(ReadyOn, SignedOn, name: "Active agreement")))
            .EnsureSuccessStatusCode();

        var unsigned = await client.GetPagedItemsAsync<ContractListItem>($"{Path}?statuses=5&statuses=6");
        Assert.Equal(
            new HashSet<Guid> { draft, ready },
            unsigned!.Select(c => c.ContractId).ToHashSet());

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync($"{Path}?statuses=99")).StatusCode);

        var tooMany = string.Join('&', Enumerable
            .Range(0, ListDefaults.MaxFilterArrayLength + 1)
            .Select(_ => "statuses=Draft"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Path}?{tooMany}")).StatusCode);
    }

    /// <summary>AC 14 — the list sort follows the lifecycle rank end to end over HTTP.</summary>
    [Fact]
    public async Task SortByStatus_PutsDraftAndReadyFirst()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var draft = await CreateAsync(client, name: "Draft agreement");
        var ready = await CreateAsync(client, name: "Ready agreement");
        (await client.PutAsJsonAsync($"{Path}/{ready}", Update(ready: ReadyOn, name: "Ready agreement")))
            .EnsureSuccessStatusCode();
        var active = await CreateAsync(client, name: "Active agreement");
        (await client.PutAsJsonAsync($"{Path}/{active}", Update(ReadyOn, SignedOn, name: "Active agreement")))
            .EnsureSuccessStatusCode();

        var sorted = await client.GetPagedItemsAsync<ContractListItem>($"{Path}?sortBy=status&sortDir=asc");

        Assert.Equal([draft, ready, active], sorted!.Select(c => c.ContractId).ToArray());
    }

    // ── Authorization and the write surface (AC 16–17) ───────────────────────────

    /// <summary>
    /// AC 16 — no new claim. The existing three gate every affected endpoint, and a read-only
    /// principal is refused the two writes and writes nothing.
    /// </summary>
    [Fact]
    public async Task TheStampsRideTheExistingClaims()
    {
        await using var write = new ApiFactory(ReadWrite);
        using var writer = write.CreateClient();
        var id = await CreateAsync(writer);

        await using var read = new ApiFactory(ReadOnly);
        using var reader = read.CreateClient();

        // contracts.read reaches all three read surfaces …
        (await reader.GetAsync(Path)).EnsureSuccessStatusCode();
        (await reader.GetAsync($"{Path}/summary")).EnsureSuccessStatusCode();

        // … and neither write.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await reader.PostAsJsonAsync(Path, New(ReadyOn, SignedOn))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await reader.PutAsJsonAsync($"{Path}/{id}", Update(ReadyOn, SignedOn))).StatusCode);
    }

    /// <summary>
    /// AC 17 — the two scalar fields do not widen the bound write surface. A body carrying the read
    /// model's nested collections and server-owned stamps alongside them creates and mutates nothing:
    /// neither field adds a relationship, so there is no nested-object write path and no id a caller
    /// could point at another entity.
    /// </summary>
    [Fact]
    public async Task ABodyCarryingNestedCollectionsAlongsideTheStamps_MutatesNone()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var response = await client.PutAsJsonAsync($"{Path}/{id}", new
        {
            name = "Agreement",
            type = ContractType.Service,
            startDate = FixedToday.Date.AddDays(-10),
            ready = ReadyOn,
            signed = SignedOn,
            // None of these is on UpdateContract; all are server-owned or nested.
            archived = FixedToday.Date.AddDays(-1),
            paused = FixedToday.Date.AddDays(-1),
            createdAtUtc = FixedToday.Date.AddYears(-5),
            parties = new[] { new { accountId = Guid.NewGuid() } },
            files = new[] { new { fileMetadataId = Guid.NewGuid() } },
            currentTerms = new[] { new { value = 999m, currencyCode = "USD" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<ExistingContract>())!;
        Assert.Equal(SignedOn, updated.Signed);
        Assert.Null(updated.Archived);
        Assert.Null(updated.Paused);
        Assert.Empty(updated.Parties);
        Assert.Empty(updated.Files);
        Assert.Empty(updated.CurrentTerms);
        Assert.NotEqual(FixedToday.Date.AddYears(-5), updated.CreatedAtUtc);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static NewContract New(
        DateTime? ready = null, DateTime? signed = null, DateTime? end = null, string name = "Agreement") => new()
        {
            Name = name,
            Type = ContractType.Service,
            StartDate = FixedToday.Date.AddDays(-10),
            EndDate = end,
            Ready = ready,
            Signed = signed,
        };

    private static UpdateContract Update(
        DateTime? ready = null, DateTime? signed = null, bool isArchived = false, bool isPaused = false,
        DateTime? endDate = null, string name = "Agreement") => new()
        {
            Name = name,
            Type = ContractType.Service,
            StartDate = FixedToday.Date.AddDays(-10),
            EndDate = endDate,
            IsArchived = isArchived,
            IsPaused = isPaused,
            Ready = ready,
            Signed = signed,
        };

    private static async Task<Guid> CreateAsync(
        HttpClient client, DateTime? end = null, string name = "Agreement")
    {
        var post = await client.PostAsJsonAsync(Path, New(end: end, name: name));
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingContract>())!.ContractId;
    }

    private static async Task EnsureSchemaAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
    }

    /// <summary>The stable machine-readable discriminator, read straight off the wire.</summary>
    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<bool> HasErrorKeyAsync(HttpResponseMessage response, string field)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.TryGetProperty("errors", out var errors)
            && errors.EnumerateObject().Any(e => string.Equals(e.Name, field, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(IReadOnlyCollection<string>? permissions)
            : base(permissions, ActorUserId, configuration: null, configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedToday));
            })
        {
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
