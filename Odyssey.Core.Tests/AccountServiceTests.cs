using Odyssey.Core;
using Odyssey.Dtos;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountFileType = Odyssey.Context.AccountFileType;
using DtoAccountFileType = Odyssey.Dtos.Finance.AccountFileType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using FinanceDtos = Odyssey.Dtos.Finance;
using Context = Odyssey.Context;

namespace Odyssey.Core.Tests;

public class AccountServiceTests
{
    [Fact]
    public async Task CreateAccount_PersistsCurrencyCode()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var created = await service.Create(new NewAccount
        {
            Name = "Checking",
            Description = "Primary",
            AccountType = DtoAccountType.CheckingAccount,
            CurrencyCode = "EUR",
            Archived = false,
        });

        Assert.Equal("EUR", created.CurrencyCode);
    }

    [Fact]
    public async Task UpdateAccount_RejectsCurrencyChangeWhenTransactionsExist()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var created = await service.Create(new NewAccount
        {
            Name = "Checking",
            Description = "Primary",
            AccountType = DtoAccountType.CheckingAccount,
            CurrencyCode = "USD",
            Archived = false,
        });

        context.Transactions.Add(new Transaction
        {
            Description = "Existing",
            Amount = 50,
            AccountId = created.AccountId,
            CurrencyCode = "USD",
            TimeStamp = DateTime.UtcNow,
            Status = TransactionStatus.New,
            StatusChangedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(created.AccountId, new NewAccount
        {
            Name = "Checking",
            Description = "Primary",
            AccountType = DtoAccountType.CheckingAccount,
            CurrencyCode = "EUR",
            Archived = false,
        }));
    }

    [Fact]
    public async Task SearchFor_AppliesOffsetAndLimit()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        await service.Create(new NewAccount { Name = "Alpha", Description = "", AccountType = DtoAccountType.CheckingAccount, Archived = false });
        await service.Create(new NewAccount { Name = "Beta", Description = "", AccountType = DtoAccountType.CheckingAccount, Archived = false });
        await service.Create(new NewAccount { Name = "Gamma", Description = "", AccountType = DtoAccountType.CheckingAccount, Archived = false });

        var results = (await service.ListAsync(new AccountsQueryParams { Offset = 1, Limit = 2 })).Items;

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchForAndGet_PopulateTransactionCountAndBalance()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var withTwo = await service.Create(new NewAccount { Name = "Busy", Description = "", AccountType = DtoAccountType.CheckingAccount, CurrencyCode = "USD", Archived = false });
        var withNone = await service.Create(new NewAccount { Name = "Quiet", Description = "", AccountType = DtoAccountType.SavingsAccount, CurrencyCode = "USD", Archived = false });

        // Net balance for `withTwo` = 100.50 + (-30.25) = 70.25 (signed amounts).
        context.Transactions.AddRange(
            new Transaction { Description = "Income", Amount = 100.50m, AccountId = withTwo.AccountId, CurrencyCode = "USD", TimeStamp = DateTime.UtcNow, Status = TransactionStatus.New, StatusChangedAt = DateTime.UtcNow },
            new Transaction { Description = "Expense", Amount = -30.25m, AccountId = withTwo.AccountId, CurrencyCode = "USD", TimeStamp = DateTime.UtcNow, Status = TransactionStatus.New, StatusChangedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var listed = (await service.ListAsync(new AccountsQueryParams())).Items;
        var busy = listed.Single(a => a.AccountId == withTwo.AccountId);
        var quiet = listed.Single(a => a.AccountId == withNone.AccountId);
        Assert.Equal(2, busy.TransactionCount);
        Assert.Equal(70.25m, busy.Balance);
        Assert.Equal(0, quiet.TransactionCount);
        Assert.Equal(0m, quiet.Balance);

        var fetched = await service.Get(withTwo.AccountId);
        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.TransactionCount);
        Assert.Equal(70.25m, fetched.Balance);

        var fetchedEmpty = await service.Get(withNone.AccountId);
        Assert.NotNull(fetchedEmpty);
        Assert.Equal(0, fetchedEmpty!.TransactionCount);
        Assert.Equal(0m, fetchedEmpty.Balance);
    }

    [Fact]
    public async Task SearchForAndGet_PopulateFileCount()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var withFiles = await service.Create(new NewAccount { Name = "Documented", Description = "", AccountType = DtoAccountType.CheckingAccount, Archived = false });
        var withNone = await service.Create(new NewAccount { Name = "Bare", Description = "", AccountType = DtoAccountType.SavingsAccount, Archived = false });

        await service.AttachFileToAccount(withFiles.AccountId, Guid.NewGuid(), "user-1");
        await service.AttachFileToAccount(withFiles.AccountId, Guid.NewGuid(), "user-1");

        var listed = (await service.ListAsync(new AccountsQueryParams())).Items;
        Assert.Equal(2, listed.Single(a => a.AccountId == withFiles.AccountId).FileCount);
        Assert.Equal(0, listed.Single(a => a.AccountId == withNone.AccountId).FileCount);

        var fetched = await service.Get(withFiles.AccountId);
        Assert.Equal(2, fetched!.FileCount);

        var fetchedEmpty = await service.Get(withNone.AccountId);
        Assert.Equal(0, fetchedEmpty!.FileCount);
    }

    [Fact]
    public async Task GetTransactions_ReturnsAccountTransactionsWithoutCircularAccount()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount { Name = "Active", Description = "", AccountType = DtoAccountType.CheckingAccount, CurrencyCode = "USD", Archived = false });
        var other = await service.Create(new NewAccount { Name = "Other", Description = "", AccountType = DtoAccountType.SavingsAccount, CurrencyCode = "USD", Archived = false });

        context.Transactions.AddRange(
            new Transaction { Description = "A", Amount = 10m, AccountId = account.AccountId, CurrencyCode = "USD", TimeStamp = DateTime.UtcNow, Status = TransactionStatus.New, StatusChangedAt = DateTime.UtcNow },
            new Transaction { Description = "B", Amount = -5m, AccountId = account.AccountId, CurrencyCode = "USD", TimeStamp = DateTime.UtcNow, Status = TransactionStatus.New, StatusChangedAt = DateTime.UtcNow },
            new Transaction { Description = "Elsewhere", Amount = 99m, AccountId = other.AccountId, CurrencyCode = "USD", TimeStamp = DateTime.UtcNow, Status = TransactionStatus.New, StatusChangedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var transactions = await service.GetTransactions(account.AccountId);

        Assert.NotNull(transactions);
        Assert.Equal(2, transactions!.Count);
        Assert.All(transactions, t => Assert.Equal(account.AccountId, t.AccountId));
        Assert.All(transactions, t => Assert.Null(t.Account));
        Assert.Contains(transactions, t => t.Description == "A");
        Assert.Contains(transactions, t => t.Description == "B");
    }

    [Fact]
    public async Task GetTransactions_ReturnsEmptyForAccountWithoutTransactions()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount { Name = "Empty", Description = "", AccountType = DtoAccountType.CheckingAccount, Archived = false });

        var transactions = await service.GetTransactions(account.AccountId);

        Assert.NotNull(transactions);
        Assert.Empty(transactions!);
    }

    [Fact]
    public async Task GetTransactions_ReturnsNullForMissingAccount()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        Assert.Null(await service.GetTransactions(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetAccountFiles_ReturnsFilesOrNullForMissingAccount()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount { Name = "Filed", Description = "", AccountType = DtoAccountType.CheckingAccount, Archived = false });
        var fileId = Guid.NewGuid();
        context.FileMetadata.Add(new FileMetadata
        {
            Id = fileId,
            UploadedByUserId = "user-1",
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            Sha256Hash = "hash",
            FileBlobId = Guid.NewGuid(),
            UploadedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        await service.AttachFileToAccount(account.AccountId, fileId, "user-1");

        var files = await service.GetAccountFiles(account.AccountId);
        Assert.NotNull(files);
        Assert.Single(files!);
        Assert.All(files!, f => Assert.Equal(account.AccountId, f.AccountId));

        Assert.Null(await service.GetAccountFiles(Guid.NewGuid()));
    }

    [Fact]
    public async Task Update_SuccessfullyModifiesAccountName()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var created = await service.Create(new NewAccount
        {
            Name = "Old Name",
            Description = "Old desc",
            AccountType = DtoAccountType.SavingsAccount,
            CurrencyCode = "USD",
            Archived = false,
        });

        var updated = await service.Update(created.AccountId, new NewAccount
        {
            Name = "New Name",
            Description = "New desc",
            AccountType = DtoAccountType.SavingsAccount,
            CurrencyCode = "USD",
            Archived = false,
        });

        Assert.NotNull(updated);
        Assert.Equal("New Name", updated!.Name);
        Assert.Equal("New desc", updated.Description);
    }

    [Fact]
    public async Task Update_ArchiveTransitionWorks()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var created = await service.Create(new NewAccount
        {
            Name = "Savings",
            Description = "",
            AccountType = DtoAccountType.SavingsAccount,
            CurrencyCode = "USD",
            Archived = false,
        });
        Assert.Null(created.Archived);

        var archived = await service.Update(created.AccountId, new NewAccount
        {
            Name = "Savings",
            Description = "",
            AccountType = DtoAccountType.SavingsAccount,
            CurrencyCode = "USD",
            Archived = true,
        });
        Assert.NotNull(archived!.Archived);

        var unarchived = await service.Update(created.AccountId, new NewAccount
        {
            Name = "Savings",
            Description = "",
            AccountType = DtoAccountType.SavingsAccount,
            CurrencyCode = "USD",
            Archived = false,
        });
        Assert.Null(unarchived!.Archived);
    }

    [Fact]
    public async Task Delete_RemovesAccount()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var created = await service.Create(new NewAccount
        {
            Name = "Temp",
            Description = "",
            AccountType = DtoAccountType.CheckingAccount,
            Archived = false,
        });

        await service.Delete(created.AccountId);

        Assert.Equal(0, context.Accounts.Count());
        Assert.Null(await service.Get(created.AccountId));
    }

    [Fact]
    public async Task Delete_RemovesDependentTransactions()
    {
        // Cheap, Docker-free guard for the Transaction.AccountId Cascade FK: a silent flip to Restrict
        // would leave these rows (or throw) instead of cascading. The relational tier proves the
        // database-level cascade; this proves the service intent at the InMemory tier (#240 M4).
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "With Transactions",
            Description = "",
            AccountType = DtoAccountType.CheckingAccount,
            CurrencyCode = "USD",
            Archived = false,
        });

        context.Transactions.AddRange(
            new Transaction { Description = "A", Amount = 10m, AccountId = account.AccountId, CurrencyCode = "USD", TimeStamp = DateTime.UtcNow, Status = TransactionStatus.New, StatusChangedAt = DateTime.UtcNow },
            new Transaction { Description = "B", Amount = -5m, AccountId = account.AccountId, CurrencyCode = "USD", TimeStamp = DateTime.UtcNow, Status = TransactionStatus.New, StatusChangedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
        Assert.Equal(2, context.Transactions.Count(t => t.AccountId == account.AccountId));

        await service.Delete(account.AccountId);

        Assert.Null(await service.Get(account.AccountId));
        Assert.Equal(0, context.Transactions.Count(t => t.AccountId == account.AccountId));
    }

    [Fact]
    public async Task Delete_IsNoOpForMissingAccount()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        await service.Delete(Guid.NewGuid());

        Assert.Equal(0, context.Accounts.Count());
    }

    [Fact]
    public async Task Create_WithArchivedCurrency_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var usd = context.Currencies.First(c => c.CurrencyCode == "USD");
        usd.Archived = DateTime.UtcNow;
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewAccount
        {
            Name = "Account",
            Description = "",
            AccountType = DtoAccountType.CheckingAccount,
            CurrencyCode = "USD",
            Archived = false,
        }));
    }

    [Fact]
    public async Task AttachFileToAccount_PersistsAssociation()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "With Files",
            Description = "",
            AccountType = DtoAccountType.CheckingAccount,
            Archived = false,
        });

        var fileId = Guid.NewGuid();
        var result = await service.AttachFileToAccount(account.AccountId, fileId, "user-1");

        Assert.NotNull(result);
        Assert.Equal(fileId, result!.FileMetadataId);
        Assert.Equal(account.AccountId, result.AccountId);
        Assert.Equal("user-1", result.AttachedByUserId);
    }

    [Fact]
    public async Task AttachFileToAccount_DefaultsToOtherFileType()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "Default Type",
            Description = "",
            AccountType = DtoAccountType.CheckingAccount,
            Archived = false,
        });

        var result = await service.AttachFileToAccount(account.AccountId, Guid.NewGuid(), "user-1");

        Assert.NotNull(result);
        Assert.Equal(ContextAccountFileType.Other, result!.FileType);
    }

    [Fact]
    public async Task AttachFileToAccount_PersistsValidityMetadata()
    {
        await using var context = TestContextFactory.Create();
        await using var journal = TestContextFactory.CreateJournal();
        var service = new AccountService(context, TestContextFactory.ContactLookup(journal));

        var account = await service.Create(new NewAccount
        {
            Name = "Insured",
            Description = "",
            AccountType = DtoAccountType.Property,
            Archived = false,
        });

        var issuer = new Contact { ExternalUid = $"urn:uuid:{Guid.NewGuid()}", NormalizedName = "ACME INSURANCE", Type = ContactType.Organization, OrganizationDetails = new() { LegalName = "Acme Insurance" } };
        journal.Contacts.Add(issuer);
        await journal.SaveChangesAsync();

        var validFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var validTo = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var issuedAt = new DateTime(2025, 12, 15, 0, 0, 0, DateTimeKind.Utc);

        var result = await service.AttachFileToAccount(account.AccountId, Guid.NewGuid(), "user-1",
            DtoAccountFileType.InsurancePolicy,
            new AttachAccountFileRequest(Guid.NewGuid(), DtoAccountFileType.InsurancePolicy, validFrom, validTo, issuedAt, issuer.ContactId));

        Assert.NotNull(result);
        Assert.Equal(ContextAccountFileType.InsurancePolicy, result!.FileType);
        Assert.Equal(validFrom, result.ValidFrom);
        Assert.Equal(validTo, result.ValidTo);
        Assert.Equal(issuedAt, result.IssuedAt);
        Assert.Equal(issuer.ContactId, result.IssuedBy);
    }

    [Fact]
    public async Task AttachFileToAccount_WithUnknownIssuer_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "Bad Issuer",
            Description = "",
            AccountType = DtoAccountType.Property,
            Archived = false,
        });

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AttachFileToAccount(account.AccountId, Guid.NewGuid(), "user-1",
                DtoAccountFileType.InsurancePolicy,
                new AttachAccountFileRequest(Guid.NewGuid(), DtoAccountFileType.InsurancePolicy, IssuedBy: Guid.NewGuid())));
    }

    [Fact]
    public async Task UpdateAccountFileType_UpdatesValidityMetadata()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "Update Validity",
            Description = "",
            AccountType = DtoAccountType.Vehicle,
            Archived = false,
        });

        var fileId = Guid.NewGuid();
        await service.AttachFileToAccount(account.AccountId, fileId, "user-1", DtoAccountFileType.Warranty);

        var validTo = new DateTime(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var updated = await service.UpdateAccountFileType(account.AccountId, fileId,
            new UpdateAccountFileRequest { FileType = DtoAccountFileType.Warranty, ValidTo = validTo });

        Assert.NotNull(updated);
        Assert.Equal(validTo, updated!.ValidTo);
    }

    [Theory]
    [InlineData(DtoAccountFileType.Message)]
    [InlineData(DtoAccountFileType.Statement)]
    [InlineData(DtoAccountFileType.Contract)]
    [InlineData(DtoAccountFileType.Tax)]
    [InlineData(DtoAccountFileType.Other)]
    public async Task AttachFileToAccount_PersistsEachFileType(DtoAccountFileType fileType)
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "Typed Files",
            Description = "",
            AccountType = DtoAccountType.CheckingAccount,
            Archived = false,
        });

        var result = await service.AttachFileToAccount(account.AccountId, Guid.NewGuid(), "user-1", fileType);

        Assert.NotNull(result);
        Assert.Equal((ContextAccountFileType)(int)fileType, result!.FileType);
    }

    [Fact]
    public async Task AttachFileToAccount_IsIdempotent()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "Idempotent",
            Description = "",
            AccountType = DtoAccountType.CheckingAccount,
            Archived = false,
        });
        var fileId = Guid.NewGuid();

        await service.AttachFileToAccount(account.AccountId, fileId, "user-1");
        await service.AttachFileToAccount(account.AccountId, fileId, "user-1");

        Assert.Equal(1, context.AccountFiles.Count());
    }

    [Fact]
    public async Task AttachFileToAccount_WhenAccountMissing_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        await Assert.ThrowsAsync<DomainNotFoundException>(() =>
            service.AttachFileToAccount(Guid.NewGuid(), Guid.NewGuid(), "user-1"));
    }

    [Fact]
    public async Task DetachFileFromAccount_RemovesAssociation()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "Detach Test",
            Description = "",
            AccountType = DtoAccountType.CheckingAccount,
            Archived = false,
        });
        var fileId = Guid.NewGuid();

        await service.AttachFileToAccount(account.AccountId, fileId, "user-1");
        Assert.Equal(1, context.AccountFiles.Count());

        await service.DetachFileFromAccount(account.AccountId, fileId);
        Assert.Equal(0, context.AccountFiles.Count());
    }

    [Fact]
    public async Task DetachFileFromAccount_IsNoOpWhenAssociationMissing()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "No Files",
            Description = "",
            AccountType = DtoAccountType.CheckingAccount,
            Archived = false,
        });

        var result = await service.DetachFileFromAccount(account.AccountId, Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAccountFileType_ChangesType()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "Retype",
            Description = "",
            AccountType = DtoAccountType.CheckingAccount,
            Archived = false,
        });
        var fileId = Guid.NewGuid();
        await service.AttachFileToAccount(account.AccountId, fileId, "user-1", DtoAccountFileType.Other);

        var updated = await service.UpdateAccountFileType(account.AccountId, fileId,
            new UpdateAccountFileRequest { FileType = DtoAccountFileType.Statement });

        Assert.NotNull(updated);
        Assert.Equal(ContextAccountFileType.Statement, updated!.FileType);
        Assert.Equal(1, context.AccountFiles.Count());
    }

    [Fact]
    public async Task UpdateAccountFileType_ReturnsNullWhenNotAttached()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "No Association",
            Description = "",
            AccountType = DtoAccountType.CheckingAccount,
            Archived = false,
        });

        var result = await service.UpdateAccountFileType(account.AccountId, Guid.NewGuid(),
            new UpdateAccountFileRequest { FileType = DtoAccountFileType.Tax });

        Assert.Null(result);
    }

    [Fact]
    public async Task SearchFor_PopulatesCurrentInterestRate_LatestInForcePreferringInterestRate()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "Savings",
            Description = "",
            AccountType = DtoAccountType.SavingsAccount,
            CurrencyCode = "USD",
            Archived = false,
        });

        context.AccountTerms.AddRange(
            new AccountTerm { AccountId = account.AccountId, TermKind = Context.TermKind.InterestRate, ValueUnit = Context.TermValueUnit.Percentage, Value = 0.03m, EffectiveFrom = new DateTime(2025, 1, 1), CreatedAtUtc = DateTime.UtcNow },
            new AccountTerm { AccountId = account.AccountId, TermKind = Context.TermKind.InterestRate, ValueUnit = Context.TermValueUnit.Percentage, Value = 0.025m, EffectiveFrom = new DateTime(2026, 1, 1), CreatedAtUtc = DateTime.UtcNow },
            // Future-dated → not yet in force, must be ignored.
            new AccountTerm { AccountId = account.AccountId, TermKind = Context.TermKind.InterestRate, ValueUnit = Context.TermValueUnit.Percentage, Value = 0.01m, EffectiveFrom = DateTime.UtcNow.AddYears(1), CreatedAtUtc = DateTime.UtcNow },
            // A fee in force must never be chosen for the rate.
            new AccountTerm { AccountId = account.AccountId, TermKind = Context.TermKind.ServiceFee, ValueUnit = Context.TermValueUnit.Amount, Value = 5m, CurrencyCode = "USD", EffectiveFrom = new DateTime(2025, 1, 1), CreatedAtUtc = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var dto = (await service.ListAsync(new AccountsQueryParams())).Items.Single(a => a.AccountId == account.AccountId);

        Assert.Equal(0.025m, dto.CurrentInterestRate);
        Assert.Equal(FinanceDtos.TermKind.InterestRate, dto.CurrentInterestRateKind);
    }

    [Fact]
    public async Task SearchFor_FallsBackToExpectedReturn_WhenNoInterestRate()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "Brokerage",
            Description = "",
            AccountType = DtoAccountType.InvestmentAccount,
            CurrencyCode = "USD",
            Archived = false,
        });

        context.AccountTerms.Add(new AccountTerm
        {
            AccountId = account.AccountId,
            TermKind = Context.TermKind.ExpectedReturn,
            ValueUnit = Context.TermValueUnit.Percentage,
            Value = 0.07m,
            EffectiveFrom = new DateTime(2025, 1, 1),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var dto = (await service.ListAsync(new AccountsQueryParams())).Items.Single(a => a.AccountId == account.AccountId);

        Assert.Equal(0.07m, dto.CurrentInterestRate);
        Assert.Equal(FinanceDtos.TermKind.ExpectedReturn, dto.CurrentInterestRateKind);
    }

    [Fact]
    public async Task SearchFor_LeavesCurrentInterestRateNull_WhenNoRateTerms()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        var account = await service.Create(new NewAccount
        {
            Name = "Plain",
            Description = "",
            AccountType = DtoAccountType.SavingsAccount,
            Archived = false,
        });

        var dto = (await service.ListAsync(new AccountsQueryParams())).Items.Single(a => a.AccountId == account.AccountId);

        Assert.Null(dto.CurrentInterestRate);
        Assert.Null(dto.CurrentInterestRateKind);
    }
}
