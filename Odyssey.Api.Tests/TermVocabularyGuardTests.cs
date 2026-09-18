using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Odyssey.Api.Controllers;
using Odyssey.Api.DataExport;
using Odyssey.Dtos.Authorization;
using Xunit;
using ContextInterval = Odyssey.Context.Interval;
using DtoInterval = Odyssey.Dtos.Finance.Interval;

namespace Odyssey.Api.Tests;

/// <summary>
/// The build-time half of issue #120: the shape of the two cadence enums, the absence of the
/// vocabulary this change retired, and the claim gates that had to survive a controller rename.
/// </summary>
public class TermVocabularyGuardTests
{
    private static readonly Assembly[] SolutionAssemblies =
    [
        typeof(Odyssey.Context.Term).Assembly,
        typeof(Odyssey.Dtos.Finance.NewTerm).Assembly,
        typeof(Odyssey.Core.Finance.TermService).Assembly,
        typeof(TermsController).Assembly,
    ];

    /// <summary>
    /// AC 1 / AC 20 — both cadence enums pinned by LITERAL ordinal, in both directions.
    /// </summary>
    /// <remarks>
    /// After this change <c>Odyssey.Dtos.Finance</c> holds two cadence enums whose member names
    /// overlap while no ordinal does — adding <c>Weekly</c> made that worse, not better, since the
    /// two now cover the same four periodic units. A mistaken cast or a copy-pasted literal between
    /// them changes meaning silently rather than failing to compile, so the numbers are pinned here:
    /// a later edit that "aligns" one to the other fails the build instead.
    /// </remarks>
    [Fact]
    public void BothCadenceEnums_KeepTheirOrdinals()
    {
        Assert.Equal(0, (int)DtoInterval.OneTime);
        Assert.Equal(1, (int)DtoInterval.PerOccurrence);
        Assert.Equal(2, (int)DtoInterval.Daily);
        Assert.Equal(3, (int)DtoInterval.Monthly);
        Assert.Equal(5, (int)DtoInterval.Annually);
        Assert.Equal(6, (int)DtoInterval.PerUnit);
        Assert.Equal(7, (int)DtoInterval.Weekly);

        Assert.Equal(0, (int)Odyssey.Dtos.Finance.BillingInterval.Daily);
        Assert.Equal(1, (int)Odyssey.Dtos.Finance.BillingInterval.Weekly);
        Assert.Equal(2, (int)Odyssey.Dtos.Finance.BillingInterval.Monthly);
        Assert.Equal(3, (int)Odyssey.Dtos.Finance.BillingInterval.Yearly);
    }

    /// <summary>
    /// AC 1 — ordinal 4 is RETIRED, and <c>Weekly</c> deliberately does not fill it.
    /// </summary>
    /// <remarks>
    /// A row still holding 4 that the migration never reached must fail as an undefined value, not
    /// silently become whatever took the slot: reusing the ordinal converts a <c>400</c> into silent
    /// data corruption. The out-of-sequence numbering is the price, and it is worth it.
    /// </remarks>
    [Fact]
    public void TheRetiredQuarterlyOrdinal_IsDefinedOnNeitherCopyOfTheIntervalEnum()
    {
        Assert.False(Enum.IsDefined((DtoInterval)4));
        Assert.False(Enum.IsDefined((ContextInterval)4));
    }

    /// <summary>AC 1 — the two copies of the enum are identical, name for name and value for value.</summary>
    [Fact]
    public void TheTwoCopiesOfTheIntervalEnum_AreIdentical()
    {
        static Dictionary<string, int> Shape<TEnum>() where TEnum : struct, Enum =>
            Enum.GetValues<TEnum>().ToDictionary(v => v.ToString()!, v => Convert.ToInt32(v));

        Assert.Equal(Shape<DtoInterval>(), Shape<ContextInterval>());
        Assert.Equal(7, Shape<DtoInterval>().Count);
    }

    /// <summary>
    /// AC 1 / AC 22 — the retired vocabulary is gone from the solution, and the one name that was
    /// deliberately kept is still there.
    /// </summary>
    [Theory]
    [InlineData("BillingPeriod")]
    [InlineData("AccountTerm")]
    [InlineData("NewAccountTerm")]
    [InlineData("ExistingAccountTerm")]
    [InlineData("CurrentAccountTerm")]
    [InlineData("AccountTermService")]
    [InlineData("AccountTermsController")]
    [InlineData("AccountTermExport")]
    public void TheRenamedTypeNames_AreGoneFromTheSolution(string retired)
    {
        var survivors = SolutionAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Name == retired)
            .Select(type => type.FullName)
            .ToList();

        Assert.Empty(survivors);
    }

    /// <summary>
    /// AC 22 — <c>AccountCurrentTerm</c> is NOT renamed. With <c>CurrentAccountTerm</c> becoming
    /// <c>CurrentTerm</c>, the surviving <c>Account</c> prefix stops being noise and starts doing
    /// real work: it is the one name meaning "the current term as carried on the account record".
    /// </summary>
    [Fact]
    public void AccountCurrentTerm_KeepsItsName()
    {
        Assert.NotNull(typeof(Odyssey.Dtos.Finance.AccountCurrentTerm));
        Assert.NotNull(typeof(Odyssey.Dtos.Finance.ExistingAccount).GetProperty("CurrentTerms"));
    }

    /// <summary>
    /// AC 19 — the claim VALUES survived the rename. They are persisted in <c>AspNetRoleClaims</c>
    /// and baked into live auth cookies, so renaming one would de-authorize every existing session
    /// and seeded role row for no functional gain. A rename pass that "completes the job" by
    /// touching these is a security regression, not a tidy-up.
    /// </summary>
    [Fact]
    public void TheTermClaimValues_AreUnchangedByTheRename()
    {
        Assert.Equal("accounts.terms.read", PermissionClaims.AccountsTermsRead);
        Assert.Equal("accounts.terms.write", PermissionClaims.AccountsTermsWrite);
    }

    /// <summary>
    /// AC 44 — every public action on the renamed controller still carries an
    /// <c>[Authorize(Policy = ...)]</c> naming a <c>PermissionClaims</c> constant.
    /// </summary>
    /// <remarks>
    /// The cheap companion to the full <c>403</c> matrix. The failure a rename risks is not
    /// anonymous access — a global <c>RequireAuthenticatedUser</c> fallback is configured — but a
    /// downgrade to "any authenticated caller", which is quieter and correspondingly easier to miss.
    /// </remarks>
    [Fact]
    public void EveryTermsControllerAction_IsGatedOnAPermissionClaim()
    {
        var claimValues = typeof(PermissionClaims)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var actions = typeof(TermsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .ToList();

        Assert.Equal(5, actions.Count);

        foreach (var action in actions)
        {
            var policy = action.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

            Assert.True(policy is not null, $"{action.Name} carries no [Authorize(Policy = ...)].");
            Assert.Contains(policy!, claimValues);
        }
    }

    /// <summary>
    /// AC 24 — the route NAMES moved with the controller, and nothing still names an old one. A
    /// <c>CreatedAtRoute</c> against a stale name throws at runtime, not compile time.
    /// </summary>
    [Fact]
    public void TheTermRouteNames_AreTheRenamedOnes()
    {
        var names = typeof(TermsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .Select(attribute => attribute.Name)
            .ToList();

        Assert.Equal(
            ["DeleteTerm", "GetCurrentTerms", "GetTerms", "PostTerm", "PutTerm"],
            names.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// AC 36 / AC 38 — the export document carries the renamed property and the three new fields, so
    /// the whole-database export moves its term rows from the <c>accountTerms</c> key to <c>terms</c>.
    /// </summary>
    [Fact]
    public void TheExportDocument_CarriesTheRenamedTermCollection()
    {
        Assert.NotNull(typeof(FinanceDatabaseExport).GetProperty("Terms"));
        Assert.Null(typeof(FinanceDatabaseExport).GetProperty("AccountTerms"));

        Assert.Equal(typeof(Guid), typeof(TermExport).GetProperty("TermId")!.PropertyType);
        Assert.Equal(typeof(DtoInterval?), typeof(TermExport).GetProperty("Interval")!.PropertyType);
        Assert.Equal(typeof(int?), typeof(TermExport).GetProperty("IntervalCount")!.PropertyType);
        Assert.Equal(typeof(DateTime?), typeof(TermExport).GetProperty("AnchorDate")!.PropertyType);
        Assert.Null(typeof(TermExport).GetProperty("AccountTermId"));
    }
}
