using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #165 against the real engine: a transaction tag used as an account smart tag cannot be
/// hard-deleted, and the <c>RESTRICT</c> foreign key on <c>AccountSmartTags.TransactionTagId</c> is
/// still there backing the service pre-check.
/// </summary>
/// <remarks>
/// The pre-check itself is covered on the fast tier (<c>Odyssey.Core.Tests</c>), which is where the
/// silent orphan happened. What only real MariaDB can show is the other half: that a caller going
/// around the service is still refused by the constraint. The EF InMemory provider enforces no
/// foreign keys at all, so the guard test for the key has nowhere else to live — and without it a
/// later migration could drop the key to <c>Cascade</c> with every fast test still green.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class TransactionTagSmartTagBlockerTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_transaction_tag_smart_tag_blocker";

    /// <summary>
    /// The service refuses with an explaining <c>409</c> and touches nothing — neither the tag nor the
    /// account's smart-tag configuration.
    /// </summary>
    [SkippableFact]
    public async Task A_tag_used_as_an_account_smart_tag_cannot_be_deleted()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var tagId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                await SeedLinkedPairAsync(context, tagId, accountId, "Checking");
            }

            await using (var context = New(connectionString))
            {
                var service = new TransactionTagService(context);
                var conflict = await Assert.ThrowsAsync<DomainConflictException>(
                    () => service.Delete(tagId));

                Assert.Contains("1 account", conflict.Message);
                // A COUNT, never the accounts: naming them would reach past the delete's own boundary.
                Assert.DoesNotContain("Checking", conflict.Message);
            }

            await using (var context = New(connectionString))
            {
                Assert.True(await context.TransactionTags.AnyAsync(tag => tag.TransactionTagId == tagId));
                Assert.Equal(1, await context.AccountSmartTags.CountAsync(link => link.TransactionTagId == tagId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The constraint behind the pre-check. A caller that goes around the service is still refused —
    /// by the database this time, which is what keeps the orphan impossible rather than merely
    /// unreachable through one code path.
    /// </summary>
    [SkippableFact]
    public async Task The_restrict_key_still_refuses_a_delete_that_bypasses_the_service()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var tagId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                await SeedLinkedPairAsync(context, tagId, accountId, "Savings");
            }

            await using (var context = New(connectionString))
            {
                var tag = await context.TransactionTags.SingleAsync(t => t.TransactionTagId == tagId);
                context.TransactionTags.Remove(tag);

                await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            }

            await using (var context = New(connectionString))
            {
                Assert.True(await context.TransactionTags.AnyAsync(tag => tag.TransactionTagId == tagId));
                Assert.Equal(1, await context.AccountSmartTags.CountAsync(link => link.TransactionTagId == tagId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The release valve: detaching the smart tag frees the tag for deletion. The refusal is about the
    /// link, not about the tag having ever been used.
    /// </summary>
    [SkippableFact]
    public async Task Detaching_the_smart_tag_frees_the_tag_for_deletion()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var tagId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                await SeedLinkedPairAsync(context, tagId, accountId, "Brokerage");
            }

            await using (var context = New(connectionString))
            {
                var link = await context.AccountSmartTags
                    .SingleAsync(l => l.AccountId == accountId && l.TransactionTagId == tagId);
                context.AccountSmartTags.Remove(link);
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                await new TransactionTagService(context).Delete(tagId);
            }

            await using (var context = New(connectionString))
            {
                Assert.False(await context.TransactionTags.AnyAsync(tag => tag.TransactionTagId == tagId));
                // The account itself is untouched by the tag's removal.
                Assert.True(await context.Accounts.AnyAsync(account => account.AccountId == accountId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    private static async Task SeedLinkedPairAsync(
        OdysseyContext context,
        Guid tagId,
        Guid accountId,
        string accountName)
    {
        context.TransactionTags.Add(new TransactionTag
        {
            TransactionTagId = tagId,
            Name = $"Groceries {tagId:N}",
            Archived = null,
        });

        context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = accountName,
            Description = string.Empty,
            Opened = DateTime.UtcNow,
            AccountType = AccountType.CheckingAccount,
            CurrencyCode = "USD",
        });
        await context.SaveChangesAsync();

        context.AccountSmartTags.Add(new AccountSmartTag
        {
            AccountId = accountId,
            TransactionTagId = tagId,
            AddedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task<string> MigratedSchemaAsync()
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var connectionString = fixture.ConnectionStringFor(Database);
        await using var context = new OdysseyContext(OptionsFor(connectionString));
        await context.Database.MigrateAsync();

        return connectionString;
    }

    private async Task DropAsync()
    {
        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private static OdysseyContext New(string connectionString) => new(OptionsFor(connectionString));

    private static DbContextOptions<OdysseyContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;
}
