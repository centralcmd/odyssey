using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextContractType = Odyssey.Context.ContractType;
using ContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The real-engine half of the account record's Contracts section: the distinct-contract count
/// (<c>Distinct</c> then <c>GroupBy</c>) and the per-contract role list (a correlated collection in
/// the projection). The API tests cover both on EF InMemory, which evaluates LINQ in memory and so
/// proves nothing about whether either shape translates to MariaDB SQL.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class AccountContractLinkRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_account_contract_links";

    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task The_count_and_the_role_list_translate_and_count_contracts_not_party_rows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid accountId, otherId, loneId, mortgageId;
        await using (var context = NewContext())
        {
            accountId = await SeedAccountAsync(context, "Everyday Checking");
            otherId = await SeedAccountAsync(context, "Savings");
            loneId = await SeedAccountAsync(context, "Lonely");
            mortgageId = await SeedContractAsync(context, "Mortgage", ContextContractType.Loan,
                (accountId, ContextContractPartyRole.Borrower), (accountId, ContextContractPartyRole.Collateral),
                (otherId, ContextContractPartyRole.Guarantor));
            await SeedContractAsync(context, "Lease", ContextContractType.Rental, (accountId, ContextContractPartyRole.Tenant));
        }

        await using (var context = NewContext())
        {
            var accounts = new AccountService(context, new ContactLookup(context));
            var page = await accounts.ListAsync(new AccountsQueryParams(), includeContractCount: true);

            Assert.Equal(2, page.Items.Single(a => a.AccountId == accountId).ContractCount);
            Assert.Equal(1, page.Items.Single(a => a.AccountId == otherId).ContractCount);
            Assert.Equal(0, page.Items.Single(a => a.AccountId == loneId).ContractCount);
            Assert.Equal(2, (await accounts.Get(accountId, includeContractCount: true))!.ContractCount);
        }

        await using (var context = NewContext())
        {
            var contracts = new ContractService(
                context, new ContactLookup(context), TimeProvider.System, new ShippedCaps(),
                NullLogger<ContractService>.Instance);

            var rows = Assert.IsType<List<AccountContractLink>>(await contracts.ListForAccountAsync(accountId));

            Assert.Equal(["Lease", "Mortgage"], rows.Select(r => r.Name));
            var mortgage = rows.Single(r => r.ContractId == mortgageId);
            Assert.Equal(new HashSet<ContractPartyRole> { ContractPartyRole.Borrower, ContractPartyRole.Collateral },
                mortgage.Roles.ToHashSet());

            Assert.Empty((await contracts.ListForAccountAsync(loneId))!);
            Assert.Null(await contracts.ListForAccountAsync(Guid.NewGuid()));
        }
    }

    private static async Task<Guid> SeedAccountAsync(OdysseyContext context, string name)
    {
        var account = new Account
        {
            AccountId = Guid.NewGuid(),
            Name = name,
            Description = "Seed",
            Opened = Anchor,
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }

    private static async Task<Guid> SeedContractAsync(
        OdysseyContext context, string name, ContextContractType type,
        params (Guid AccountId, ContextContractPartyRole Role)[] parties)
    {
        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = name,
            Type = type,
            StartDate = Anchor,
            Signed = Anchor,
            CreatedAtUtc = Anchor,
        };
        foreach (var (accountId, role) in parties)
        {
            contract.Parties.Add(new ContractParty
            {
                ContractPartyId = Guid.NewGuid(),
                ContractId = contract.ContractId,
                AccountId = accountId,
                Role = role,
            });
        }

        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    /// <summary>The shipped cap values; neither read here consults them.</summary>
    private sealed class ShippedCaps : ISystemSettingsLookup
    {
        public Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FinanceRequestCaps(25, 50, 500, 1000));

        public Task<ContractSummarySettings> GetContractSummarySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContractSummarySettings(45, 45, 6));
    }

    private async Task MigrateAsync()
    {
        await using (var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options))
        {
            await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
            await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
