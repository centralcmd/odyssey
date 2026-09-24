using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using Interval = Odyssey.Dtos.Finance.Interval;

namespace Odyssey.Api.Tests;

public class TermsApiTests
{
    private const string ActorUserId = "account-terms-actor-id";

    private static string TermsPath(Guid accountId) => $"/api/accounts/{accountId}/terms";
    private static string CurrentPath(Guid accountId) => $"/api/accounts/{accountId}/terms/current";

    /// <summary>A percentage term named like the former rate kind — now an ordinary labelled series.</summary>
    private static NewTerm InterestRate(decimal value, DateTime effectiveFrom) => new()
    {
        Label = "Interest rate",
        ValueUnit = TermValueUnit.Percentage,
        Value = value,
        EffectiveFrom = effectiveFrom,
    };

    private static NewTerm Fee(string? label, decimal value, DateTime effectiveFrom) => new()
    {
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        EffectiveFrom = effectiveFrom,
    };

    // ── Authorization matrix (spec §7) ────────────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(TermsPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutTermsReadPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(TermsPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Write_WithReadOnlyPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsTermsRead]);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.03m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var put = await client.PutAsJsonAsync($"{TermsPath(accountId)}/{Guid.NewGuid()}", InterestRate(0.03m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        var delete = await client.DeleteAsync($"{TermsPath(accountId)}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Read_WithReadPermission_Succeeds()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsTermsRead]);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var list = await client.GetAsync(TermsPath(accountId));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var current = await client.GetAsync(CurrentPath(accountId));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    /// <summary>
    /// AC 18 — each endpoint refuses a principal holding EVERY claim except the one that gates it,
    /// run after the controller rename to prove the <c>[Authorize]</c> attributes travelled with it.
    /// </summary>
    /// <remarks>
    /// The "holds every other claim" shape is load-bearing rather than thorough. The app configures
    /// a global <c>FallbackPolicy = RequireAuthenticatedUser</c>, so a policy attribute dropped
    /// during a rename does not open the endpoint to anonymous callers — it downgrades it to ANY
    /// authenticated one, which a `permissions: []` principal would still see refused. Only a
    /// principal holding everything else can tell the two apart: here it must still be `403`, where
    /// a fallback-only action would answer `200`.
    /// </remarks>
    [Fact]
    public async Task EachEndpoint_RefusesAPrincipalHoldingEveryClaimButItsOwn()
    {
        var withoutRead = AllClaimsExcept(PermissionClaims.AccountsTermsRead);
        var withoutWrite = AllClaimsExcept(PermissionClaims.AccountsTermsWrite);

        await using (var factory = new ApiFactory(withoutRead))
        {
            var accountId = await SeedAccountAsync(factory);
            using var client = factory.CreateClient();

            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(TermsPath(accountId))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(CurrentPath(accountId))).StatusCode);
        }

        await using (var factory = new ApiFactory(withoutWrite))
        {
            var accountId = await SeedAccountAsync(factory);
            using var client = factory.CreateClient();

            var post = await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.03m, new DateTime(2026, 1, 1)));
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

            var put = await client.PutAsJsonAsync(
                $"{TermsPath(accountId)}/{Guid.NewGuid()}", InterestRate(0.03m, new DateTime(2026, 1, 1)));
            Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{TermsPath(accountId)}/{Guid.NewGuid()}")).StatusCode);
        }
    }

    /// <summary>AC 18's positive half — the gating claim alone is enough for each endpoint.</summary>
    [Fact]
    public async Task EachEndpoint_AdmitsAPrincipalHoldingItsOwnClaim()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(TermsPath(accountId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(CurrentPath(accountId))).StatusCode);

        var post = await client.PostAsJsonAsync(TermsPath(accountId), Fee("Annual card fee", 95m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var created = await post.Content.ReadFromJsonAsync<ExistingTerm>();
        var put = await client.PutAsJsonAsync(
            $"{TermsPath(accountId)}/{created!.TermId}", Fee("Annual card fee", 120m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{TermsPath(accountId)}/{created.TermId}")).StatusCode);
    }

    private static string[] AllClaimsExcept(string claim) =>
        [.. RolePermissions.AllClaims.Where(c => !string.Equals(c, claim, StringComparison.Ordinal))];

    // ── CRUD behaviour (spec §16) ─────────────────────────────────────────────

    [Fact]
    public async Task Post_ValidInterestRate_ReturnsCreatedAndIsRetrievable()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.0325m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var list = await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId));
        var term = Assert.Single(list!);
        Assert.Equal(0.0325m, term.Value);
        Assert.Null(term.CurrencyCode);
    }

    [Fact]
    public async Task Post_AmountFeeWithoutCurrency_DefaultsToAccountCurrency()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount, "EUR");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), Fee("Account fee", 5m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var list = await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId));
        Assert.Equal("EUR", Assert.Single(list!).CurrencyCode);
    }

    [Fact]
    public async Task Post_OnMissingAccount_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(Guid.NewGuid()), InterestRate(0.03m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public async Task Post_Duplicate_ReturnsConflict()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        var date = new DateTime(2026, 1, 1);
        await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.03m, date));
        var duplicate = await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.04m, date));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Current_RespectsAsOfAndSupersession()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.03m, new DateTime(2026, 1, 1)));
        await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.02m, new DateTime(2026, 6, 1)));

        var current = await client.GetFromJsonAsync<List<CurrentTerm>>($"{CurrentPath(accountId)}?asOf=2026-03-01");
        Assert.Equal(0.03m, Assert.Single(current!).Value);
    }

    [Fact]
    public async Task Put_TermNotOnAccount_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"{TermsPath(accountId)}/{Guid.NewGuid()}", InterestRate(0.03m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task Delete_TermNotOnAccount_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        var delete = await client.DeleteAsync($"{TermsPath(accountId)}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task Delete_ExistingTerm_ReturnsNoContent()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.SavingsAccount);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(TermsPath(accountId), InterestRate(0.03m, new DateTime(2026, 1, 1)));
        var term = Assert.Single((await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId)))!);

        var delete = await client.DeleteAsync($"{TermsPath(accountId)}/{term.TermId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }


    // ── Series labels (spec §16) ──────────────────────────────────────────────

    [Fact]
    public async Task Post_TwoLabelledFeesOnTheSameDate_BothCreatedAndBothCurrent()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var date = new DateTime(2026, 1, 1);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM · abroad", 25m, date))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM · domestic", 5m, date))).StatusCode);

        var current = await client.GetFromJsonAsync<List<CurrentTerm>>(CurrentPath(accountId));
        Assert.Equal(2, current!.Count);
        Assert.Equal(new[] { "ATM · abroad", "ATM · domestic" }, current.Select(t => t.Label));
    }

    [Theory]
    [InlineData("atm · abroad")]
    [InlineData("  ATM   ·   abroad ")]
    public async Task Post_LabelDifferingOnlyByCaseOrSpacing_ReturnsConflict(string collidingLabel)
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var date = new DateTime(2026, 1, 1);
        await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM · abroad", 25m, date));
        var duplicate = await client.PostAsJsonAsync(TermsPath(accountId), Fee(collidingLabel, 30m, date));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_FeeWithoutLabel_ReturnsBadRequest(string? label)
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Cash);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), Fee(label, 5m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_OverlongLabel_IsRejectedByModelValidation()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(
            TermsPath(accountId), Fee(new string('x', TermLabel.MaxLength + 1), 5m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_NormalizesLabelOnWriteAndRoundTripsThroughHistory()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(TermsPath(accountId), Fee("  ATM   Abroad  ", 25m, new DateTime(2026, 1, 1)));

        var list = await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId));
        Assert.Equal("ATM Abroad", Assert.Single(list!).Label);
    }

    [Fact]
    public async Task Post_LabelKeyInTheRequestBody_IsNotBound()
    {
        // Mass assignment: LabelKey is on no request DTO, so a body that names it must be ignored and
        // the stored value derived from Label alone.
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), new
        {
            label = "ATM Abroad",
            labelKey = "smuggled",
            valueUnit = TermValueUnit.Amount,
            value = 25m,
            effectiveFrom = new DateTime(2026, 1, 1),
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var stored = await context.Terms.AsNoTracking().SingleAsync(t => t.AccountId == accountId);
        Assert.Equal("ATM Abroad", stored.Label);
        Assert.Equal("atm abroad", stored.LabelKey);
    }

    [Fact]
    public async Task Put_ChangingOnlyTheLabel_SplitsOneCurrentEntryIntoTwo()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM", 25m, new DateTime(2026, 1, 1)));
        await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM", 30m, new DateTime(2026, 6, 1)));

        var beforeCurrent = await client.GetFromJsonAsync<List<CurrentTerm>>($"{CurrentPath(accountId)}?asOf=2026-12-01");
        Assert.Single(beforeCurrent!);

        var later = (await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId)))!
            .Single(t => t.EffectiveFrom.Date == new DateTime(2026, 6, 1));
        var put = await client.PutAsJsonAsync(
            $"{TermsPath(accountId)}/{later.TermId}", Fee("ATM · abroad", 30m, new DateTime(2026, 6, 1)));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var afterCurrent = await client.GetFromJsonAsync<List<CurrentTerm>>($"{CurrentPath(accountId)}?asOf=2026-12-01");
        Assert.Equal(2, afterCurrent!.Count);
    }

    [Fact]
    public async Task AccountRecord_CarriesOneDistinctlyNamedTermPerInForceSeries()
    {
        // The record card renders one tile per in-force term, so a card charging three fees must
        // carry three DIFFERENT names — otherwise it renders three indistinguishable tiles.
        await using var factory = new ApiFactory(
            [PermissionClaims.AccountsTermsRead, PermissionClaims.AccountsTermsWrite, PermissionClaims.AccountsRead]);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var date = new DateTime(2026, 1, 1);
        await client.PostAsJsonAsync(TermsPath(accountId), Fee("Annual card fee", 95m, date));
        await client.PostAsJsonAsync(TermsPath(accountId), Fee("ATM · abroad", 25m, date));
        await client.PostAsJsonAsync(TermsPath(accountId), Fee("Paper statement", 2m, date));

        var account = await client.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{accountId}");

        Assert.Equal(3, account!.CurrentTerms.Count);
        Assert.Equal(3, account.CurrentTerms.Select(t => t.Label).Distinct().Count());
    }

    // ── Cadence: the interval vocabulary (issue #120, AC 2-5, 21, 24) ─────────

    [Fact]
    public async Task Post_RetiredQuarterlyOrdinal_ReturnsBadRequestKeyedOnInterval()
    {
        // Ordinal 4 was Quarterly and is retired permanently. [EnumDataType] calls Enum.IsDefined, so
        // it fails model validation before the service is reached.
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), new
        {
            label = "Quarterly charge",
            valueUnit = TermValueUnit.Amount,
            value = 45m,
            interval = 4,
            effectiveFrom = new DateTime(2026, 1, 1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Contains("Interval", await ErrorKeysAsync(post));
    }

    [Fact]
    public async Task Post_PerUnitWithoutCount_RoundTripsWithANullCount()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.InvestmentAccount);
        using var client = factory.CreateClient();

        var term = Fee("Custody · per share", 0.02m, new DateTime(2026, 1, 1));
        term.Interval = Interval.PerUnit;

        var post = await client.PostAsJsonAsync(TermsPath(accountId), term);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var stored = (await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId)))!.Single();
        Assert.Equal(Interval.PerUnit, stored.Interval);
        Assert.Null(stored.IntervalCount);
    }

    [Fact]
    public async Task Post_WeeklyWithACount_RoundTripsBothUnchanged()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount);
        using var client = factory.CreateClient();

        var term = Fee("Cash handling", 3m, new DateTime(2026, 1, 1));
        term.Interval = Interval.Weekly;
        term.IntervalCount = 2;

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(TermsPath(accountId), term)).StatusCode);

        var stored = (await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId)))!.Single();
        Assert.Equal(Interval.Weekly, stored.Interval);
        Assert.Equal(2, stored.IntervalCount);
    }

    [Fact]
    public async Task Post_UsingTheOldBillingPeriodKey_IsAcceptedWithTheIntervalUnset()
    {
        // The break is documented behaviour, not a surprise: `billingPeriod` is REMOVED rather than
        // deprecated, so a client still sending it has the key ignored and the interval left null.
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), new
        {
            label = "Paper statement",
            valueUnit = TermValueUnit.Amount,
            value = 2m,
            billingPeriod = 3,
            effectiveFrom = new DateTime(2026, 1, 1),
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var stored = (await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId)))!.Single();
        Assert.Null(stored.Interval);
    }

    [Fact]
    public async Task Post_ReturnsANonEmptyTermIdMatchingThePersistedRowAndNoAccountTermIdKey()
    {
        // Pins the Mapster convention-mapping risk: AccountTermId -> TermId was renamed on BOTH
        // sides at once, and a one-sided rename would silently yield Guid.Empty on every response.
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), Fee("Annual card fee", 95m, new DateTime(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var created = await post.Content.ReadFromJsonAsync<ExistingTerm>();
        Assert.NotEqual(Guid.Empty, created!.TermId);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var stored = await context.Terms.AsNoTracking().SingleAsync(t => t.AccountId == accountId);
        Assert.Equal(stored.TermId, created.TermId);

        var body = await post.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accountTermId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("billingPeriod", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_CarriesALocationHeaderResolvedFromTheRenamedRouteName()
    {
        // A renamed [Http*(Name = ...)] that some call site still names by its old string throws at
        // RUNTIME, not compile time — CreatedAtRoute is the one that would.
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TermsPath(accountId), Fee("Card replacement", 15m, new DateTime(2026, 1, 1)));

        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.NotNull(post.Headers.Location);
        Assert.Contains(TermsPath(accountId), post.Headers.Location!.ToString());
    }

    // ── Cadence: the interval count rules (AC 6-11) ───────────────────────────

    [Fact]
    public async Task Post_MonthlyWithACountOfThree_RoundTripsBothUnchanged()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount);
        using var client = factory.CreateClient();

        var term = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        term.Interval = Interval.Monthly;
        term.IntervalCount = 3;

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(TermsPath(accountId), term)).StatusCode);

        var stored = (await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId)))!.Single();
        Assert.Equal(Interval.Monthly, stored.Interval);
        Assert.Equal(3, stored.IntervalCount);
    }

    [Fact]
    public async Task Post_PeriodicIntervalWithoutACount_StoresTheIdentityCadence()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount);
        using var client = factory.CreateClient();

        var term = Fee("Paper statement", 2m, new DateTime(2026, 1, 1));
        term.Interval = Interval.Monthly;

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(TermsPath(accountId), term)).StatusCode);

        var stored = (await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId)))!.Single();
        Assert.Equal(1, stored.IntervalCount);
    }

    [Theory]
    [InlineData(Interval.PerOccurrence)]
    [InlineData(Interval.OneTime)]
    [InlineData(Interval.PerUnit)]
    [InlineData(null)]
    public async Task Post_CountOnANonPeriodicInterval_ReturnsBadRequest(Interval? interval)
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var term = Fee("ATM · abroad", 25m, new DateTime(2026, 1, 1));
        term.Interval = interval;
        term.IntervalCount = 2;

        var post = await client.PostAsJsonAsync(TermsPath(accountId), term);

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Contains("periodic interval", await post.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Post_CountOutsideItsRange_ReturnsBadRequestKeyedOnIntervalCount(int count)
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount);
        using var client = factory.CreateClient();

        var term = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        term.Interval = Interval.Monthly;
        term.IntervalCount = count;

        var post = await client.PostAsJsonAsync(TermsPath(accountId), term);

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Contains("IntervalCount", await ErrorKeysAsync(post));
    }

    /// <summary>
    /// A percentage term is an ordinary term: the cadence and the anchor the retired rate kinds
    /// refused are accepted on it, on an account type that used to be ineligible for a rate at all.
    /// </summary>
    [Fact]
    public async Task Post_APercentageTermWithACadenceAndAnchor_IsAcceptedOnAnyAccountType()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.Cash);
        using var client = factory.CreateClient();

        var term = InterestRate(0.0325m, new DateTime(2026, 1, 1));
        term.Interval = Interval.Annually;
        term.AnchorDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var post = await client.PostAsJsonAsync(TermsPath(accountId), term);

        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var stored = Assert.Single((await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId)))!);
        Assert.Equal("Interest rate", stored.Label);
        Assert.Equal(Interval.Annually, stored.Interval);
    }

    // ── Cadence: the anchor date rules (AC 12-16) ─────────────────────────────

    [Theory]
    [InlineData("2026-01-15")]  // arrears — the motivating case
    [InlineData("2025-12-20")]  // prepaid
    [InlineData("2026-01-01")]  // equal
    public async Task Post_AnchorDateInEitherOrder_IsAcceptedAndRoundTrips(string anchor)
    {
        // No ordering constraint in either direction: billed in arrears and prepaid are both
        // legitimate records, so the server imposes no relationship (AC 12, spec 8.2 rule 6).
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount);
        using var client = factory.CreateClient();

        var effectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var anchorDate = DateTime.SpecifyKind(DateTime.Parse(anchor), DateTimeKind.Utc);

        var term = Fee("Account maintenance", 45m, effectiveFrom);
        term.AnchorDate = anchorDate;

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(TermsPath(accountId), term)).StatusCode);

        var stored = (await client.GetFromJsonAsync<List<ExistingTerm>>(TermsPath(accountId)))!.Single();
        Assert.Equal(anchorDate, stored.AnchorDate);
        Assert.Equal(effectiveFrom, stored.EffectiveFrom);
    }

    [Theory]
    [InlineData(Interval.OneTime)]
    [InlineData(null)]
    public async Task Post_AnchorDateOnANonPeriodicFee_IsAccepted(Interval? interval)
    {
        // The rule is KIND-based, not interval-based: a one-time fee charged on a known date is
        // precisely a case worth recording (AC 14).
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CreditCard);
        using var client = factory.CreateClient();

        var term = Fee("Card replacement", 15m, new DateTime(2026, 1, 1));
        term.Interval = interval;
        term.AnchorDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(TermsPath(accountId), term)).StatusCode);
    }

    [Fact]
    public async Task Post_UnspecifiedKindAnchorDate_IsStoredAsUtcAlongsideEffectiveFrom()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount);
        using var client = factory.CreateClient();

        var term = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
        term.AnchorDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(TermsPath(accountId), term)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var stored = await context.Terms.AsNoTracking().SingleAsync(t => t.AccountId == accountId);

        // Both dates go through the SAME normalization, so one row cannot carry two kinds.
        Assert.Equal(DateTimeKind.Utc, stored.EffectiveFrom.Kind);
        Assert.Equal(DateTimeKind.Utc, stored.AnchorDate!.Value.Kind);
        Assert.Equal(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), stored.AnchorDate);
    }

    [Fact]
    public async Task Post_SameSeriesAndDateWithDifferentAnchors_StillConflicts()
    {
        // An anchor date does not make two entries distinct: it enters neither the series key nor
        // the duplicate guard (AC 16).
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount);
        using var client = factory.CreateClient();

        var first = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        first.AnchorDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(TermsPath(accountId), first)).StatusCode);

        var second = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        second.AnchorDate = new DateTime(2026, 2, 20, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(TermsPath(accountId), second)).StatusCode);
    }

    [Fact]
    public async Task Current_ResolvesOnEffectiveFromEvenWhenTheAnchorIsStillInTheFuture()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount);
        using var client = factory.CreateClient();

        var term = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        term.AnchorDate = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await client.PostAsJsonAsync(TermsPath(accountId), term);

        var current = await client.GetFromJsonAsync<List<CurrentTerm>>($"{CurrentPath(accountId)}?asOf=2026-06-01");

        Assert.Single(current!);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), current![0].AnchorDate);
    }

    // ── Downstream: the account record's embedded projection (AC 35) ──────────

    [Fact]
    public async Task AccountRecord_CarriesTheCadenceAndAnchorOnItsCurrentTerms()
    {
        await using var factory = new ApiFactory(
            [PermissionClaims.AccountsTermsRead, PermissionClaims.AccountsTermsWrite, PermissionClaims.AccountsRead]);
        var accountId = await SeedAccountAsync(factory, DtoAccountType.CheckingAccount);
        using var client = factory.CreateClient();

        var term = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        term.Interval = Interval.Monthly;
        term.IntervalCount = 3;
        term.AnchorDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        await client.PostAsJsonAsync(TermsPath(accountId), term);

        var response = await client.GetAsync($"/api/accounts/{accountId}");
        var account = await response.Content.ReadFromJsonAsync<ExistingAccount>();

        var current = Assert.Single(account!.CurrentTerms);
        Assert.Equal(Interval.Monthly, current.Interval);
        Assert.Equal(3, current.IntervalCount);
        Assert.Equal(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), current.AnchorDate);

        Assert.DoesNotContain("billingPeriod", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The ProblemDetails <c>errors</c> keys, which is where model validation reports.</summary>
    private static async Task<IReadOnlyCollection<string>> ErrorKeysAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        return problem is null ? [] : [.. problem.Errors.Keys];
    }

    private static readonly string[] WriteAndRead =
        [PermissionClaims.AccountsTermsRead, PermissionClaims.AccountsTermsWrite];

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedAccountAsync(
        WebApplicationFactory<Program> factory,
        DtoAccountType accountType = DtoAccountType.SavingsAccount,
        string currencyCode = "USD")
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = "Test",
            Description = "Test account",
            Opened = DateTime.UtcNow,
            AccountType = (Odyssey.Context.AccountType)(int)accountType,
            CurrencyCode = currencyCode,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
