using System.Net;
using System.Net.Http.Json;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using ContextAccountType = Odyssey.Context.AccountType;
// Both halves of each aligned pair are in scope (Odyssey.Context for the direct seeds,
// Odyssey.Dtos.Finance for the wire), so each wire type is named explicitly.
using ContractType = Odyssey.Dtos.Finance.ContractType;
using TermKind = Odyssey.Dtos.Finance.TermKind;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using TermDirection = Odyssey.Dtos.Finance.TermDirection;
using Interval = Odyssey.Dtos.Finance.Interval;

namespace Odyssey.Api.Tests;

/// <summary>
/// A term's money direction over real HTTP (issue #159): the write path's two refusals and its
/// full-replace behaviour, the read-path parity across all four projections of a <c>Terms</c> row, and
/// the two halves of the roll-up that need a real exchange-rate table rather than a stub.
/// </summary>
/// <remarks>
/// The cadence arithmetic and the bucketing rules are the unit tier's subject
/// (<c>ContractSummaryDirectionTests</c>); what only this tier reaches is the claim gates, the
/// <c>[EnumDataType]</c> model validation that runs BEFORE the service, and the conversion path — a
/// rate row is a real table, and the one base currency elected across both directions cannot be shown
/// without one.
/// </remarks>
public class ContractTermDirectionApiTests
{
    private const string ActorUserId = "contract-term-direction-actor-id";
    private const string Path = "/api/contracts";

    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate,
    ];

    private static readonly string[] AccountTermWrite =
    [
        PermissionClaims.AccountsTermsRead, PermissionClaims.AccountsTermsWrite,
    ];

    // ── Write path ───────────────────────────────────────────────────────────

    /// <summary>AC 1 / AC 2 — accepted on a fee, and an omitted direction means Outgoing.</summary>
    [Theory]
    [InlineData(TermDirection.Incoming, TermDirection.Incoming)]
    [InlineData(null, TermDirection.Outgoing)]
    public async Task Post_EchoesTheDirection_AndOmittingItMeansOutgoing(
        TermDirection? sent, TermDirection expected)
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var body = Salary(600000m);
        if (sent is { } direction)
        {
            body.Direction = direction;
        }

        var response = await client.PostAsJsonAsync(Terms(contractId), body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ExistingTerm>();
        Assert.Equal(expected, created!.Direction);
    }

    /// <summary>
    /// A percentage term on a contract carries a direction like any other: the rate kinds that
    /// refused one were folded into labelled terms.
    /// </summary>
    [Fact]
    public async Task Post_IncomingOnAPercentageTerm_IsAccepted()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Terms(contractId), new NewTerm
        {
            TermKind = TermKind.Fee,
            Label = "Interest rate",
            ValueUnit = TermValueUnit.Percentage,
            Value = 0.0325m,
            Direction = TermDirection.Incoming,
            EffectiveFrom = FixedToday.AddDays(-30),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ExistingTerm>();
        Assert.Equal(TermDirection.Incoming, created!.Direction);
    }

    /// <summary>
    /// AC 4 — an account-owned term may not carry a non-default direction, and omitting it still
    /// succeeds. No account surface READS a direction, so accepting one there would let a user record
    /// a fact the product then contradicts.
    /// </summary>
    [Fact]
    public async Task Post_OnAnAccountTerm_Refuses_Incoming_AndAcceptsAnOmittedDirection()
    {
        await using var factory = await NewFactoryAsync([.. ReadWrite, .. AccountTermWrite]);
        using var client = factory.CreateClient();
        var accountId = await SeedAccountAsync(factory);

        var refused = await client.PostAsJsonAsync($"/api/accounts/{accountId}/terms", new NewTerm
        {
            TermKind = TermKind.Fee,
            Label = "Interest received",
            ValueUnit = TermValueUnit.Amount,
            Value = 12m,
            Direction = TermDirection.Incoming,
            Interval = Interval.Monthly,
            EffectiveFrom = FixedToday.AddDays(-30),
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<ApiProblemBody>();
        Assert.True(problem!.Errors!.ContainsKey(nameof(NewTerm.Direction)));

        var accepted = await client.PostAsJsonAsync($"/api/accounts/{accountId}/terms", new NewTerm
        {
            TermKind = TermKind.Fee,
            Label = "Card fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 4m,
            Interval = Interval.Monthly,
            EffectiveFrom = FixedToday.AddDays(-30),
        });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(TermDirection.Outgoing,
            (await accepted.Content.ReadFromJsonAsync<ExistingTerm>())!.Direction);
    }

    /// <summary>
    /// AC 5 — a value outside the enum is refused by <c>[ApiController]</c> model validation, before
    /// the service is reached. Nothing is written.
    /// </summary>
    [Fact]
    public async Task Post_AnUnrecognisedDirection_Is400FromModelValidation_AndWritesNothing()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Terms(contractId), new
        {
            termKind = TermKind.Fee,
            label = "Base salary",
            valueUnit = TermValueUnit.Amount,
            value = 600000m,
            currencyCode = "EUR",
            direction = "Sideways",
            interval = Interval.Monthly,
            effectiveFrom = FixedToday.AddDays(-30),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<ExistingTerm>>(Terms(contractId)))!);
    }

    /// <summary>
    /// AC 6 — correcting a mis-directed term is an ordinary replace. It does not fork the series and
    /// raises no conflict: direction is a property of the ENTRY and joins neither the series key nor
    /// the duplicate guard.
    /// </summary>
    [Fact]
    public async Task Put_CorrectingTheDirection_Replaces_RatherThanForkingTheSeries()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var created = await PostTermAsync(client, contractId, Salary(600000m, TermDirection.Incoming));

        var corrected = Salary(600000m);
        var response = await client.PutAsJsonAsync($"{Terms(contractId)}/{created.TermId}", corrected);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var history = (await client.GetFromJsonAsync<List<ExistingTerm>>(Terms(contractId)))!;
        Assert.Equal(TermDirection.Outgoing, Assert.Single(history).Direction);
    }

    /// <summary>
    /// AC 7 — the documented full-replace behaviour, pinned so that a later "null means unchanged"
    /// carve-out on this one field is a deliberate decision rather than an accident. Omitting
    /// <c>direction</c> resets the term to <c>Outgoing</c>, exactly as omitting <c>label</c>,
    /// <c>interval</c> or <c>anchorDate</c> already clears those.
    /// </summary>
    [Fact]
    public async Task Put_OmittingTheDirection_ResetsTheTermToOutgoing()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var created = await PostTermAsync(client, contractId, Salary(600000m, TermDirection.Incoming));

        // A body with no `direction` member at all — not one carrying an explicit Outgoing.
        var response = await client.PutAsJsonAsync($"{Terms(contractId)}/{created.TermId}", new
        {
            termKind = TermKind.Fee,
            label = "Base salary",
            valueUnit = TermValueUnit.Amount,
            value = 600000m,
            currencyCode = "EUR",
            interval = Interval.Annually,
            intervalCount = 1,
            effectiveFrom = FixedToday.AddDays(-30),
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var history = (await client.GetFromJsonAsync<List<ExistingTerm>>(Terms(contractId)))!;
        Assert.Equal(TermDirection.Outgoing, Assert.Single(history).Direction);
    }

    /// <summary>
    /// AC 8 — <c>direction</c> is a scalar enum and adds no mass-assignment surface: a body carrying a
    /// populated nested related object alongside it creates and mutates nothing beyond the term.
    /// </summary>
    [Fact]
    public async Task Post_WithANestedRelatedObject_CreatesNothingBeyondTheTerm()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Terms(contractId), new
        {
            termKind = TermKind.Fee,
            label = "Base salary",
            valueUnit = TermValueUnit.Amount,
            value = 600000m,
            currencyCode = "EUR",
            direction = TermDirection.Incoming,
            interval = Interval.Annually,
            effectiveFrom = FixedToday.AddDays(-30),
            contract = new { name = "Injected contract", type = ContractType.Other },
            account = new { name = "Injected account", currencyCode = "USD" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(TermDirection.Incoming,
            (await response.Content.ReadFromJsonAsync<ExistingTerm>())!.Direction);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Single(context.Contracts);
        Assert.Empty(context.Accounts);
        Assert.Single(context.Terms);
    }

    // ── Read-path parity ─────────────────────────────────────────────────────

    /// <summary>
    /// AC 11 — all four projections of the same <c>Terms</c> rows agree on both directions. The
    /// contract detail payload is the one that would have been missed: its <c>CurrentTerms</c> is typed
    /// <c>AccountCurrentTerm</c>, not <c>CurrentTerm</c>, so adding the field to the latter alone would
    /// have left the feature's own goal unmet on its most-used endpoint.
    /// </summary>
    [Fact]
    public async Task Direction_IsPresentOnEveryProjectionOfATermRow()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client, type: ContractType.Employment);

        await PostTermAsync(client, contractId, Salary(600000m, TermDirection.Incoming));
        await PostTermAsync(client, contractId, UnionFee(450m));

        static void AssertBothSides(IEnumerable<(string Label, TermDirection Direction)> rows)
        {
            var byLabel = rows.ToDictionary(r => r.Label, r => r.Direction);
            Assert.Equal(TermDirection.Incoming, byLabel["Base salary"]);
            Assert.Equal(TermDirection.Outgoing, byLabel["Union membership"]);
        }

        var history = (await client.GetFromJsonAsync<List<ExistingTerm>>(Terms(contractId)))!;
        AssertBothSides(history.Select(t => (t.Label!, t.Direction)));

        var current = (await client.GetFromJsonAsync<List<CurrentTerm>>($"{Terms(contractId)}/current"))!;
        AssertBothSides(current.Select(t => (t.Label!, t.Direction)));

        var contract = (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{contractId}"))!;
        AssertBothSides(contract.CurrentTerms.Select(t => (t.Label!, t.Direction)));

        var put = await client.PutAsJsonAsync($"{Path}/{contractId}", new UpdateContract
        {
            Name = contract.Name,
            Type = contract.Type,
            StartDate = contract.StartDate,
            EndDate = contract.EndDate,
        });
        put.EnsureSuccessStatusCode();
        var updated = (await put.Content.ReadFromJsonAsync<ExistingContract>())!;
        AssertBothSides(updated.CurrentTerms.Select(t => (t.Label!, t.Direction)));
    }

    /// <summary>
    /// AC 12 — the embedded account projection carries the field for a principal holding
    /// <b>accounts.read alone</b>. Two things are pinned here. The gate on
    /// <c>ExistingAccount.CurrentTerms</c> is <c>accounts.read</c>, not <c>accounts.terms.read</c>,
    /// which gates only the dedicated term endpoints — the same shared projection therefore sits behind
    /// two independent claims depending on the route, and a future claim split would change that
    /// silently. And <c>AccountService.ToCurrentTerm</c> is a HAND-WRITTEN initializer, so a field not
    /// listed there is dropped without a compiler error; it would still read correctly today only
    /// because the omitted value and the correct one coincide.
    /// </summary>
    [Fact]
    public async Task AccountCurrentTerms_CarryTheDirection_ForAnAccountsReadHolderAlone()
    {
        await using var factory = await NewFactoryAsync([.. AccountTermWrite]);
        var accountId = await SeedAccountAsync(factory);
        using (var writer = factory.CreateClient())
        {
            (await writer.PostAsJsonAsync($"/api/accounts/{accountId}/terms", new NewTerm
            {
                TermKind = TermKind.Fee,
                Label = "Card fee",
                ValueUnit = TermValueUnit.Amount,
                Value = 4m,
                Interval = Interval.Monthly,
                EffectiveFrom = FixedToday.AddDays(-30),
            })).EnsureSuccessStatusCode();
        }

        // A SECOND principal against the first's data, which needs the shared store.
        await using var readerFactory = await NewFactoryAsync([PermissionClaims.AccountsRead], factory);
        using var reader = readerFactory.CreateClient();

        var account = (await reader.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{accountId}"))!;

        Assert.Equal(TermDirection.Outgoing, Assert.Single(account.CurrentTerms).Direction);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.GetAsync($"/api/accounts/{accountId}/terms/current")).StatusCode);
    }

    // ── The conversion half of the roll-up ───────────────────────────────────

    /// <summary>
    /// AC 18 — an unconvertible currency is NAMED and excluded from the incoming gross AND the net,
    /// never folded in at 1:1. The exclusion list covers both directions, so the net is partial exactly
    /// when the grosses are.
    /// </summary>
    [Fact]
    public async Task Summary_AnUnconvertibleIncomingCurrency_IsNamedAndLeftOutOfTheNet()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();

        var employment = await CreateContractAsync(client, type: ContractType.Employment, start: FixedToday.AddDays(-30));
        var service = await CreateContractAsync(client, name: "Cleaning", type: ContractType.Service, start: FixedToday.AddDays(-30));

        await PostTermAsync(client, employment, MonthlyFee("Base salary", 5000m, "SEK", TermDirection.Incoming));
        await PostTermAsync(client, service, MonthlyFee("Cleaning", 100m, "USD"));

        var summary = (await client.GetFromJsonAsync<ContractSummary>($"{Path}/summary?baseCurrency=USD"))!;

        Assert.Equal(["SEK"], summary.RunRate.UnconvertedCurrencies);
        Assert.Null(summary.RunRate.IncomingMonthly);
        Assert.Null(summary.RunRate.IncomingYearly);
        Assert.Empty(summary.RunRate.IncomingByType);
        Assert.Equal(100m, summary.RunRate.Monthly);
        // The net is the convertible subset's net — never 5000 − 100, and never a 1:1 fold-in.
        Assert.Equal(-100m, summary.RunRate.NetMonthly);
    }

    /// <summary>
    /// AC 19 — with no base supplied, the vote counts BOTH directions and elects ONE base, which both
    /// sides then convert to. Voting per bucket would elect two bases and make the net a difference of
    /// two different currencies.
    /// </summary>
    [Fact]
    public async Task Summary_TheBaseCurrencyVote_CountsBothDirections_AndElectsOneBase()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        await SeedRateAsync(factory, "USD", "EUR", 0.50m);
        using var client = factory.CreateClient();

        var employment = await CreateContractAsync(client, type: ContractType.Employment, start: FixedToday.AddDays(-30));
        var service = await CreateContractAsync(client, name: "Cleaning", type: ContractType.Service, start: FixedToday.AddDays(-30));

        // Two incoming EUR terms against one outgoing USD term: the majority currency is EUR, and it
        // is reached only by counting the incoming side.
        await PostTermAsync(client, employment, MonthlyFee("Base salary", 4000m, "EUR", TermDirection.Incoming));
        await PostTermAsync(client, employment, MonthlyFee("Bonus", 500m, "EUR", TermDirection.Incoming));
        await PostTermAsync(client, service, MonthlyFee("Cleaning", 200m, "USD"));

        var summary = (await client.GetFromJsonAsync<ContractSummary>($"{Path}/summary"))!;

        Assert.Equal("EUR", summary.RunRate.BaseCurrency);
        Assert.Empty(summary.RunRate.UnconvertedCurrencies);
        Assert.Equal(4500m, summary.RunRate.IncomingMonthly);
        Assert.Equal(100m, summary.RunRate.Monthly);       // 200 USD × 0.50
        Assert.Equal(4400m, summary.RunRate.NetMonthly);
    }

    // ── Authorization ────────────────────────────────────────────────────────

    /// <summary>
    /// AC 10 — the income aggregate is behind <c>contracts.read</c>, which Guest does not hold, so the
    /// three surfaces that now carry it are all refused. The claim set is the real Guest one rather
    /// than a hand-picked list, so a later grant of <c>contracts.read</c> to Guest fails this test
    /// instead of silently widening the reach of the new figures.
    /// </summary>
    [Fact]
    public async Task AGuestPrincipal_CannotReachTheSummary_TheContract_OrItsTerms()
    {
        Assert.DoesNotContain(PermissionClaims.ContractsRead, Odyssey.Context.Authorization.RolePermissions.GuestClaims);

        await using var seedFactory = await NewFactoryAsync(ReadWrite);
        Guid contractId;
        using (var writer = seedFactory.CreateClient())
        {
            contractId = await CreateContractAsync(writer);
            await PostTermAsync(writer, contractId, Salary(600000m, TermDirection.Incoming));
        }

        await using var guestFactory =
            await NewFactoryAsync(Odyssey.Context.Authorization.RolePermissions.GuestClaims, seedFactory);
        using var guest = guestFactory.CreateClient();

        foreach (var route in new[] { $"{Path}/summary", $"{Path}/{contractId}", Terms(contractId) })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync(route)).StatusCode);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Terms(Guid contractId) => $"{Path}/{contractId}/terms";

    private static NewTerm Salary(decimal value, TermDirection direction = TermDirection.Outgoing) => new()
    {
        TermKind = TermKind.Fee,
        Label = "Base salary",
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = "EUR",
        Interval = Interval.Annually,
        IntervalCount = 1,
        Direction = direction,
        EffectiveFrom = FixedToday.AddDays(-30),
    };

    private static NewTerm UnionFee(decimal value) => MonthlyFee("Union membership", value, "EUR");

    private static NewTerm MonthlyFee(
        string label, decimal value, string currency, TermDirection direction = TermDirection.Outgoing) => new()
    {
        TermKind = TermKind.Fee,
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = currency,
        Interval = Interval.Monthly,
        IntervalCount = 1,
        Direction = direction,
        EffectiveFrom = FixedToday.AddDays(-30),
    };

    private static async Task<ExistingTerm> PostTermAsync(HttpClient client, Guid contractId, NewTerm term)
    {
        var response = await client.PostAsJsonAsync(Terms(contractId), term);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingTerm>())!;
    }

    private static async Task<Guid> CreateContractAsync(
        HttpClient client,
        string name = "Employment Agreement",
        ContractType type = ContractType.Employment,
        DateTime? start = null)
    {
        var response = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = name,
            Type = type,
            StartDate = start ?? FixedToday.AddYears(-1),
            // Signed, or the contract derives as Draft and leaves the run rate through the status gate
            // — which would make every roll-up assertion here pass for the wrong reason.
            Ready = FixedToday.AddYears(-1).AddDays(-1),
            Signed = FixedToday.AddYears(-1),
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingContract>())!.ContractId;
    }

    private static async Task<Guid> SeedAccountAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var account = new Account
        {
            Name = "Savings",
            Description = "Seeded for the account-side direction assertions.",
            AccountType = ContextAccountType.SavingsAccount,
            CurrencyCode = "USD",
            Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }

    private static async Task SeedRateAsync(
        WebApplicationFactory<Program> factory, string from, string to, decimal rate)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        context.ExchangeRates.Add(new ExchangeRate
        {
            ExchangeRateId = Guid.NewGuid(),
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = rate,
            AsOf = FixedToday.AddDays(-1),
        });
        await context.SaveChangesAsync();
    }

    /// <summary>The per-field shape of a ProblemDetails body, for the two field-named refusals.</summary>
    private sealed record ApiProblemBody
    {
        public Dictionary<string, string[]>? Errors { get; init; }
    }

    private static async Task<ApiFactory> NewFactoryAsync(
        IReadOnlyCollection<string>? permissions, ApiFactory? sharingStoreWith = null)
    {
        var factory = new ApiFactory(permissions, sharingStoreWith);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
        return factory;
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        /// <param name="sharingStoreWith">
        /// A factory whose InMemory store this one joins. Two of the assertions here need a SECOND
        /// principal reading the first one's data — an <c>accounts.read</c>-only reader, and a Guest —
        /// and an isolated store per factory (the default, for good reason) cannot express that.
        /// </param>
        public ApiFactory(IReadOnlyCollection<string>? permissions, ApiFactory? sharingStoreWith = null)
            : base(permissions, ActorUserId, configuration: null, configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedToday));
            }, sharingStoreWith: sharingStoreWith)
        {
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
