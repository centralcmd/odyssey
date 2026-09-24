using System.Text.RegularExpressions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #167 §4 / AC 29: <see cref="EstimateEffectiveDating"/> is the single home of the two query rules
/// every estimate table obeys. The behavioural tests pin the rules themselves — and that the SQL-side
/// selector and the in-memory <see cref="EffectiveDatedExtensions"/> agree — and the source-lint pins
/// that no estimate service grows a second copy, which is what keeps the anti-divergence argument true
/// after merge rather than only at it.
/// </summary>
public class EstimateEffectiveDatingTests
{
    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<Guid> SeedProperty(OdysseyContext context)
    {
        var property = new Property
        {
            Name = "Storgata 14",
            Description = string.Empty,
            Type = Odyssey.Dtos.Finance.PropertyType.RealEstate,
            CurrencyCode = "NOK",
            RealEstateDetails = new RealEstateDetails { Kind = Odyssey.Dtos.Finance.RealEstateKind.House },
        };
        context.Properties.Add(property);
        await context.SaveChangesAsync();
        return property.PropertyId;
    }

    private static PropertyEstimate Row(Guid propertyId, decimal value, DateTime effectiveFrom, DateTime created) => new()
    {
        PropertyId = propertyId,
        Value = value,
        CurrencyCode = "NOK",
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = created,
    };

    [Fact]
    public async Task ResolveCurrent_TieOnEffectiveFrom_IsBrokenByTheMostRecentlyCreatedRow()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = await SeedProperty(context);
        // The pre-existing check-then-act race (§9) can land two rows on one date; the read must still
        // resolve deterministically, and the same way on every surface.
        context.PropertyEstimates.AddRange(
            Row(propertyId, 1m, Jan1, Jan1.AddHours(2)),
            Row(propertyId, 2m, Jan1, Jan1.AddHours(1)));
        await context.SaveChangesAsync();

        var current = await EstimateEffectiveDating.ResolveCurrentAsync(
            context.PropertyEstimates.Where(e => e.PropertyId == propertyId), Jan1);

        Assert.Equal(1m, current!.Value);
    }

    [Fact]
    public async Task HasConflict_MatchesTheExactInstantOnly()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = await SeedProperty(context);
        context.PropertyEstimates.Add(Row(propertyId, 1m, Jan1, Jan1));
        await context.SaveChangesAsync();
        var scoped = context.PropertyEstimates.Where(e => e.PropertyId == propertyId);

        Assert.True(await EstimateEffectiveDating.HasConflictAsync(scoped, Jan1));
        Assert.False(await EstimateEffectiveDating.HasConflictAsync(scoped, Jan1.AddSeconds(1)));
    }

    /// <summary>
    /// The list's value sort runs <see cref="EstimateEffectiveDating.CurrentValue{TOwner,T}"/> as a
    /// correlated subquery; the account roll-ups materialize and use <see cref="EffectiveDatedExtensions"/>.
    /// The two restate one ordering, so they are compared directly over data with ties, future rows and
    /// an owner with nothing in force.
    /// </summary>
    [Fact]
    public async Task CurrentValue_AgreesWithTheInMemoryResolution()
    {
        await using var context = TestContextFactory.Create();
        var a = await SeedProperty(context);
        var b = await SeedProperty(context);
        var c = await SeedProperty(context);
        context.PropertyEstimates.AddRange(
            Row(a, 10m, Jan1, Jan1),
            Row(a, 20m, Jan1.AddMonths(1), Jan1),
            Row(a, 30m, Jan1.AddMonths(1), Jan1.AddMinutes(1)),
            Row(a, 99m, Jan1.AddYears(5), Jan1),
            Row(b, 5m, Jan1.AddDays(-3), Jan1),
            Row(c, 7m, Jan1.AddYears(5), Jan1));
        await context.SaveChangesAsync();
        var cutoff = Jan1.AddMonths(2);

        var selector = EstimateEffectiveDating.CurrentValue<Property, PropertyEstimate>(
            p => p.Estimates, e => e.Value, cutoff);
        var viaQuery = context.Properties.Select(selector).ToList();

        var inMemory = context.PropertyEstimates.ToList()
            .Where(e => e.EffectiveFrom <= cutoff)
            .GroupBy(e => e.PropertyId)
            .ToDictionary(g => g.Key, g => g.MostEffective()!.Value);

        Assert.Equal(30m, inMemory[a]);
        foreach (var property in context.Properties.ToList())
        {
            var expected = inMemory.TryGetValue(property.PropertyId, out var value) ? value : (decimal?)null;
            Assert.Equal(expected, context.Properties.Where(p => p.PropertyId == property.PropertyId).Select(selector).Single());
        }

        Assert.Equal(3, viaQuery.Count);
        Assert.Contains(null, viaQuery);
    }

    // ── AC 29: the source-lint ────────────────────────────────────────────────

    private static readonly Regex SupersessionOrdering =
        new(@"OrderByDescending\(\s*(\w+)\s*=>\s*\1\.EffectiveFrom\b", RegexOptions.Compiled);

    private static readonly Regex DuplicateDateCheck =
        new(@"\.EffectiveFrom\s*==", RegexOptions.Compiled);

    private static readonly Regex TouchesAnEstimateTable =
        new(@"\b(AccountEstimates?|PropertyEstimates?)\b", RegexOptions.Compiled);

    private const string HelperFile = "EstimateEffectiveDating.cs";

    /// <summary>
    /// Scoped to files in <c>Odyssey.Core/Finance/</c> that touch an estimate table, so <c>TermService</c>
    /// — a different rule set over a different table — is not swept in. <c>EffectiveDatedExtensions</c> is
    /// the sanctioned in-memory counterpart and mentions no estimate table, so it is outside the scan by
    /// the same rule rather than by an exemption.
    /// </summary>
    private static IReadOnlyList<string> ScannedFiles() =>
        Directory.EnumerateFiles(Path.Combine(SolutionRoot(), "Odyssey.Core", "Finance"), "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != HelperFile)
            .Where(path => TouchesAnEstimateTable.IsMatch(StripComments(File.ReadAllText(path))))
            .ToList();

    [Fact]
    public void The_lint_scans_both_estimate_services_and_fires_on_the_helper_itself()
    {
        // A lint whose scan set silently emptied — a moved folder, a renamed table — would pass forever.
        var names = ScannedFiles().Select(Path.GetFileName).ToList();
        Assert.Contains("AccountEstimateService.cs", names);
        Assert.Contains("PropertyEstimateService.cs", names);
        Assert.Contains("PropertyService.cs", names);

        // And the patterns are live: the one sanctioned home matches both.
        var helper = StripComments(File.ReadAllText(
            Path.Combine(SolutionRoot(), "Odyssey.Core", "Finance", HelperFile)));
        Assert.Matches(SupersessionOrdering, helper);
        Assert.Matches(DuplicateDateCheck, helper);
    }

    [Fact]
    public void No_estimate_file_restates_either_query_rule_outside_the_helper()
    {
        var offenders = ScannedFiles()
            .Select(path => (Path: path, Code: StripComments(File.ReadAllText(path))))
            .Where(file => SupersessionOrdering.IsMatch(file.Code) || DuplicateDateCheck.IsMatch(file.Code))
            .Select(file => Path.GetFileName(file.Path))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These files restate an estimate query rule instead of calling EstimateEffectiveDating: "
            + string.Join(", ", offenders));
    }

    private static string StripComments(string code) =>
        Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"//.*?$", string.Empty, RegexOptions.Multiline);

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the solution root from the test binary.");
    }
}
