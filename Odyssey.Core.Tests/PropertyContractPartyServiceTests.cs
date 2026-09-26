using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextContractEventType = Odyssey.Context.ContractEventType;
using ContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;

namespace Odyssey.Core.Tests;

/// <summary>
/// Service-level coverage of a property as a contract party (issue #208): the one-of-three branches in
/// <see cref="ContractService"/>, the restructured party projection, and <see cref="PropertyService.Delete"/>'s
/// hand-rolled, evented cascade — which is the ONLY cascade the EF InMemory tier sees.
/// </summary>
public class PropertyContractPartyServiceTests
{
    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
    private const string UserId = "deleting-user";

    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class StubCaps : ISystemSettingsLookup
    {
        public Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FinanceRequestCaps(25, 50, 500, 1000));

        public Task<ContractSummarySettings> GetContractSummarySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContractSummarySettings(45, 45, 6));
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

    private ContractService Contracts(OdysseyContext context, ILogger<ContractService>? logger = null) =>
        new(context, TestContextFactory.ContactLookup(journal), new FixedTimeProvider(FixedToday),
            new StubCaps(), logger ?? new RecordingLogger<ContractService>());

    private static PropertyService Properties(OdysseyContext context, ILogger<PropertyService>? logger = null) =>
        new(context, new FixedTimeProvider(FixedToday), logger);

    private static NewContract Contract(string name = "Agreement", DtoContractType type = DtoContractType.Other) => new()
    {
        Name = name,
        Type = type,
        StartDate = FixedToday.AddDays(-30),
        Ready = FixedToday.AddDays(-40),
        Signed = FixedToday.AddDays(-39),
    };

    [Fact]
    public async Task AddParty_WithAProperty_ProjectsAsPropertyKind_NotInstitution()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = (await Properties(context).Create(PropertyTestData.Car("Volvo XC60"))).PropertyId;
        var service = Contracts(context);
        var contract = await service.Create(Contract(), userId: null);

        var party = await service.AddParty(contract.ContractId,
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Object }, UserId);

        Assert.Equal(ContractPartyKind.Property, party!.Kind);
        Assert.Null(party.Institution);
        Assert.Null(party.Account);
        Assert.Equal(new ContractPropertyReference
        {
            PropertyId = propertyId,
            Name = "Volvo XC60",
            Type = PropertyType.Vehicle,
        }, party.Property);

        var reread = Assert.Single((await service.Get(contract.ContractId))!.Parties);
        Assert.Equal(ContractPartyKind.Property, reread.Kind);
        Assert.Equal(propertyId, reread.Property!.PropertyId);
    }

    [Fact]
    public async Task AddParty_WithAPropertyAndAnAccount_IsRefused_KeyedOnPropertyId()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = (await Properties(context).Create(PropertyTestData.House())).PropertyId;
        var service = Contracts(context);
        var contract = await service.Create(Contract(), userId: null);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => service.AddParty(contract.ContractId,
            new ContractPartyRequest { PropertyId = propertyId, AccountId = Guid.NewGuid(), Role = ContractPartyRole.Other },
            UserId));

        Assert.Contains(nameof(ContractPartyRequest.PropertyId), error.Errors!.Keys);
    }

    [Fact]
    public async Task AddParty_WithAnArchivedProperty_Succeeds()
    {
        await using var context = TestContextFactory.Create();
        var body = PropertyTestData.House();
        body.Archived = true;
        var propertyId = (await Properties(context).Create(body)).PropertyId;
        var service = Contracts(context);
        var contract = await service.Create(Contract(), userId: null);

        var party = await service.AddParty(contract.ContractId,
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Property }, UserId);

        Assert.Equal(ContractPartyKind.Property, party!.Kind);
    }

    [Fact]
    public async Task UpdateParty_RedatingAPropertyParty_IsNotARetarget()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = (await Properties(context).Create(PropertyTestData.House())).PropertyId;
        var service = Contracts(context);
        var contract = await service.Create(Contract(), userId: null);
        var party = await service.AddParty(contract.ContractId,
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Property }, UserId);

        await service.UpdateParty(contract.ContractId, party!.ContractPartyId,
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Property, ToDate = FixedToday },
            UserId);

        Assert.Equal(0, await context.ContractEvents.CountAsync(e => e.Type == ContextContractEventType.PartyRemoved));
    }

    [Fact]
    public async Task FindPartiesRejectedByType_NamesAPropertyPartyByTheProperty()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = (await Properties(context).Create(PropertyTestData.House("Cabin"))).PropertyId;
        var service = Contracts(context);
        var contract = await service.Create(Contract(type: DtoContractType.Insurance), userId: null);
        await service.AddParty(contract.ContractId,
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Insured }, UserId);

        var blockers = await service.FindPartiesRejectedByTypeAsync(contract.ContractId, DtoContractType.Employment);

        Assert.Equal("Cabin", Assert.Single(blockers).DisplayName);
    }

    [Fact]
    public async Task PropertyDelete_RemovesPartyRows_EventsEachOnItsContract_WithOneTimestampAndTheCaller()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = (await Properties(context).Create(PropertyTestData.House())).PropertyId;
        var contracts = Contracts(context);
        var loan = await contracts.Create(Contract("Loan"), userId: null);
        var cover = await contracts.Create(Contract("Cover"), userId: null);
        await contracts.AddParty(loan.ContractId,
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Collateral }, UserId);
        await contracts.AddParty(loan.ContractId,
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Property }, UserId);
        await contracts.AddParty(cover.ContractId,
            new ContractPartyRequest { PropertyId = propertyId, Role = ContractPartyRole.Object }, UserId);
        var eventsBefore = await context.ContractEvents.CountAsync();
        var logger = new RecordingLogger<PropertyService>();

        Assert.True(await Properties(context, logger).Delete(propertyId, UserId));

        Assert.Empty(await context.ContractParties.ToListAsync());
        Assert.Equal(2, await context.Contracts.CountAsync());

        var removed = await context.ContractEvents
            .Where(e => e.Type == ContextContractEventType.PartyRemoved)
            .ToListAsync();
        Assert.Equal(eventsBefore + 3, await context.ContractEvents.CountAsync());
        Assert.Equal(2, removed.Count(e => e.ContractId == loan.ContractId));
        Assert.Single(removed, e => e.ContractId == cover.ContractId);
        Assert.All(removed, e =>
        {
            Assert.Equal(UserId, e.CreatedByUserId);
            Assert.Equal(FixedToday, e.CreatedAtUtc);
        });

        Assert.Equal(3, logger.Lines.Count);
        Assert.All(logger.Lines, line =>
        {
            Assert.StartsWith($"Contract party {ContractPartyAudit.DetachedByPropertyDelete}:", line);
            Assert.Contains($"target Property {propertyId}", line);
            Assert.Contains($"-> {ContractPartyAudit.NoRole}", line);
            Assert.Contains($"by user {UserId}", line);
            Assert.DoesNotContain("Storgata", line);
        });
    }

    [Fact]
    public async Task PropertyDelete_WithNoParties_StagesNothing()
    {
        await using var context = TestContextFactory.Create();
        var propertyId = (await Properties(context).Create(PropertyTestData.House())).PropertyId;
        var logger = new RecordingLogger<PropertyService>();

        Assert.True(await Properties(context, logger).Delete(propertyId, UserId));

        Assert.Empty(await context.ContractEvents.ToListAsync());
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public void AuditLine_StripsLineBreaksFromTheUserId()
    {
        var logger = new RecordingLogger<PropertyService>();
        var party = new ContractParty
        {
            ContractPartyId = Guid.NewGuid(),
            ContractId = Guid.NewGuid(),
            PropertyId = Guid.NewGuid(),
            Role = Context.ContractPartyRole.Other,
        };

        ContractPartyAudit.Log(logger, "added", party, previousRole: null, "user\r\nContract party forged");

        var line = Assert.Single(logger.Lines);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.EndsWith("by user userContract party forged.", line);
    }

    [Fact]
    public void AuditLine_NamesTheTargetKind_ForEachOfTheThreeColumns()
    {
        var logger = new RecordingLogger<ContractService>();
        var targetId = Guid.NewGuid();

        ContractPartyAudit.Log(logger, "added", Party(accountId: targetId), previousRole: null, UserId);
        ContractPartyAudit.Log(logger, "added", Party(contactId: targetId), previousRole: null, UserId);
        ContractPartyAudit.Log(logger, "added", Party(propertyId: targetId), previousRole: null, UserId);

        Assert.Equal(
            [$"target Account {targetId}", $"target Institution {targetId}", $"target Property {targetId}"],
            logger.Lines.Select(l => l[l.IndexOf("target ", StringComparison.Ordinal)..l.IndexOf(", role", StringComparison.Ordinal)]));

        static ContractParty Party(Guid? accountId = null, Guid? contactId = null, Guid? propertyId = null) => new()
        {
            ContractPartyId = Guid.NewGuid(),
            ContractId = Guid.NewGuid(),
            AccountId = accountId,
            ContactId = contactId,
            PropertyId = propertyId,
            Role = Context.ContractPartyRole.Other,
        };
    }
}
