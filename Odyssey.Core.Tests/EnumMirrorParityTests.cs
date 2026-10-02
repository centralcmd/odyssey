using Mapster;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Every enum declared in both <c>Odyssey.Context</c> and <c>Odyssey.Dtos</c> under one name carries
/// identical member names and ordinals, so <see cref="EnumMirror"/> may map the pair by ordinal (issue
/// #287 M6). Reflection finds the pairs, so a new mirrored enum is covered the day it is declared.
/// </summary>
public class EnumMirrorParityTests
{
    public static TheoryData<Type, Type> MirroredPairs()
    {
        var dtoEnums = typeof(SystemSettingsDefaults).Assembly.GetTypes()
            .Where(t => t.IsEnum && t.IsPublic)
            .ToLookup(t => t.Name, StringComparer.Ordinal);

        var data = new TheoryData<Type, Type>();
        foreach (var context in typeof(OdysseyContext).Assembly.GetTypes().Where(t => t.IsEnum && t.IsPublic))
        {
            foreach (var dto in dtoEnums[context.Name])
            {
                data.Add(context, dto);
            }
        }

        return data;
    }

    [Fact]
    public void The_scan_finds_the_mirrored_pairs()
    {
        // 21 at the time of writing; a floor rather than an exact count, so adding a pair needs no edit
        // here, while a scan broken into finding nothing cannot pass vacuously.
        Assert.True(MirroredPairs().Count >= 21);
    }

    [Theory]
    [MemberData(nameof(MirroredPairs))]
    public void Each_pair_declares_the_same_names_at_the_same_ordinals(Type context, Type dto)
    {
        Assert.Equal(Shape(context), Shape(dto));
        Assert.Equal(Enum.GetUnderlyingType(context), Enum.GetUnderlyingType(dto));
        Assert.Equal(context.IsDefined(typeof(FlagsAttribute), false), dto.IsDefined(typeof(FlagsAttribute), false));
    }

    [Fact]
    public void Every_registered_pair_round_trips_each_member_through_Mapster()
    {
        MapsterConfig.Register();

        RoundTrips<Context.AccountType, Dtos.Finance.AccountType>();
        RoundTrips<Context.AccountFileType, Dtos.Finance.AccountFileType>();
        RoundTrips<Context.BudgetCategoryType, Dtos.Finance.BudgetCategoryType>();
        RoundTrips<Context.TransactionFileType, Dtos.Finance.TransactionFileType>();
        RoundTrips<Context.TaxStatementFileType, Dtos.Finance.TaxStatementFileType>();
        RoundTrips<Context.PropertyFileType, Dtos.Finance.PropertyFileType>();
        RoundTrips<Context.TermValueUnit, Dtos.Finance.TermValueUnit>();
        RoundTrips<Context.Interval, Dtos.Finance.Interval>();
    }

    [Fact]
    public void An_undefined_stored_value_lands_on_the_pair_fallback()
    {
        MapsterConfig.Register();

        // Retired ordinals: AccountType 6 (Property) and Interval 4 (Quarterly).
        Assert.Equal(Dtos.Finance.AccountType.Unknown, ((Context.AccountType)6).Adapt<Dtos.Finance.AccountType>());
        Assert.Equal(Dtos.Finance.Interval.OneTime, ((Context.Interval)4).Adapt<Dtos.Finance.Interval>());
        Assert.Equal(Dtos.Finance.AccountFileType.Other, ((Context.AccountFileType)999).Adapt<Dtos.Finance.AccountFileType>());
        Assert.Equal(Context.TransactionFileType.Other, ((Dtos.Finance.TransactionFileType)999).Adapt<Context.TransactionFileType>());
        Assert.Equal(Dtos.Finance.BudgetCategoryType.Expense, ((Context.BudgetCategoryType)999).Adapt<Dtos.Finance.BudgetCategoryType>());
    }

    [Fact]
    public void An_undefined_term_value_unit_throws_rather_than_picking_a_member()
    {
        MapsterConfig.Register();

        Assert.ThrowsAny<Exception>(() => ((Context.TermValueUnit)999).Adapt<Dtos.Finance.TermValueUnit>());
    }

    [Fact]
    public void A_flags_combination_is_a_defined_value()
    {
        var both = Context.DaysOfWeekFlags.Monday | Context.DaysOfWeekFlags.Friday;

        Assert.Equal(Dtos.Journal.DaysOfWeekFlags.Monday | Dtos.Journal.DaysOfWeekFlags.Friday,
            EnumMirror.ConvertOrThrow<Context.DaysOfWeekFlags, Dtos.Journal.DaysOfWeekFlags>(both));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EnumMirror.ConvertOrThrow<Context.DaysOfWeekFlags, Dtos.Journal.DaysOfWeekFlags>((Context.DaysOfWeekFlags)(1 << 20)));
    }

    private static void RoundTrips<TContext, TDto>()
        where TContext : struct, Enum
        where TDto : struct, Enum
    {
        foreach (var member in Enum.GetValues<TContext>())
        {
            var dto = member.Adapt<TDto>();
            Assert.Equal(member.ToString(), dto.ToString());
            Assert.Equal(member, dto.Adapt<TContext>());
        }
    }

    private static List<(string Name, long Value)> Shape(Type type) =>
        [.. Enum.GetValues(type).Cast<object>()
            .Select(value => (Enum.GetName(type, value)!, Convert.ToInt64(value)))
            .OrderBy(member => member.Item2).ThenBy(member => member.Item1, StringComparer.Ordinal)];
}
