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
        typeof(ContractController).Assembly,
    ];

    /// <summary>
    /// AC 1 / AC 20 — the cadence enum pinned by LITERAL ordinal, in both directions.
    /// </summary>
    /// <remarks>
    /// <c>Odyssey.Dtos.Finance</c> held a second cadence enum until the standalone subscriptions
    /// feature was removed, and the two overlapped by member name while no ordinal did. A mistaken
    /// cast or a copy-pasted literal changes meaning silently rather than failing to compile, so the
    /// numbers stay pinned here: a later edit that renumbers one fails the build instead.
    /// </remarks>
    [Fact]
    public void TheCadenceEnum_KeepsItsOrdinals()
    {
        Assert.Equal(0, (int)DtoInterval.OneTime);
        Assert.Equal(1, (int)DtoInterval.PerOccurrence);
        Assert.Equal(2, (int)DtoInterval.Daily);
        Assert.Equal(3, (int)DtoInterval.Monthly);
        Assert.Equal(5, (int)DtoInterval.Annually);
        Assert.Equal(6, (int)DtoInterval.PerUnit);
        Assert.Equal(7, (int)DtoInterval.Weekly);
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
    /// Issue #159 — the two copies of <c>TermDirection</c> are identical and their ordinals are pinned
    /// by literal. <c>Outgoing = 0</c> in particular is load-bearing twice over: it is the database
    /// default the migration backfills every existing row with, and it is what an omitted
    /// <c>direction</c> on a request deserializes to. Renumbering either copy would silently
    /// reinterpret every stored row and move money to the other side of the household's net.
    /// </summary>
    [Fact]
    public void TheTwoCopiesOfTheTermDirectionEnum_AreIdentical_AtPinnedOrdinals()
    {
        static Dictionary<string, int> Shape<TEnum>() where TEnum : struct, Enum =>
            Enum.GetValues<TEnum>().ToDictionary(v => v.ToString()!, v => Convert.ToInt32(v));

        Assert.Equal(0, (int)Odyssey.Dtos.Finance.TermDirection.Outgoing);
        Assert.Equal(1, (int)Odyssey.Dtos.Finance.TermDirection.Incoming);
        Assert.Equal(0, (int)Odyssey.Context.TermDirection.Outgoing);
        Assert.Equal(1, (int)Odyssey.Context.TermDirection.Incoming);

        Assert.Equal(
            Shape<Odyssey.Dtos.Finance.TermDirection>(),
            Shape<Odyssey.Context.TermDirection>());
        Assert.Equal(2, Shape<Odyssey.Dtos.Finance.TermDirection>().Count);

        // The default of the C# type is the default of the COLUMN, which is what makes the backfill
        // behaviour-preserving without a data migration.
        Assert.Equal(Odyssey.Context.TermDirection.Outgoing, default(Odyssey.Context.TermDirection));
        Assert.Equal(Odyssey.Dtos.Finance.TermDirection.Outgoing, new Odyssey.Dtos.Finance.NewTerm
        {
            ValueUnit = Odyssey.Dtos.Finance.TermValueUnit.Amount,
            Value = 0m,
            EffectiveFrom = default,
        }.Direction);
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
    // Issue #190 — the account-owned term surface and its owner discriminator.
    [InlineData("TermsController")]
    [InlineData("TermOwnerKind")]
    [InlineData("TermOwnerFacts")]
    [InlineData("AccountTermsSection")]
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
    /// AC 22 — <c>AccountCurrentTerm</c> is NOT renamed, even after issue #190 took it off the account
    /// record (its Non-Goal 5): <c>ExistingContract.CurrentTerms</c> still carries it.
    /// </summary>
    [Fact]
    public void AccountCurrentTerm_KeepsItsName()
    {
        Assert.NotNull(typeof(Odyssey.Dtos.Finance.AccountCurrentTerm));
        Assert.Equal(
            typeof(List<Odyssey.Dtos.Finance.AccountCurrentTerm>),
            typeof(Odyssey.Dtos.Finance.ExistingContract).GetProperty("CurrentTerms")!.PropertyType);
    }

    /// <summary>
    /// Issue #190 AC 18 — the account-owned term projections are gone from every wire shape: the
    /// account record carries no term count or current terms, a term names no account, and the export
    /// row has no account column. A contract is the only owner, so its id is not nullable.
    /// </summary>
    [Fact]
    public void NoWireShape_CarriesAnAccountOwnedTerm()
    {
        Assert.Null(typeof(Odyssey.Dtos.Finance.ExistingAccount).GetProperty("TermCount"));
        Assert.Null(typeof(Odyssey.Dtos.Finance.ExistingAccount).GetProperty("CurrentTerms"));
        Assert.Null(typeof(Odyssey.Dtos.Finance.ExistingTerm).GetProperty("AccountId"));
        Assert.Equal(typeof(Guid), typeof(Odyssey.Dtos.Finance.ExistingTerm).GetProperty("ContractId")!.PropertyType);
        Assert.Null(typeof(TermExport).GetProperty("AccountId"));
        Assert.Equal(typeof(Guid), typeof(TermExport).GetProperty("ContractId")!.PropertyType);
        Assert.Null(typeof(Odyssey.Context.Term).GetProperty("AccountId"));
        Assert.Null(typeof(Odyssey.Context.Account).GetProperty("Terms"));
    }

    /// <summary>
    /// Issue #190 AC 17 — the account-term claim pair is DELETED, not renamed. Pinned as the whole
    /// <c>accounts.*</c> family rather than by naming the two retired values, so no claim — however
    /// spelled — can creep back under the account prefix unnoticed, and no role can hold one the
    /// vocabulary lacks: <c>RoleClaimSeeder</c> revokes whatever the roles no longer list at the next
    /// start. Terms are read under <c>contracts.read</c> and written under <c>contracts.update</c>.
    /// </summary>
    [Fact]
    public void TheAccountClaimFamily_HasNoTermPair()
    {
        string[] expected =
        [
            PermissionClaims.AccountsCreate, PermissionClaims.AccountsRead, PermissionClaims.AccountsUpdate,
            PermissionClaims.AccountsDelete, PermissionClaims.AccountsEstimatesRead,
            PermissionClaims.AccountsEstimatesWrite,
        ];

        var declared = typeof(PermissionClaims)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => value.StartsWith("accounts.", StringComparison.Ordinal));

        Assert.Equal(expected.Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));

        foreach (var role in new[]
                 {
                     Odyssey.Context.Authorization.RolePermissions.AllClaims,
                     Odyssey.Context.Authorization.RolePermissions.AdminClaims,
                     Odyssey.Context.Authorization.RolePermissions.OwnerClaims,
                     Odyssey.Context.Authorization.RolePermissions.UserClaims,
                     Odyssey.Context.Authorization.RolePermissions.GuestClaims,
                 })
        {
            Assert.All(role.Where(value => value.StartsWith("accounts.", StringComparison.Ordinal)),
                value => Assert.Contains(value, expected));
        }
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
