using System.Reflection;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextInterval = Odyssey.Context.Interval;
using DtoTermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The two non-numeric term kinds (issue #192) at the service tier: the log line keeps a Text term's
/// value out, the Mapster converters are exhaustive and throw rather than guess, the snapshot has no
/// text member to leak, and the contracts roll-up resolves each series BEFORE it filters to Amount.
/// </summary>
public class TermTextDateTimeServiceTests
{
    private const string SecretText = "SECRET-CLAUSE 3 months, to the end of a month";
    private static readonly DateTime FixedToday = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();

    // ── AC 11: the log line never carries the text ────────────────────────────

    [Fact]
    public async Task EveryTextTermWrite_LogsThePlaceholder_AndNeverTheText()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var log = new RecordingLogger<TermService>();
        var service = new TermService(context, new FixedTimeProvider(FixedToday), logger: log);

        var created = await service.CreateForContract(contractId, Text(SecretText), userId: "u1");
        await service.UpdateForContract(contractId, created.TermId, Text(SecretText + " (revised)"), userId: "u1");
        await service.DeleteForContract(contractId, created.TermId, userId: "u1");

        Assert.Equal(3, log.Lines.Count);
        Assert.All(log.Lines, line =>
        {
            Assert.Contains("(text)", line, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET-CLAUSE", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ADateTimeTermWrite_LogsTheRoundTrippedInstant()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var log = new RecordingLogger<TermService>();
        var service = new TermService(context, new FixedTimeProvider(FixedToday), logger: log);
        var instant = new DateTime(2027, 3, 31, 10, 0, 0, DateTimeKind.Utc);

        await service.CreateForContract(contractId, DateTimeTerm(instant), userId: "u1");

        Assert.Contains(instant.ToString("O"), Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    /// <summary>
    /// A kind change logs both halves in their own shape: the old amount as a number, the new text as
    /// the placeholder — the snapshot's original half reads the unit off the change tracker too.
    /// </summary>
    [Fact]
    public async Task AnAmountChangedToText_LogsTheOldNumberAndThePlaceholder()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var log = new RecordingLogger<TermService>();
        var service = new TermService(context, new FixedTimeProvider(FixedToday), logger: log);

        var created = await service.CreateForContract(contractId, Amount(45m), userId: "u1");
        log.Lines.Clear();
        await service.UpdateForContract(contractId, created.TermId, Text(SecretText, label: "Service charge"), userId: "u1");

        var line = Assert.Single(log.Lines);
        Assert.Contains("45 EUR from", line, StringComparison.Ordinal);
        Assert.Contains("-> (text) (none) from", line, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-CLAUSE", line, StringComparison.Ordinal);
    }

    // ── AC 20: the snapshot has no member that could carry the text ───────────

    [Fact]
    public void TermSnapshot_HasNoTextValueMember()
    {
        var snapshot = typeof(TermService).GetNestedType("TermSnapshot", BindingFlags.NonPublic);

        Assert.NotNull(snapshot);
        Assert.DoesNotContain(
            snapshot!.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static),
            member => member.Name.Contains("Text", StringComparison.OrdinalIgnoreCase)
                && member.Name != "LogValue");
        Assert.Null(snapshot.GetProperty(nameof(Term.TextValue)));
    }

    // ── AC 12: the Mapster converters are exhaustive ──────────────────────────

    [Fact]
    public void EveryTermValueUnit_RoundTripsContextAndDto_ByNameAndOrdinal()
    {
        Assert.Equal(Enum.GetNames<ContextTermValueUnit>(), Enum.GetNames<DtoTermValueUnit>());

        foreach (var member in Enum.GetValues<ContextTermValueUnit>())
        {
            var dto = member.Adapt<DtoTermValueUnit>();
            Assert.Equal(member.ToString(), dto.ToString());
            Assert.Equal((int)member, (int)dto);
            Assert.Equal(member, dto.Adapt<ContextTermValueUnit>());
        }
    }

    /// <summary>
    /// The read path maps a stored row through the registered converter, which throws on an
    /// undefined ordinal rather than falling through to <c>Percentage</c> — a Text term shown as a
    /// percentage is the misrepresentation the exhaustive switch removes. Each defined member still maps
    /// by name through the same path.
    /// </summary>
    [Fact]
    public void AnUndefinedOrdinalOnARow_Throws_RatherThanMappingToPercentage()
    {
        foreach (var member in Enum.GetValues<ContextTermValueUnit>())
        {
            var mapped = new Term { ValueUnit = member, Label = "x" }.Adapt<ExistingTerm>();
            Assert.Equal(member.ToString(), mapped.ValueUnit.ToString());
        }

        var corrupt = new Term { ValueUnit = (ContextTermValueUnit)4, Label = "x" };
        Assert.ThrowsAny<Exception>(() => corrupt.Adapt<ExistingTerm>());
        Assert.ThrowsAny<Exception>(() => corrupt.Adapt<CurrentTerm>());
    }

    /// <summary>A direct (non-HTTP) caller with an undefined unit is refused before the converter.</summary>
    [Fact]
    public async Task ADirectCallerWithAnUndefinedUnit_IsRefusedKeyedOnValueUnit()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);
        var term = Amount(1m);
        term.ValueUnit = (DtoTermValueUnit)7;

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateForContract(contractId, term, null));
        Assert.True(ex.Errors!.ContainsKey(nameof(NewTerm.ValueUnit)));
    }

    // ── AC 13: the roll-up resolves the series before it filters ──────────────

    [Fact]
    public async Task RollUp_AnAmountSupersededByText_ContributesNothing()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedActiveContract(context);
        SeedTerm(context, id, "Service charge", ContextTermValueUnit.Amount, FixedToday.AddMonths(-6), value: 40m);
        SeedTerm(context, id, "Service charge", ContextTermValueUnit.Text, FixedToday.AddMonths(-1), text: "Included in the rent");

        var summary = await Summary(context).GetSummary("USD");

        Assert.Null(summary.RunRate.Monthly);
        Assert.Empty(summary.UpcomingCharges);
    }

    [Fact]
    public async Task RollUp_AnAmountSupersededByAPercentage_ContributesNothing()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedActiveContract(context);
        SeedTerm(context, id, "Management", ContextTermValueUnit.Amount, FixedToday.AddMonths(-6), value: 40m);
        SeedTerm(context, id, "Management", ContextTermValueUnit.Percentage, FixedToday.AddMonths(-1), value: 0.01m);

        var summary = await Summary(context).GetSummary("USD");

        Assert.Null(summary.RunRate.Monthly);
    }

    [Fact]
    public async Task RollUp_FactTermsBesideAPrice_LeaveThePriceCounted()
    {
        await using var context = TestContextFactory.Create();
        var id = SeedActiveContract(context);
        SeedTerm(context, id, "Rent", ContextTermValueUnit.Amount, FixedToday.AddMonths(-6), value: 1000m);
        SeedTerm(context, id, "Notice period", ContextTermValueUnit.Text, FixedToday.AddMonths(-6), text: "3 months");
        SeedTerm(context, id, "Break deadline", ContextTermValueUnit.DateTime, FixedToday.AddMonths(-6),
            instant: FixedToday.AddMonths(5));

        var summary = await Summary(context).GetSummary("USD");

        Assert.Equal(1000m, summary.RunRate.Monthly);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static NewTerm Text(string text, string label = "Notice period") => new()
    {
        Label = label,
        ValueUnit = DtoTermValueUnit.Text,
        TextValue = text,
        EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static NewTerm DateTimeTerm(DateTime instant) => new()
    {
        Label = "Break deadline",
        ValueUnit = DtoTermValueUnit.DateTime,
        DateTimeValue = instant,
        EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static NewTerm Amount(decimal value) => new()
    {
        Label = "Service charge",
        ValueUnit = DtoTermValueUnit.Amount,
        Value = value,
        CurrencyCode = "EUR",
        EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private ContractService Summary(OdysseyContext context) =>
        new(context, TestContextFactory.ContactLookup(journal), new FixedTimeProvider(FixedToday),
            new FakeSystemSettingsLookup(), NullLogger<ContractService>.Instance);

    private static Guid SeedActiveContract(OdysseyContext context)
    {
        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = "Lease",
            Type = Odyssey.Context.ContractType.Rental,
            StartDate = FixedToday.AddYears(-1),
            EndDate = FixedToday.AddYears(1),
            Ready = FixedToday.AddYears(-1).AddDays(-1),
            Signed = FixedToday.AddYears(-1),
            CreatedAtUtc = FixedToday.AddYears(-1),
        };
        context.Contracts.Add(contract);
        context.SaveChanges();
        return contract.ContractId;
    }

    private static void SeedTerm(
        OdysseyContext context, Guid contractId, string label, ContextTermValueUnit unit, DateTime effectiveFrom,
        decimal? value = null, string? text = null, DateTime? instant = null)
    {
        var numeric = unit is ContextTermValueUnit.Amount or ContextTermValueUnit.Percentage;
        context.Terms.Add(new Term
        {
            TermId = Guid.NewGuid(),
            ContractId = contractId,
            Label = label,
            LabelKey = label.ToLowerInvariant(),
            ValueUnit = unit,
            Value = value,
            TextValue = text,
            DateTimeValue = instant,
            CurrencyCode = unit == ContextTermValueUnit.Amount ? "USD" : null,
            Interval = numeric ? ContextInterval.Monthly : null,
            IntervalCount = numeric ? 1 : null,
            EffectiveFrom = effectiveFrom,
            CreatedAtUtc = effectiveFrom,
        });
        context.SaveChanges();
    }

    private static async Task<Guid> SeedContractAsync(OdysseyContext context)
    {
        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = "Lease",
            Type = Odyssey.Context.ContractType.Rental,
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
