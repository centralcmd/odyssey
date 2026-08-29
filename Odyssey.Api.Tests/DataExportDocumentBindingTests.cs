using System.Collections;
using System.Reflection;
using Odyssey.Api.DataExport;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// <see cref="DataExportDocument"/> is an API contract — <c>DataExportController</c> declares it on a
/// <c>[ProducesResponseType]</c> — and it is also a persisted, versioned document that downstream
/// readers parse by enum number. Issue #392: it imported <c>Odyssey.Context</c> and not
/// <c>Odyssey.Dtos.Finance</c>, so seven enum properties bound to the persistence copies. Nothing was
/// mis-serialized (the copies are numerically identical), but it leaked entity types onto the OpenAPI
/// surface, and a member added to only one copy would have changed the export's wire meaning with no
/// compiler error. These tests pin the binding so the leak cannot come back silently.
/// </summary>
public class DataExportDocumentBindingTests
{
    [Fact]
    public void TheDocument_ReferencesNoPersistenceType()
    {
        var reachable = ReachableOdysseyTypes(typeof(DataExportDocument));

        // The walk actually descends into the collections and their element types — without this the
        // emptiness assertion below would pass on a set of one.
        Assert.Contains(typeof(Odyssey.Dtos.Finance.AccountType), reachable);

        var leaks = reachable
            .Where(type => type.Assembly.GetName().Name!.EndsWith(".Context", StringComparison.Ordinal))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(leaks);
    }

    [Theory]
    [InlineData(typeof(AccountExport), nameof(AccountExport.AccountType), typeof(Odyssey.Dtos.Finance.AccountType))]
    [InlineData(typeof(AccountTermExport), nameof(AccountTermExport.TermKind), typeof(Odyssey.Dtos.Finance.TermKind))]
    [InlineData(typeof(AccountTermExport), nameof(AccountTermExport.ValueUnit), typeof(Odyssey.Dtos.Finance.TermValueUnit))]
    [InlineData(typeof(AccountTermExport), nameof(AccountTermExport.BillingPeriod), typeof(Odyssey.Dtos.Finance.BillingPeriod?))]
    [InlineData(typeof(BudgetItemExport), nameof(BudgetItemExport.CategoryType), typeof(Odyssey.Dtos.Finance.BudgetCategoryType))]
    [InlineData(typeof(AccountFileExport), nameof(AccountFileExport.FileType), typeof(Odyssey.Dtos.Finance.AccountFileType))]
    [InlineData(typeof(TransactionFileExport), nameof(TransactionFileExport.Type), typeof(Odyssey.Dtos.Finance.TransactionFileType))]
    public void AnEnumProperty_BindsToTheDtosCopy(Type declaringType, string propertyName, Type expected) =>
        Assert.Equal(expected, declaringType.GetProperty(propertyName)!.PropertyType);

    /// <summary>
    /// The casts in <c>DataExportService</c> are wire-neutral only while the two copies agree. If they
    /// ever diverge the export starts meaning something different for the same number, so the casts
    /// must become an explicit mapping rather than a rename that compiles.
    /// </summary>
    [Theory]
    [InlineData(typeof(Odyssey.Dtos.Finance.AccountType), typeof(Odyssey.Context.AccountType))]
    [InlineData(typeof(Odyssey.Dtos.Finance.TermKind), typeof(Odyssey.Context.TermKind))]
    [InlineData(typeof(Odyssey.Dtos.Finance.TermValueUnit), typeof(Odyssey.Context.TermValueUnit))]
    [InlineData(typeof(Odyssey.Dtos.Finance.BillingPeriod), typeof(Odyssey.Context.BillingPeriod))]
    [InlineData(typeof(Odyssey.Dtos.Finance.BudgetCategoryType), typeof(Odyssey.Context.BudgetCategoryType))]
    [InlineData(typeof(Odyssey.Dtos.Finance.AccountFileType), typeof(Odyssey.Context.AccountFileType))]
    [InlineData(typeof(Odyssey.Dtos.Finance.TransactionFileType), typeof(Odyssey.Context.TransactionFileType))]
    public void TheDtosAndEntityCopies_StillAgreeMemberForMember(Type dtosEnum, Type entityEnum) =>
        Assert.Equal(Members(dtosEnum), Members(entityEnum));

    private static IReadOnlyList<string> Members(Type enumType) =>
        Enum.GetValues(enumType)
            .Cast<object>()
            .Select(value => $"{value} = {Convert.ToInt64(value)}")
            .Order(StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyCollection<Type> ReachableOdysseyTypes(Type root)
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>([root]);

        while (pending.TryPop(out var current))
        {
            foreach (var type in Unwrap(current))
            {
                if (type.Namespace?.StartsWith("Odyssey.", StringComparison.Ordinal) != true
                    || !seen.Add(type))
                {
                    continue;
                }

                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    pending.Push(property.PropertyType);
                }
            }
        }

        return seen;
    }

    // Guid?, IReadOnlyList<AccountExport> and IReadOnlyDictionary<string, int> all hide the types that
    // matter behind a wrapper, so yield the generic arguments alongside the type itself.
    private static IEnumerable<Type> Unwrap(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            yield return underlying;
            yield break;
        }

        if (type.IsArray)
        {
            yield return type.GetElementType()!;
            yield break;
        }

        yield return type;

        if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
        {
            foreach (var argument in type.GetGenericArguments())
            {
                yield return argument;
            }
        }
    }
}
