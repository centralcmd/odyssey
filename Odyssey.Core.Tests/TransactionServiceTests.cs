using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

public class TransactionServiceTests
{
    [Fact]
    public async Task CreateAndGetTransactionRoundTripsWithAccount()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Checking",
            Description = "Daily use",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var created = await service.Create(new NewTransaction
        {
            Description = "Grocery Store",
            Amount = 95,
            AccountId = account.AccountId,
            TimeStamp = new DateTime(2024, 12, 5, 0, 0, 0, DateTimeKind.Utc),
            ExternalId = "ext-123",
            InternalId = "int-456",
            ExtraData = "Imported from bank feed",
        });

        var fetched = await service.Get(created.TransactionId);

        Assert.NotNull(fetched);
        Assert.Equal("Grocery Store", fetched!.Description);
        Assert.Equal(95, fetched.Amount);
        Assert.NotNull(fetched.Account);
        Assert.Equal(account.AccountId, fetched.Account!.AccountId);
        Assert.Equal("ext-123", fetched.ExternalId);
        Assert.Equal("int-456", fetched.InternalId);
        Assert.Equal("Imported from bank feed", fetched.ExtraData);
        Assert.Equal(TransactionStatus.New, fetched.Status);
        Assert.NotEqual(default, fetched.StatusChangedAt);
    }

    [Fact]
    public async Task UpdateAndDeleteTransactionWorks()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Savings",
            Description = "Backup",
            AccountType = ContextAccountType.SavingsAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var created = await service.Create(new NewTransaction
        {
            Description = "Initial",
            Amount = 150,
            AccountId = account.AccountId,
        });

        var updated = await service.Update(created.TransactionId, new NewTransaction
        {
            Description = "Updated",
            Amount = 200,
            AccountId = account.AccountId,
            TimeStamp = new DateTime(2024, 12, 2, 0, 0, 0, DateTimeKind.Utc),
            ExternalId = "ext-updated",
            InternalId = "int-updated",
            ExtraData = "Updated notes",
        });

        Assert.NotNull(updated);
        Assert.Equal("Updated", updated!.Description);
        Assert.Equal(200, updated.Amount);
        Assert.Equal("ext-updated", updated.ExternalId);
        Assert.Equal("int-updated", updated.InternalId);
        Assert.Equal("Updated notes", updated.ExtraData);

        await service.Delete(created.TransactionId);
        Assert.Equal(0, context.Transactions.Count());
    }

    [Fact]
    public async Task SearchForTransactionsAppliesOffsetAndLimit()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Checking",
            Description = "Daily",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        await service.Create(new NewTransaction
        {
            Description = "Transaction A",
            Amount = 10,
            AccountId = account.AccountId,
            TimeStamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await service.Create(new NewTransaction
        {
            Description = "Transaction B",
            Amount = 20,
            AccountId = account.AccountId,
            TimeStamp = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        });

        var results = (await service.ListAsync(new TransactionsQueryParams
        {
            Offset = 1,
            Limit = 1,
            SortBy = TransactionSortBy.Date,
            SortDir = SortDirection.Asc,
        })).Items;

        Assert.Single(results);
        Assert.Equal("Transaction B", results[0].Description);
    }

    [Fact]
    public async Task SearchForFiltersByAccountIds()
    {
        await using var context = TestContextFactory.Create();
        var checking = new Account
        {
            Name = "Checking",
            Description = "Daily",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var savings = new Account
        {
            Name = "Savings",
            Description = "Backup",
            AccountType = ContextAccountType.SavingsAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var credit = new Account
        {
            Name = "Credit",
            Description = "Card",
            AccountType = ContextAccountType.CreditCard,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.AddRange(checking, savings, credit);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        await service.Create(new NewTransaction { Description = "Checking A", Amount = 10, AccountId = checking.AccountId });
        await service.Create(new NewTransaction { Description = "Checking B", Amount = 20, AccountId = checking.AccountId });
        await service.Create(new NewTransaction { Description = "Savings A", Amount = 30, AccountId = savings.AccountId });
        await service.Create(new NewTransaction { Description = "Credit A", Amount = 40, AccountId = credit.AccountId });

        // Single account
        var checkingOnly = (await service.ListAsync(new TransactionsQueryParams { AccountIds = [checking.AccountId] })).Items;
        Assert.Equal(2, checkingOnly.Count);
        Assert.All(checkingOnly, t => Assert.Equal(checking.AccountId, t.AccountId));

        // Multiple accounts
        var checkingAndSavings = (await service.ListAsync(new TransactionsQueryParams { AccountIds = [checking.AccountId, savings.AccountId] })).Items;
        Assert.Equal(3, checkingAndSavings.Count);
        Assert.DoesNotContain(checkingAndSavings, t => t.AccountId == credit.AccountId);

        // No filter returns everything
        var all = (await service.ListAsync(new TransactionsQueryParams())).Items;
        Assert.Equal(4, all.Count);

        // Empty filter is treated as no filter
        var emptyFilter = (await service.ListAsync(new TransactionsQueryParams { AccountIds = [] })).Items;
        Assert.Equal(4, emptyFilter.Count);
    }

    [Fact]
    public async Task SearchForFiltersByTagIdsAndDateRange()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Checking",
            Description = "Daily",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var groceries = new TransactionTag { Name = "Groceries" };
        var rent = new TransactionTag { Name = "Rent" };
        var fuel = new TransactionTag { Name = "Fuel" };
        context.Accounts.Add(account);
        context.TransactionTags.AddRange(groceries, rent, fuel);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        // In range + matching tags.
        await service.Create(new NewTransaction { Description = "Groceries Jan", Amount = 50, AccountId = account.AccountId, TransactionTagIds = [groceries.TransactionTagId], TimeStamp = new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc) });
        await service.Create(new NewTransaction { Description = "Rent Jan", Amount = 800, AccountId = account.AccountId, TransactionTagIds = [rent.TransactionTagId], TimeStamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        // Matching tag but out of range.
        await service.Create(new NewTransaction { Description = "Groceries Feb", Amount = 60, AccountId = account.AccountId, TransactionTagIds = [groceries.TransactionTagId], TimeStamp = new DateTime(2025, 2, 5, 0, 0, 0, DateTimeKind.Utc) });
        // In range but a tag we are not asking for.
        await service.Create(new NewTransaction { Description = "Fuel Jan", Amount = 40, AccountId = account.AccountId, TransactionTagIds = [fuel.TransactionTagId], TimeStamp = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc) });
        // In range, no tag at all.
        await service.Create(new NewTransaction { Description = "Untagged Jan", Amount = 5, AccountId = account.AccountId, TimeStamp = new DateTime(2025, 1, 20, 0, 0, 0, DateTimeKind.Utc) });

        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        var results = (await service.ListAsync(new TransactionsQueryParams { TagIds = [groceries.TransactionTagId, rent.TransactionTagId], From = from, To = to })).Items;

        Assert.Equal(2, results.Count);
        Assert.Contains(results, t => t.Description == "Groceries Jan");
        Assert.Contains(results, t => t.Description == "Rent Jan");
        Assert.DoesNotContain(results, t => t.Description == "Groceries Feb"); // out of range
        Assert.DoesNotContain(results, t => t.Description == "Fuel Jan");      // untracked tag
        Assert.DoesNotContain(results, t => t.Description == "Untagged Jan");  // no tag
    }

    [Fact]
    public async Task CreateWithMultipleTags_RoundTripsAllTags()
    {
        await using var context = TestContextFactory.Create();
        var account = NewAccount();
        var groceries = new TransactionTag { Name = "Groceries" };
        var reimbursable = new TransactionTag { Name = "Reimbursable" };
        context.Accounts.Add(account);
        context.TransactionTags.AddRange(groceries, reimbursable);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var created = await service.Create(new NewTransaction
        {
            Description = "Team lunch",
            Amount = 40,
            AccountId = account.AccountId,
            TransactionTagIds = [groceries.TransactionTagId, reimbursable.TransactionTagId],
        });

        var fetched = await service.Get(created.TransactionId);

        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.TransactionTags.Count);
        Assert.Contains(fetched.TransactionTags, t => t.TransactionTagId == groceries.TransactionTagId);
        Assert.Contains(fetched.TransactionTags, t => t.TransactionTagId == reimbursable.TransactionTagId);
    }

    [Fact]
    public async Task CreateDeDuplicatesRepeatedTagIds()
    {
        await using var context = TestContextFactory.Create();
        var account = NewAccount();
        var groceries = new TransactionTag { Name = "Groceries" };
        context.Accounts.Add(account);
        context.TransactionTags.Add(groceries);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var created = await service.Create(new NewTransaction
        {
            Description = "Dup tags",
            Amount = 10,
            AccountId = account.AccountId,
            TransactionTagIds = [groceries.TransactionTagId, groceries.TransactionTagId],
        });

        var fetched = await service.Get(created.TransactionId);
        Assert.Single(fetched!.TransactionTags);
    }

    [Fact]
    public async Task UpdateReconcilesTagSet_AddsRemovesAndKeepsUntouched()
    {
        await using var context = TestContextFactory.Create();
        var account = NewAccount();
        var a = new TransactionTag { Name = "A" };
        var b = new TransactionTag { Name = "B" };
        var c = new TransactionTag { Name = "C" };
        context.Accounts.Add(account);
        context.TransactionTags.AddRange(a, b, c);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var created = await service.Create(new NewTransaction
        {
            Description = "Reconcile me",
            Amount = 10,
            AccountId = account.AccountId,
            TransactionTagIds = [a.TransactionTagId, b.TransactionTagId],
        });

        // Drop B, keep A, add C.
        await service.Update(created.TransactionId, new NewTransaction
        {
            Description = "Reconcile me",
            Amount = 10,
            AccountId = account.AccountId,
            TransactionTagIds = [a.TransactionTagId, c.TransactionTagId],
        });

        var fetched = await service.Get(created.TransactionId);
        Assert.Equal(2, fetched!.TransactionTags.Count);
        Assert.Contains(fetched.TransactionTags, t => t.TransactionTagId == a.TransactionTagId);
        Assert.Contains(fetched.TransactionTags, t => t.TransactionTagId == c.TransactionTagId);
        Assert.DoesNotContain(fetched.TransactionTags, t => t.TransactionTagId == b.TransactionTagId);

        // No orphaned or duplicated link rows.
        Assert.Equal(2, CountLinks(context, created.TransactionId));
    }

    [Fact]
    public async Task UpdateToEmptyTagSet_RemovesAllTags()
    {
        await using var context = TestContextFactory.Create();
        var account = NewAccount();
        var a = new TransactionTag { Name = "A" };
        context.Accounts.Add(account);
        context.TransactionTags.Add(a);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var created = await service.Create(new NewTransaction
        {
            Description = "Clear tags",
            Amount = 10,
            AccountId = account.AccountId,
            TransactionTagIds = [a.TransactionTagId],
        });

        await service.Update(created.TransactionId, new NewTransaction
        {
            Description = "Clear tags",
            Amount = 10,
            AccountId = account.AccountId,
            TransactionTagIds = [],
        });

        var fetched = await service.Get(created.TransactionId);
        Assert.Empty(fetched!.TransactionTags);
        Assert.Equal(0, CountLinks(context, created.TransactionId));
    }

    [Fact]
    public async Task SearchForReturnsTransactionOnceWhenItCarriesMultipleRequestedTags()
    {
        await using var context = TestContextFactory.Create();
        var account = NewAccount();
        var a = new TransactionTag { Name = "A" };
        var b = new TransactionTag { Name = "B" };
        context.Accounts.Add(account);
        context.TransactionTags.AddRange(a, b);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var created = await service.Create(new NewTransaction
        {
            Description = "Both tags",
            Amount = 10,
            AccountId = account.AccountId,
            TransactionTagIds = [a.TransactionTagId, b.TransactionTagId],
        });

        var results = (await service.ListAsync(new TransactionsQueryParams { TagIds = [a.TransactionTagId, b.TransactionTagId] })).Items;

        Assert.Single(results);
        Assert.Equal(created.TransactionId, results[0].TransactionId);
    }

    [Fact]
    public async Task CreateWithInvalidTagId_Throws()
    {
        await using var context = TestContextFactory.Create();
        var account = NewAccount();
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewTransaction
        {
            Description = "Bad tag",
            Amount = 10,
            AccountId = account.AccountId,
            TransactionTagIds = [Guid.NewGuid()],
        }));
    }

    [Fact]
    public async Task CreateWithArchivedTagId_Throws()
    {
        await using var context = TestContextFactory.Create();
        var account = NewAccount();
        var archived = new TransactionTag { Name = "Old", Archived = DateTime.UtcNow };
        context.Accounts.Add(account);
        context.TransactionTags.Add(archived);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewTransaction
        {
            Description = "Archived tag",
            Amount = 10,
            AccountId = account.AccountId,
            TransactionTagIds = [archived.TransactionTagId],
        }));
    }

    private static Account NewAccount() => new()
    {
        Name = "Checking",
        Description = "Daily",
        AccountType = ContextAccountType.CheckingAccount,
        Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static int CountLinks(OdysseyContext context, Guid transactionId) =>
        context.TransactionTagLinks.Count(l => l.TransactionId == transactionId);

    [Fact]
    public async Task CreateTransactionPersistsDecimalAmount()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Decimal Checking",
            Description = "Decimal account",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var created = await service.Create(new NewTransaction
        {
            Description = "Coffee",
            Amount = 12.345678m,
            AccountId = account.AccountId,
        });

        var fetched = await service.Get(created.TransactionId);

        Assert.NotNull(fetched);
        Assert.Equal(12.345678m, fetched!.Amount);
    }

    [Fact]
    public async Task UpdateTransactionCanClearExternalAndInternalIds()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Operations",
            Description = "Ops account",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var created = await service.Create(new NewTransaction
        {
            Description = "Has IDs",
            Amount = 50,
            AccountId = account.AccountId,
            ExternalId = "ext-value",
            InternalId = "int-value",
        });

        var updated = await service.Update(created.TransactionId, new NewTransaction
        {
            Description = "IDs cleared",
            Amount = 51,
            AccountId = account.AccountId,
            ExternalId = null,
            InternalId = null,
            ExtraData = null,
        });

        Assert.NotNull(updated);
        Assert.Null(updated!.ExternalId);
        Assert.Null(updated.InternalId);
        Assert.Null(updated.ExtraData);
    }

    [Fact]
    public async Task CreateTransactionSupportsMaxLengthExtraData()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Metadata",
            Description = "Metadata account",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var extraData = new string('x', 1024);

        var created = await service.Create(new NewTransaction
        {
            Description = "Imported",
            Amount = 1,
            AccountId = account.AccountId,
            ExtraData = extraData,
        });

        var fetched = await service.Get(created.TransactionId);

        Assert.NotNull(fetched);
        Assert.Equal(1024, fetched!.ExtraData!.Length);
        Assert.Equal(extraData, fetched.ExtraData);
    }


    [Fact]
    public async Task CreateTransactionWithApprovedStatusPersistsStatusAndComment()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Review",
            Description = "Review account",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var created = await service.Create(new NewTransaction
        {
            Description = "Reviewed",
            Amount = 33,
            AccountId = account.AccountId,
            Status = TransactionStatus.Approved,
            StatusComment = "Looks good",
        });

        Assert.Equal(TransactionStatus.Approved, created.Status);
        Assert.Equal("Looks good", created.StatusComment);
        Assert.NotEqual(default, created.StatusChangedAt);
    }

    [Fact]
    public async Task UpdateTransactionStatusChangesStatusChangedAtOnlyWhenStatusChanges()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Ops",
            Description = "Ops account",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var created = await service.Create(new NewTransaction
        {
            Description = "Initial",
            Amount = 10,
            AccountId = account.AccountId,
        });

        var firstStatusChangedAt = created.StatusChangedAt;
        await Task.Delay(20);

        var unchangedStatus = await service.Update(created.TransactionId, new NewTransaction
        {
            Description = "No status change",
            Amount = 11,
            AccountId = account.AccountId,
            Status = TransactionStatus.New,
            StatusComment = "comment updated",
        });

        Assert.NotNull(unchangedStatus);
        Assert.Equal(firstStatusChangedAt, unchangedStatus!.StatusChangedAt);
        Assert.Equal("comment updated", unchangedStatus.StatusComment);

        await Task.Delay(20);

        var changedStatus = await service.Update(created.TransactionId, new NewTransaction
        {
            Description = "Status changed",
            Amount = 12,
            AccountId = account.AccountId,
            Status = TransactionStatus.Flagged,
            StatusComment = "Needs review",
        });

        Assert.NotNull(changedStatus);
        Assert.Equal(TransactionStatus.Flagged, changedStatus!.Status);
        Assert.Equal("Needs review", changedStatus.StatusComment);
        Assert.True(changedStatus.StatusChangedAt > firstStatusChangedAt);
    }


    [Fact]
    public async Task CreateTransactionWithContactRoundTripsContact()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Card",
            Description = "Card account",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        await using var journal = TestContextFactory.CreateJournal();
        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "IKEA",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "IKEA" },
        };
        journal.Contacts.Add(contact);
        await journal.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.ContactLookup(journal));
        var created = await service.Create(new NewTransaction
        {
            Description = "Desk",
            Amount = 350,
            AccountId = account.AccountId,
            ContactId = contact.ContactId,
        });

        var fetched = await service.Get(created.TransactionId);

        Assert.NotNull(fetched);
        Assert.Equal(contact.ContactId, fetched!.ContactId);
        Assert.NotNull(fetched.Contact);
        Assert.Equal("IKEA", fetched.Contact!.ResolvedDisplayName);
    }

    [Fact]
    public async Task CreateTransactionWithArchivedContactThrows()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Card",
            Description = "Card account",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        await using var journal = TestContextFactory.CreateJournal();
        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "OLD STORE",
            Type = ContactType.Organization,
            Archived = DateTime.UtcNow,
            OrganizationDetails = new() { LegalName = "Old Store" },
        };
        journal.Contacts.Add(contact);
        await journal.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.ContactLookup(journal));

        await Assert.ThrowsAsync<DomainValidationException>(async () =>
            await service.Create(new NewTransaction
            {
                Description = "Desk",
                Amount = 350,
                AccountId = account.AccountId,
                ContactId = contact.ContactId,
            }));
    }



    [Fact]
    public async Task CreateTransaction_RejectsCurrencyMismatchWithAccount()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "EUR Account",
            Description = "Euro account",
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "EUR",
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewTransaction
        {
            Description = "Mismatch",
            Amount = 1,
            AccountId = account.AccountId,
            CurrencyCode = "USD",
        }));
    }

    [Fact]
    public async Task SearchFor_PagingBoundaries_ClampAndReturnEmptyBeyondRange()
    {
        await using var context = TestContextFactory.Create();
        var account = NewAccount();
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        for (var i = 0; i < 3; i++)
        {
            await service.Create(new NewTransaction { Description = $"T{i}", Amount = i, AccountId = account.AccountId });
        }

        // Offset at the exact count yields nothing (no off-by-one over-read).
        Assert.Empty((await service.ListAsync(new TransactionsQueryParams { Offset = 3, Limit = 100 })).Items);
        // Offset beyond the count is also empty, never an error.
        Assert.Empty((await service.ListAsync(new TransactionsQueryParams { Offset = 99, Limit = 100 })).Items);
        // limit: 0 returns nothing even though rows exist.
        Assert.Empty((await service.ListAsync(new TransactionsQueryParams { Offset = 0, Limit = 0 })).Items);
        // A negative offset is clamped to 0 (treated as "from the start"), not throwing or skipping.
        Assert.Equal(3, (await service.ListAsync(new TransactionsQueryParams { Offset = -5, Limit = 100 })).Items.Count);
    }

    [Fact]
    public async Task SearchFor_DateRange_IsInclusiveOnBothBoundsAndExclusiveJustOutside()
    {
        await using var context = TestContextFactory.Create();
        var account = NewAccount();
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 1, 31, 23, 59, 59, DateTimeKind.Utc);

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        // Exactly on each bound — both must be included (>= / <=).
        await service.Create(new NewTransaction { Description = "On from", Amount = 1, AccountId = account.AccountId, TimeStamp = from });
        await service.Create(new NewTransaction { Description = "On to", Amount = 2, AccountId = account.AccountId, TimeStamp = to });
        // One tick on either side — both must be excluded.
        await service.Create(new NewTransaction { Description = "Just before", Amount = 3, AccountId = account.AccountId, TimeStamp = from.AddTicks(-1) });
        await service.Create(new NewTransaction { Description = "Just after", Amount = 4, AccountId = account.AccountId, TimeStamp = to.AddTicks(1) });

        var results = (await service.ListAsync(new TransactionsQueryParams { From = from, To = to })).Items;

        Assert.Equal(2, results.Count);
        Assert.Contains(results, t => t.Description == "On from");
        Assert.Contains(results, t => t.Description == "On to");
        Assert.DoesNotContain(results, t => t.Description == "Just before");
        Assert.DoesNotContain(results, t => t.Description == "Just after");
    }

    [Fact]
    public async Task SearchFor_CombinesAccountTagAndDateFiltersAsIntersection()
    {
        await using var context = TestContextFactory.Create();
        var matching = NewAccount();
        var otherAccount = new Account
        {
            Name = "Other",
            Description = "Other",
            AccountType = ContextAccountType.SavingsAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var wanted = new TransactionTag { Name = "Wanted" };
        var unwanted = new TransactionTag { Name = "Unwanted" };
        context.Accounts.AddRange(matching, otherAccount);
        context.TransactionTags.AddRange(wanted, unwanted);
        await context.SaveChangesAsync();

        var inRange = new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var from = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc);

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        // The one row satisfying all three filters.
        await service.Create(new NewTransaction { Description = "Match", Amount = 1, AccountId = matching.AccountId, TransactionTagIds = [wanted.TransactionTagId], TimeStamp = inRange });
        // Each of the following violates exactly one filter.
        await service.Create(new NewTransaction { Description = "Wrong account", Amount = 1, AccountId = otherAccount.AccountId, TransactionTagIds = [wanted.TransactionTagId], TimeStamp = inRange });
        await service.Create(new NewTransaction { Description = "Wrong tag", Amount = 1, AccountId = matching.AccountId, TransactionTagIds = [unwanted.TransactionTagId], TimeStamp = inRange });
        await service.Create(new NewTransaction { Description = "Out of range", Amount = 1, AccountId = matching.AccountId, TransactionTagIds = [wanted.TransactionTagId], TimeStamp = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc) });

        var results = (await service.ListAsync(new TransactionsQueryParams
        {
            AccountIds = [matching.AccountId], TagIds = [wanted.TransactionTagId], From = from, To = to,
        })).Items;

        var only = Assert.Single(results);
        Assert.Equal("Match", only.Description);
    }

}
