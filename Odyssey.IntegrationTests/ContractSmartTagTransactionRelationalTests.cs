// EF1002 flags interpolation into ExecuteSqlRawAsync. The only value interpolated is the test's own
// database-name constant.
#pragma warning disable EF1002

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextContractType = Odyssey.Context.ContractType;
using ContractPartyRole = Odyssey.Context.ContractPartyRole;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #226 (AC 20, 22, 29 and the day boundary): the correlated sub-queries
/// and the summary aggregate translated by Pomelo and run by MariaDB, at the maximum smart-tag cap and
/// a party count above <see cref="ListDefaults.MaxFilterArrayLength"/>. The EF InMemory provider
/// evaluates LINQ in memory, so none of the translation is observable on the fast tiers.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class ContractSmartTagTransactionRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_smart_tag_transactions";

    private static readonly DateTime TermStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TermEnd = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// AC 20 — 50 smart tags (the cap's maximum) and 60 contact parties. The match must come back right,
    /// and neither scope rule may reach the database as a materialised id list: every command is
    /// inspected for an <c>IN (…)</c> carrying that many parameters.
    /// </summary>
    [SkippableFact]
    public async Task At_the_maximum_tag_cap_and_above_the_filter_array_length_the_match_is_right()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid contractId, lastParty, firstParty, stranger;
        var matching = new List<Guid>();
        await using (var context = NewContext())
        {
            var account = await SeedAccountAsync(context);
            var contract = await SeedContractAsync(context);
            contractId = contract.ContractId;

            var tags = Enumerable.Range(0, SystemSettingsBounds.ContractMaxSmartTagsPerContractMax)
                .Select(i => new TransactionTag { Name = $"Watched {i:00}" })
                .ToList();
            var unwatched = new TransactionTag { Name = "Unwatched" };
            context.TransactionTags.AddRange(tags);
            context.TransactionTags.Add(unwatched);

            var parties = Enumerable.Range(0, ListDefaults.MaxFilterArrayLength + 10)
                .Select(i => NewContact($"Party {i:00}"))
                .ToList();
            var outsider = NewContact("Outsider");
            context.Contacts.AddRange(parties);
            context.Contacts.Add(outsider);
            await context.SaveChangesAsync();

            context.ContractSmartTags.AddRange(tags.Select(t => new ContractSmartTag
            {
                ContractId = contractId, TransactionTagId = t.TransactionTagId, AddedAt = TermStart,
            }));
            context.ContractParties.AddRange(parties.Select(p => new ContractParty
            {
                ContractId = contractId, ContactId = p.ContactId, Role = ContractPartyRole.Other,
            }));
            await context.SaveChangesAsync();

            firstParty = parties[0].ContactId;
            lastParty = parties[^1].ContactId;
            stranger = outsider.ContactId;

            var hitFirst = NewTransaction(account, "first", -10m, TermStart.AddDays(5), firstParty, tags[0]);
            var hitLast = NewTransaction(account, "last", -20m, TermStart.AddDays(6), lastParty, tags[^1]);
            context.Transactions.AddRange(
                hitFirst,
                hitLast,
                NewTransaction(account, "outsider", -1m, TermStart.AddDays(7), stranger, tags[0]),
                NewTransaction(account, "unwatched", -1m, TermStart.AddDays(8), lastParty, unwatched),
                NewTransaction(account, "before", -1m, TermStart.AddDays(-1), lastParty, tags[0]));
            await context.SaveChangesAsync();
            matching.AddRange([hitLast.TransactionId, hitFirst.TransactionId]);
        }

        var counter = new CommandCapture();
        await using (var context = NewContext(counter))
        {
            var result = await Service(context).ListAsync(contractId, new ContractSmartTagTransactionsQueryParams());

            Assert.NotNull(result);
            Assert.Equal(ContractSmartTagEmptyReason.None, result!.Scope.EmptyReason);
            Assert.Equal(SystemSettingsBounds.ContractMaxSmartTagsPerContractMax, result.Scope.SmartTagCount);
            Assert.Equal(ListDefaults.MaxFilterArrayLength + 10, result.Scope.PartyContactCount);
            Assert.Equal(matching, result.Page.Items.Select(t => t.TransactionId));
            Assert.Equal(2, result.Summary.TransactionCount);
        }

        Assert.All(counter.Commands, command =>
            Assert.True(command.ParameterCount < ListDefaults.MaxFilterArrayLength,
                $"A command carried {command.ParameterCount} parameters: {command.Text}"));
    }

    /// <summary>
    /// AC 22 + AC 29 — all three rules together with a search and a non-default sort, and the summary's
    /// values, on MariaDB. Amounts carry the column's full six fractional digits and must sum exactly.
    /// </summary>
    [SkippableFact]
    public async Task All_three_rules_with_search_sort_and_the_summary_translate_on_MariaDB()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid contractId;
        await using (var context = NewContext())
        {
            var alpha = await SeedAccountAsync(context, "Alpha", "NOK");
            var beta = await SeedAccountAsync(context, "Beta", "NOK");
            var euro = await SeedAccountAsync(context, "Euro", "EUR");
            var contract = await SeedContractAsync(context);
            contractId = contract.ContractId;

            var tag = new TransactionTag { Name = "Electricity" };
            var other = new TransactionTag { Name = "Other" };
            var supplier = NewContact("Hafslund");
            var stranger = NewContact("Stranger");
            context.AddRange(tag, other, supplier, stranger);
            await context.SaveChangesAsync();
            context.ContractSmartTags.Add(new ContractSmartTag
            {
                ContractId = contractId, TransactionTagId = tag.TransactionTagId, AddedAt = TermStart,
            });
            context.ContractParties.Add(new ContractParty
            {
                ContractId = contractId, ContactId = supplier.ContactId, Role = ContractPartyRole.Other,
            });

            context.Transactions.AddRange(
                NewTransaction(beta, "Power bill March", -100.123456m, TermStart.AddDays(60), supplier.ContactId, tag),
                NewTransaction(alpha, "Power bill January", -200.000001m, TermStart.AddDays(10), supplier.ContactId, tag),
                NewTransaction(alpha, "Refund", 0.333333m, TermStart.AddDays(20), supplier.ContactId, tag),
                NewTransaction(euro, "Power bill EU", -12.5m, TermStart.AddDays(30), supplier.ContactId, tag),
                // A second watched tag on the same row must not double it (the link is the other tag).
                NewTransaction(alpha, "Power bill April", -1.000001m, TermStart.AddDays(90), supplier.ContactId, tag, other),
                // Excluded by R1, R2 and R3 respectively.
                NewTransaction(alpha, "Power bill untagged", -999m, TermStart.AddDays(10), supplier.ContactId, other),
                NewTransaction(alpha, "Power bill 2025", -999m, TermStart.AddDays(-10), supplier.ContactId, tag),
                NewTransaction(alpha, "Power bill stranger", -999m, TermStart.AddDays(10), stranger.ContactId, tag));
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            var result = (await Service(context).ListAsync(contractId, new ContractSmartTagTransactionsQueryParams
            {
                Search = "power bill",
                SortBy = TransactionSortBy.Account,
                SortDir = SortDirection.Desc,
            }))!;

            // Account name descending: Euro, Beta, then Alpha's two rows (tie broken by id).
            var descriptions = result.Page.Items.Select(t => t.Description).ToArray();
            Assert.Equal(["Power bill EU", "Power bill March"], descriptions[..2]);
            Assert.Equal(
                ["Power bill April", "Power bill January"],
                descriptions[2..].OrderBy(d => d, StringComparer.Ordinal));
            Assert.Equal(4, result.Page.TotalCount);

            Assert.Equal(5, result.Summary.TransactionCount);
            Assert.Equal(["EUR", "NOK"], result.Summary.ByCurrency.Select(r => r.CurrencyCode));
            var eur = result.Summary.ByCurrency[0];
            Assert.Equal((1, 0m, 12.5m, -12.5m), (eur.TransactionCount, eur.TotalIn, eur.TotalOut, eur.Net));
            var nok = result.Summary.ByCurrency[1];
            Assert.Equal(4, nok.TransactionCount);
            Assert.Equal(0.333333m, nok.TotalIn);
            Assert.Equal(301.123458m, nok.TotalOut);
            Assert.Equal(-300.790125m, nok.Net);
        }
    }

    /// <summary>
    /// The day boundary on MariaDB: the start day's midnight and the end day's last second are in, the
    /// next midnight is out — also when the stored contract dates carry a time of day.
    /// </summary>
    [SkippableFact]
    public async Task The_whole_end_day_is_inside_the_window_and_the_next_midnight_is_not()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid contractId;
        await using (var context = NewContext())
        {
            var account = await SeedAccountAsync(context);
            var contract = await SeedContractAsync(context,
                start: TermStart.AddHours(13), end: DateTime.SpecifyKind(TermEnd.AddHours(8), DateTimeKind.Unspecified));
            contractId = contract.ContractId;
            var tag = new TransactionTag { Name = "Electricity" };
            var supplier = NewContact("Supplier");
            context.AddRange(tag, supplier);
            await context.SaveChangesAsync();
            context.ContractSmartTags.Add(new ContractSmartTag
            {
                ContractId = contractId, TransactionTagId = tag.TransactionTagId, AddedAt = TermStart,
            });
            context.ContractParties.Add(new ContractParty
            {
                ContractId = contractId, ContactId = supplier.ContactId, Role = ContractPartyRole.Other,
            });
            context.Transactions.AddRange(
                NewTransaction(account, "before start", -1m, TermStart.AddSeconds(-1), supplier.ContactId, tag),
                NewTransaction(account, "start midnight", -1m, TermStart, supplier.ContactId, tag),
                NewTransaction(account, "end last second", -1m, TermEnd.AddDays(1).AddSeconds(-1), supplier.ContactId, tag),
                NewTransaction(account, "next midnight", -1m, TermEnd.AddDays(1), supplier.ContactId, tag));
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            var result = (await Service(context).ListAsync(contractId, new ContractSmartTagTransactionsQueryParams()))!;

            Assert.Equal(["end last second", "start midnight"], result.Page.Items.Select(t => t.Description));
            Assert.Equal(TermStart, result.Scope.From);
            Assert.Equal(TermEnd.AddDays(1), result.Scope.ToExclusive);
        }
    }

    /// <summary>
    /// The §10 contingency index is in place and replaced the one-column merchant index it prefixes —
    /// migrated create-before-drop, since the ContactId foreign key needs one of them at every moment.
    /// </summary>
    [SkippableFact]
    public async Task The_merchant_and_date_index_replaces_the_merchant_index()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT INDEX_NAME, COLUMN_NAME FROM information_schema.STATISTICS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Transactions' " +
            "AND INDEX_NAME LIKE 'IX_Transactions_ContactId%' ORDER BY INDEX_NAME, SEQ_IN_INDEX";
        var rows = new List<(string Index, string Column)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.Equal(
            [("IX_Transactions_ContactId_TimeStamp", "ContactId"), ("IX_Transactions_ContactId_TimeStamp", "TimeStamp")],
            rows);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ContractSmartTagTransactionService Service(OdysseyContext context) =>
        new(context, new TransactionService(context, new ContactLookup(context)));

    private static Contact NewContact(string name) => new()
    {
        ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
        DisplayName = name,
        NormalizedName = name.ToLowerInvariant(),
        Type = ContactType.Organization,
    };

    private static Transaction NewTransaction(
        Account account, string description, decimal amount, DateTime timeStamp, Guid contactId,
        params TransactionTag[] tags) => new()
    {
        Description = description,
        Amount = amount,
        TimeStamp = timeStamp,
        AccountId = account.AccountId,
        ContactId = contactId,
        CurrencyCode = account.CurrencyCode,
        TransactionTags = [.. tags],
    };

    private static async Task<Account> SeedAccountAsync(
        OdysseyContext context, string name = "Household", string currency = "NOK")
    {
        var account = new Account
        {
            Name = name,
            Description = name,
            Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = currency,
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    private static async Task<Contract> SeedContractAsync(
        OdysseyContext context, DateTime? start = null, DateTime? end = null)
    {
        var contract = new Contract
        {
            Name = "Power",
            Type = ContextContractType.Subscription,
            StartDate = start ?? TermStart,
            EndDate = end ?? TermEnd,
            CreatedAtUtc = TermStart,
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract;
    }

    private sealed record CapturedCommand(string Text, int ParameterCount);

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(new CapturedCommand(command.CommandText, command.Parameters.Count));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task MigrateAsync()
    {
        await using (var server = ServerContext())
        {
            await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
            await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString));
        if (interceptor is not null)
            builder = builder.AddInterceptors(interceptor);

        return new OdysseyContext(builder.Options);
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
