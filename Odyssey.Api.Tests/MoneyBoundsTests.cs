using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using Context = Odyssey.Context;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #240: every money amount and exchange rate a request carries is bounded by the column it is
/// stored in. Before this a value past the column's precision failed in MariaDB as a <c>500</c>, and a
/// rate below the rate column's smallest step was stored as zero.
/// </summary>
/// <remarks>
/// Three layers: the <see cref="MoneyBounds"/> constants against the EF model (so neither side can
/// move alone), the <c>[Range]</c> on every write DTO against the constants (so a new money property
/// cannot ship unbounded), and the HTTP boundary itself (so the bound really yields a <c>400</c>).
/// </remarks>
public class MoneyBoundsTests
{
    private const string ActorUserId = "money-bounds-actor";

    private static readonly string[] Claims =
    [
        PermissionClaims.TransactionsRead, PermissionClaims.TransactionsCreate,
        PermissionClaims.TaxesRead, PermissionClaims.TaxesCreate,
        PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesCreate, PermissionClaims.ExchangeRatesUpdate,
        PermissionClaims.CurrenciesRead,
        PermissionClaims.FileAnalysisRead, PermissionClaims.FileAnalysisImport,
    ];

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    // ── The constants against the EF model ────────────────────────────────────

    // Every column a request DTO writes a money amount into.
    private static readonly (Type Entity, string Property)[] AmountColumns =
    [
        (typeof(Context.Transaction), nameof(Context.Transaction.Amount)),
        (typeof(Context.BudgetItem), nameof(Context.BudgetItem.PlannedAmount)),
        (typeof(Context.AccountEstimate), nameof(Context.AccountEstimate.Value)),
        (typeof(Context.PropertyEstimate), nameof(Context.PropertyEstimate.Value)),
        (typeof(Context.Term), nameof(Context.Term.Value)),
        (typeof(Context.TaxStatement), nameof(Context.TaxStatement.DeclaredTotalAssets)),
        (typeof(Context.TaxStatement), nameof(Context.TaxStatement.DeclaredTotalLiabilities)),
        (typeof(Context.TaxStatement), nameof(Context.TaxStatement.DeclaredNetWorth)),
        (typeof(Context.TaxStatement), nameof(Context.TaxStatement.DeclaredTotalIncome)),
        (typeof(Context.TaxStatement), nameof(Context.TaxStatement.AssessedTax)),
        (typeof(Context.TaxStatement), nameof(Context.TaxStatement.SettlementAmount)),
    ];

    private static readonly (Type Entity, string Property)[] RateColumns =
    [
        (typeof(Context.ExchangeRate), nameof(Context.ExchangeRate.Rate)),
    ];

    // Decimal columns deliberately outside MoneyBounds, each with its reason. A new decimal column must
    // land in one of the three lists, which forces the question "does a request write this?" to be asked.
    private static readonly (Type Entity, string Property)[] OtherDecimalColumns =
    [
        // Written from the analyzer's parsed output, never from a request body; the import request's
        // override amount is bounded instead (ImportCandidateRequest.Amount).
        (typeof(Context.FileAnalysisCandidateTransaction), nameof(Context.FileAnalysisCandidateTransaction.Amount)),
        // Confidences in [0, 1], decimal(5,4) — not money.
        (typeof(Context.FileAnalysisCandidateTransaction), nameof(Context.FileAnalysisCandidateTransaction.LlmConfidence)),
        (typeof(Context.FileAnalysisCandidateTransaction), nameof(Context.FileAnalysisCandidateTransaction.MerchantMatchConfidence)),
        (typeof(Context.FileAnalysisCandidateTransaction), nameof(Context.FileAnalysisCandidateTransaction.CategoryMatchConfidence)),
        (typeof(Context.FileAnalysisJob), nameof(Context.FileAnalysisJob.AutoLinkThresholdInForce)),
        // Areas, decimal(18,2), already bounded by [Range(0, 1_000_000)] on RealEstateDetailsDto.
        (typeof(Context.RealEstateDetails), nameof(Context.RealEstateDetails.LivingAreaSqm)),
        (typeof(Context.RealEstateDetails), nameof(Context.RealEstateDetails.PlotAreaSqm)),
    ];

    private static OdysseyContext CreateContext() => new(
        new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(nameof(MoneyBoundsTests)).Options);

    private static Microsoft.EntityFrameworkCore.Metadata.IProperty ModelProperty(
        OdysseyContext context, Type entity, string property)
    {
        var entityType = context.Model.FindEntityType(entity)
            ?? throw new InvalidOperationException($"{entity.Name} is not in the model.");
        return entityType.FindProperty(property)
            ?? throw new InvalidOperationException($"{entity.Name}.{property} is not in the model.");
    }

    [Fact]
    public void AmountColumns_HaveTheAmountPrecision()
    {
        using var context = CreateContext();
        foreach (var (entity, property) in AmountColumns)
        {
            var column = ModelProperty(context, entity, property);
            Assert.Equal(
                $"{entity.Name}.{property} decimal({MoneyBounds.AmountPrecision},{MoneyBounds.AmountScale})",
                $"{entity.Name}.{property} decimal({column.GetPrecision()},{column.GetScale()})");
        }
    }

    [Fact]
    public void RateColumns_HaveTheRatePrecision()
    {
        using var context = CreateContext();
        foreach (var (entity, property) in RateColumns)
        {
            var column = ModelProperty(context, entity, property);
            Assert.Equal(
                $"{entity.Name}.{property} decimal({MoneyBounds.ExchangeRatePrecision},{MoneyBounds.ExchangeRateScale})",
                $"{entity.Name}.{property} decimal({column.GetPrecision()},{column.GetScale()})");
        }
    }

    [Fact]
    public void EveryDecimalColumnInTheModel_IsClassified()
    {
        using var context = CreateContext();
        var classified = AmountColumns.Concat(RateColumns).Concat(OtherDecimalColumns)
            .Select(c => $"{c.Entity.Name}.{c.Property}")
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = context.Model.GetEntityTypes()
            .SelectMany(e => e.GetDeclaredProperties())
            .Where(p => Nullable.GetUnderlyingType(p.ClrType) == typeof(decimal) || p.ClrType == typeof(decimal))
            .Select(p => $"{p.DeclaringType.ClrType.Name}.{p.Name}")
            .Where(name => !classified.Contains(name))
            .ToList();

        // Each entry is a decimal column not yet listed as amount, rate or other (issue #240).
        Assert.Empty(unclassified);
    }

    // The largest magnitude decimal(p,s) holds: 10^(p-s) - 10^-s.
    private static decimal ColumnMax(int precision, int scale)
    {
        var step = 1m;
        for (var i = 0; i < scale; i++)
        {
            step /= 10m;
        }

        var whole = 1m;
        for (var i = 0; i < precision - scale; i++)
        {
            whole *= 10m;
        }

        return whole - step;
    }

    private static decimal ColumnStep(int scale)
    {
        var step = 1m;
        for (var i = 0; i < scale; i++)
        {
            step /= 10m;
        }

        return step;
    }

    private static decimal Parse(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    [Fact]
    public void AmountLimits_AreDerivedFromTheAmountPrecision()
    {
        var max = ColumnMax(MoneyBounds.AmountPrecision, MoneyBounds.AmountScale);
        Assert.Equal(max, Parse(MoneyBounds.AmountMax));
        Assert.Equal(-max, Parse(MoneyBounds.AmountMin));
    }

    [Fact]
    public void RateLimits_AreDerivedFromTheRatePrecision()
    {
        Assert.Equal(ColumnMax(MoneyBounds.ExchangeRatePrecision, MoneyBounds.ExchangeRateScale), Parse(MoneyBounds.ExchangeRateMax));
        Assert.Equal(ColumnStep(MoneyBounds.ExchangeRateScale), Parse(MoneyBounds.ExchangeRateMin));
        Assert.Equal(0.00000001m, Parse(MoneyBounds.ExchangeRateMin));
    }

    // ── The [Range] on every write DTO against the constants ──────────────────

    // Discovered automatically: every decimal a request body can carry, found by walking every write-side
    // DTO in Odyssey.Dtos (New*/Update*/Put*/*Update/*Request) and every Odyssey.Dtos type nested inside one. A
    // positional record's attribute sits on its constructor parameter, so that is read too.

    // The only rate members; every other discovered decimal is a money amount unless listed below.
    private static readonly HashSet<string> RateMembers = new(StringComparer.Ordinal)
    {
        $"{nameof(NewExchangeRate)}.{nameof(NewExchangeRate.Rate)}",
        $"{nameof(UpdateExchangeRate)}.{nameof(UpdateExchangeRate.Rate)}",
    };

    // Write-side decimals that are neither a money amount nor a rate, each with its reason. Anything a
    // request can carry as a decimal that is not here must name the MoneyBounds [Range].
    private static readonly Dictionary<string, string> DecimalMembersOutsideMoneyBounds = new(StringComparer.Ordinal)
    {
        [$"{nameof(RealEstateDetailsDto)}.{nameof(RealEstateDetailsDto.LivingAreaSqm)}"] =
            "An area in square metres, decimal(18,2); bounded by its own [Range(0, 1_000_000)].",
        [$"{nameof(RealEstateDetailsDto)}.{nameof(RealEstateDetailsDto.PlotAreaSqm)}"] =
            "An area in square metres, decimal(18,2); bounded by its own [Range(0, 1_000_000)].",
        [$"{nameof(Odyssey.Dtos.Application.SystemSettingsUpdate)}.{nameof(Odyssey.Dtos.Application.SystemSettingsUpdate.FileAnalysisMatchAutoLinkThreshold)}"] =
            "A confidence threshold in [0, 1], stored as a setting string; bounded by its own [Range].",
    };

    private sealed record DecimalMember(string Name, RangeAttribute? Range);

    private static bool IsWriteSideDto(Type type) =>
        type.Name.StartsWith("New", StringComparison.Ordinal)
        || type.Name.StartsWith("Update", StringComparison.Ordinal)
        || type.Name.StartsWith("Put", StringComparison.Ordinal)
        || type.Name.EndsWith("Update", StringComparison.Ordinal)
        || type.Name.EndsWith("Request", StringComparison.Ordinal);

    // The Odyssey.Dtos classes a property of this type can hold: itself, or the arguments of a
    // generic collection, or an array's element type.
    private static IEnumerable<Type> DtoTypesWithin(Type type, Assembly dtos)
    {
        if (type.IsArray)
        {
            return DtoTypesWithin(type.GetElementType()!, dtos);
        }

        if (type.IsGenericType)
        {
            return type.GetGenericArguments().SelectMany(argument => DtoTypesWithin(argument, dtos));
        }

        return type.IsClass && type.Assembly == dtos ? [type] : [];
    }

    private static IReadOnlyList<DecimalMember> WriteSideDecimalMembers()
    {
        var dtos = typeof(MoneyBounds).Assembly;
        var pending = new Queue<Type>(dtos.GetTypes().Where(t => t.IsClass && !t.IsAbstract && IsWriteSideDto(t)));
        var seen = new HashSet<Type>();
        var found = new List<DecimalMember>();

        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type))
            {
                continue;
            }

            var constructorParameters = type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Where(parameter => parameter.Name is not null)
                .GroupBy(parameter => parameter.Name!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // A get-only property is computed, never bound from a body.
                if (property.SetMethod is not { IsPublic: true })
                {
                    continue;
                }

                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (propertyType == typeof(decimal))
                {
                    var range = property.GetCustomAttribute<RangeAttribute>()
                        ?? (constructorParameters.TryGetValue(property.Name, out var parameter)
                            ? parameter.GetCustomAttribute<RangeAttribute>()
                            : null);
                    found.Add(new DecimalMember($"{type.Name}.{property.Name}", range));
                    continue;
                }

                foreach (var nested in DtoTypesWithin(propertyType, dtos))
                {
                    pending.Enqueue(nested);
                }
            }
        }

        return found;
    }

    private static string Describe(RangeAttribute? range) => range is null
        ? "no [Range]"
        : $"[Range({range.OperandType?.Name}, {range.Minimum}, {range.Maximum}, "
          + $"MinimumIsExclusive={range.MinimumIsExclusive}, MaximumIsExclusive={range.MaximumIsExclusive}, "
          + $"ParseLimitsInInvariantCulture={range.ParseLimitsInInvariantCulture}, "
          + $"ConvertValueInInvariantCulture={range.ConvertValueInInvariantCulture})]";

    private static string Expected(string min, string max) => Describe(new RangeAttribute(typeof(decimal), min, max)
    {
        ParseLimitsInInvariantCulture = true,
        ConvertValueInInvariantCulture = true,
    });

    [Fact]
    public void EveryWriteSideDecimal_IsBoundedByItsColumn()
    {
        var amount = Expected(MoneyBounds.AmountMin, MoneyBounds.AmountMax);
        var rate = Expected(MoneyBounds.ExchangeRateMin, MoneyBounds.ExchangeRateMax);

        // Each entry names the member, what it should carry and what it carries.
        var violations = WriteSideDecimalMembers()
            .Where(member => !DecimalMembersOutsideMoneyBounds.ContainsKey(member.Name))
            .Select(member => (member.Name, Expected: RateMembers.Contains(member.Name) ? rate : amount, Actual: Describe(member.Range)))
            .Where(member => member.Expected != member.Actual)
            .Select(member => $"{member.Name}: expected {member.Expected}, found {member.Actual}")
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void TheWalk_ReachesTopLevelNestedAndPositionalMembers()
    {
        var found = WriteSideDecimalMembers().Select(member => member.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains($"{nameof(NewTransaction)}.{nameof(NewTransaction.Amount)}", found);
        // Reached through ImportRequest.Candidates, and bound on the positional constructor parameter.
        Assert.Contains($"{nameof(ImportCandidateRequest)}.{nameof(ImportCandidateRequest.Amount)}", found);
        // Reached through NewProperty.RealEstate.
        Assert.Contains($"{nameof(RealEstateDetailsDto)}.{nameof(RealEstateDetailsDto.LivingAreaSqm)}", found);
    }

    [Fact]
    public void EveryListedMember_IsStillAWriteSideDecimal()
    {
        // A list entry that no longer names a discovered member would exempt nothing and hide a rename.
        var found = WriteSideDecimalMembers().Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        var stale = RateMembers.Concat(DecimalMembersOutsideMoneyBounds.Keys).Where(name => !found.Contains(name)).ToList();

        Assert.Empty(stale);
    }

    // ── The attribute's behaviour, including under a comma-decimal culture ────

    private static bool IsValid(object dto) =>
        Validator.TryValidateObject(dto, new ValidationContext(dto), null, validateAllProperties: true);

    private static NewTransaction Transaction(decimal amount) => new()
    {
        Description = "Bound",
        Amount = amount,
        AccountId = Guid.NewGuid(),
    };

    private static UpdateExchangeRate Rate(decimal rate) => new() { Rate = rate, AsOf = DateTime.UtcNow };

    public static TheoryData<decimal, bool> AmountCases => new()
    {
        { 999_999_999_999.999999m, true },
        { -999_999_999_999.999999m, true },
        { 0m, true },
        { 999_999_999_999.9999991m, false },
        { 1_000_000_000_000m, false },
        { -1_000_000_000_000m, false },
    };

    public static TheoryData<decimal, bool> RateCases => new()
    {
        { 0.00000001m, true },
        { 9_999_999_999.99999999m, true },
        { 0.000000001m, false },
        { 0m, false },
        { -1m, false },
        { 10_000_000_000m, false },
    };

    [Theory]
    [MemberData(nameof(AmountCases))]
    public void Amount_IsValidExactlyWithinTheColumn(decimal amount, bool valid)
    {
        Assert.Equal(valid, IsValid(Transaction(amount)));
        Assert.Equal(valid, InCulture("nb-NO", () => IsValid(Transaction(amount))));
    }

    [Theory]
    [MemberData(nameof(RateCases))]
    public void Rate_IsValidExactlyWithinTheColumn(decimal rate, bool valid)
    {
        Assert.Equal(valid, IsValid(Rate(rate)));
        Assert.Equal(valid, InCulture("nb-NO", () => IsValid(Rate(rate))));
    }

    private static T InCulture<T>(string name, Func<T> action)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            return action();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture);
        }
    }

    // ── The HTTP boundary ─────────────────────────────────────────────────────

    private static async Task EnsureReferenceDataAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
    }

    private static async Task AssertBadRequestOn(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(field, problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    private static NewTaxStatement TaxStatement(decimal amount) => new()
    {
        Name = "Bound",
        FiscalYear = 2024,
        StartDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        BaseCurrencyCode = "USD",
        DeclaredTotalAssets = amount,
    };

    private static NewExchangeRate NewRate(decimal rate) => new()
    {
        FromCurrencyCode = "USD",
        ToCurrencyCode = "EUR",
        Rate = rate,
    };

    [Fact]
    public async Task CreateTransaction_AmountPastTheColumn_Returns400()
    {
        await using var factory = new ApiFactory(Claims);
        using var client = factory.CreateClient();

        // The annotation 400 precedes the account lookup, so no account needs seeding.
        var response = await client.PostAsJsonAsync("/api/transactions", Transaction(1_000_000_000_000m));

        await AssertBadRequestOn(response, nameof(NewTransaction.Amount));
    }

    [Fact]
    public async Task CreateTaxStatement_AmountAtTheColumnMax_IsAccepted()
    {
        await using var factory = new ApiFactory(Claims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/tax-statements", TaxStatement(999_999_999_999.999999m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ExistingTaxStatement>();
        Assert.Equal(999_999_999_999.999999m, created!.DeclaredTotalAssets);
    }

    [Fact]
    public async Task CreateTaxStatement_AmountJustPastTheColumnMax_Returns400()
    {
        await using var factory = new ApiFactory(Claims);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/tax-statements", TaxStatement(1_000_000_000_000m));

        await AssertBadRequestOn(response, nameof(NewTaxStatement.DeclaredTotalAssets));
    }

    [Theory]
    [InlineData("0.00000001")]
    [InlineData("9999999999.99999999")]
    public async Task CreateExchangeRate_RateAtTheColumnBounds_IsAccepted(string rate)
    {
        await using var factory = new ApiFactory(Claims);
        await EnsureReferenceDataAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/exchange-rates", NewRate(Parse(rate)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ExistingExchangeRate>();
        Assert.Equal(Parse(rate), created!.Rate);
    }

    [Theory]
    [InlineData("0.000000001")]      // below the column's smallest step: was stored as 0
    [InlineData("0.000000004")]
    [InlineData("10000000000")]      // past decimal(18,8): was a 500
    public async Task CreateExchangeRate_RateOutsideTheColumn_Returns400(string rate)
    {
        await using var factory = new ApiFactory(Claims);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/exchange-rates", NewRate(Parse(rate)));

        await AssertBadRequestOn(response, nameof(NewExchangeRate.Rate));
    }

    private static ImportRequest Import(decimal amount) =>
        new([new ImportCandidateRequest(Guid.NewGuid(), null, null, amount, null)]);

    [Fact]
    public async Task ImportCandidates_AmountOverridePastTheColumn_Returns400()
    {
        await using var factory = new ApiFactory(Claims);
        using var client = factory.CreateClient();

        // The attribute sits on a positional record's constructor parameter; this proves MVC honours it.
        var response = await client.PostAsJsonAsync($"/api/file-analysis/{Guid.NewGuid()}/import", Import(1_000_000_000_000m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(problem!.Errors.Keys, key => key.EndsWith(nameof(ImportCandidateRequest.Amount), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportCandidates_AmountOverrideAtTheColumnMax_PassesModelValidation()
    {
        await using var factory = new ApiFactory(Claims);
        using var client = factory.CreateClient();

        // The control for the case above: the same body within bounds gets past model validation and
        // reaches the service, which answers 503 because file analysis is disabled by default.
        var response = await client.PostAsJsonAsync($"/api/file-analysis/{Guid.NewGuid()}/import", Import(999_999_999_999.999999m));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task UpdateExchangeRate_RateBelowTheColumnStep_Returns400()
    {
        await using var factory = new ApiFactory(Claims);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/exchange-rates/{Guid.NewGuid()}", Rate(0.000000001m));

        await AssertBadRequestOn(response, nameof(UpdateExchangeRate.Rate));
    }
}
