using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using AccountType = Odyssey.Context.AccountType;
using BudgetCategoryType = Odyssey.Context.BudgetCategoryType;
using ContractFileType = Odyssey.Context.ContractFileType;
using ContractType = Odyssey.Dtos.Finance.ContractType;
using InsurancePolicyType = Odyssey.Dtos.Finance.InsurancePolicyType;
using PolicyFileType = Odyssey.Context.PolicyFileType;
using TaxStatementFileType = Odyssey.Context.TaxStatementFileType;

namespace Odyssey.Api.Tests;

/// <summary>
/// The MUTATION endpoints that return a file-bearing body, and the budget report — the surfaces the
/// PR review found wired but unasserted.
/// </summary>
/// <remarks>
/// <para>
/// Read endpoints are covered by <see cref="FinanceFileAttributionApiTests"/> and
/// <see cref="FinanceFileAttributionModuleApiTests"/>. The gap was that a `PUT`/`PATCH` returning the
/// updated record returns its FILES too, so dropping the enricher there ships bare ids from a surface
/// no read test touches.
/// </para>
/// <para>
/// CREATE endpoints are deliberately absent, and that is not an omission: no create DTO
/// (<c>NewContract</c>, <c>NewTaxStatement</c>, <c>NewInsurancePolicy</c>, <c>NewPolicyRenewal</c>)
/// accepts a file, so a created record's file collection is necessarily empty and no assertion over
/// it could distinguish an enriched response from an unenriched one. They are covered structurally
/// instead, by the per-action source guard in <see cref="FinanceFileAttributionGuardTests"/>.
/// </para>
/// </remarks>
public sealed class FinanceFileAttributionWriteApiTests
{
    private const string UploaderUserId = "write-uploader-id";
    private const string UploaderEmail = "write-uploader@example.com";
    private const string DisplayName = "Edsger D.";

    [Fact]
    public async Task PutContract_ResolvesFileAttribution()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.ContractsRead, PermissionClaims.ContractsUpdate]);
        using var client = factory.CreateClient();
        var contractId = await SeedContractAsync(factory);

        var response = await client.PutAsJsonAsync(
            $"/api/contracts/{contractId}",
            new UpdateContract { Name = "Lease (renewed)", Type = ContractType.Other });
        response.EnsureSuccessStatusCode();
        var contract = await response.Content.ReadFromJsonAsync<ExistingContract>();

        var file = Assert.Single(contract!.Files);
        Assert.Equal(DisplayName, file.AttachedByName);
        Assert.Equal(DisplayName, file.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task PutTaxStatement_ResolvesFileAttribution()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.TaxesRead, PermissionClaims.TaxesUpdate]);
        using var client = factory.CreateClient();
        var statementId = await SeedTaxStatementAsync(factory);

        var response = await client.PutAsJsonAsync(
            $"/api/tax-statements/{statementId}",
            new UpdateTaxStatement
            {
                Name = "2025 return (amended)",
                FiscalYear = 2025,
                StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            });
        response.EnsureSuccessStatusCode();
        var statement = await response.Content.ReadFromJsonAsync<ExistingTaxStatement>();

        var file = Assert.Single(statement!.Files);
        Assert.Equal(DisplayName, file.AttachedByName);
        Assert.Equal(DisplayName, file.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task PatchTaxStatementStatus_ResolvesFileAttribution()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.TaxesRead, PermissionClaims.TaxesUpdate]);
        using var client = factory.CreateClient();
        var statementId = await SeedTaxStatementAsync(factory);

        var response = await client.PatchAsJsonAsync(
            $"/api/tax-statements/{statementId}/status",
            new UpdateTaxStatementStatus { Status = TaxStatementStatus.Approved });
        response.EnsureSuccessStatusCode();
        var statement = await response.Content.ReadFromJsonAsync<ExistingTaxStatement>();

        var file = Assert.Single(statement!.Files);
        Assert.Equal(DisplayName, file.AttachedByName);
        Assert.Equal(DisplayName, file.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task PutTaxStatementTags_ResolvesFileAttribution()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.TaxesRead, PermissionClaims.TaxesUpdate]);
        using var client = factory.CreateClient();
        var statementId = await SeedTaxStatementAsync(factory);

        var response = await client.PutAsJsonAsync(
            $"/api/tax-statements/{statementId}/tags",
            new UpdateTaxStatementTags());
        response.EnsureSuccessStatusCode();
        var statement = await response.Content.ReadFromJsonAsync<ExistingTaxStatement>();

        var file = Assert.Single(statement!.Files);
        Assert.Equal(DisplayName, file.AttachedByName);
        Assert.Equal(DisplayName, file.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task PutInsurancePolicy_ResolvesFileAttributionOnBothRenewalViews()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.InsuranceRead, PermissionClaims.InsuranceUpdate]);
        using var client = factory.CreateClient();
        var (policyId, _) = await SeedInsurancePolicyAsync(factory);

        var response = await client.PutAsJsonAsync(
            $"/api/insurance-policies/{policyId}",
            new UpdateInsurancePolicy { Name = "Home (revised)", Type = InsurancePolicyType.Other });
        response.EnsureSuccessStatusCode();
        var policy = await response.Content.ReadFromJsonAsync<ExistingInsurancePolicy>();

        var listed = Assert.Single(Assert.Single(policy!.Renewals).Files);
        Assert.Equal(DisplayName, listed.AttachedByName);
        Assert.Equal(DisplayName, listed.FileMetadata.UploadedByName);

        Assert.NotNull(policy.CurrentRenewal);
        var current = Assert.Single(policy.CurrentRenewal.Files);
        Assert.Equal(DisplayName, current.AttachedByName);
        Assert.Equal(DisplayName, current.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task PutPolicyRenewal_ResolvesFileAttribution()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.InsuranceRead, PermissionClaims.InsuranceUpdate]);
        using var client = factory.CreateClient();
        var (policyId, renewalId) = await SeedInsurancePolicyAsync(factory);

        var response = await client.PutAsJsonAsync(
            $"/api/insurance-policies/{policyId}/renewals/{renewalId}",
            new UpdatePolicyRenewal
            {
                FromDate = DateTime.UtcNow.Date.AddDays(-30),
                ToDate = DateTime.UtcNow.Date.AddDays(330),
                Premium = 1300m,
                CoverageAmount = 500_000m,
            });
        response.EnsureSuccessStatusCode();
        var renewal = await response.Content.ReadFromJsonAsync<ExistingPolicyRenewal>();

        var file = Assert.Single(renewal!.Files);
        Assert.Equal(DisplayName, file.AttachedByName);
        Assert.Equal(DisplayName, file.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task PostInsurancePolicyParty_ResolvesFileAttribution()
    {
        // The party endpoints return the WHOLE policy, renewals and files included, so they carry the
        // same disclosure as a read even though the caller is writing a link.
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.InsuranceRead, PermissionClaims.InsuranceUpdate]);
        using var client = factory.CreateClient();
        var (policyId, _) = await SeedInsurancePolicyAsync(factory);
        var contactId = await SeedContactAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"/api/insurance-policies/{policyId}/parties",
            new InsurancePolicyPartyRequest { Role = InsurancePartyRole.Insurer, TargetId = contactId });
        response.EnsureSuccessStatusCode();
        var policy = await response.Content.ReadFromJsonAsync<ExistingInsurancePolicy>();

        var file = Assert.Single(Assert.Single(policy!.Renewals).Files);
        Assert.Equal(DisplayName, file.AttachedByName);
        Assert.Equal(DisplayName, file.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task PutInsurancePolicyParty_ResolvesFileAttribution()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.InsuranceRead, PermissionClaims.InsuranceUpdate]);
        using var client = factory.CreateClient();
        var (policyId, _) = await SeedInsurancePolicyAsync(factory);
        var contactId = await SeedContactAsync(factory);

        (await client.PostAsJsonAsync(
            $"/api/insurance-policies/{policyId}/parties",
            new InsurancePolicyPartyRequest { Role = InsurancePartyRole.Insurer, TargetId = contactId }))
            .EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            $"/api/insurance-policies/{policyId}/parties/{InsurancePartyRole.Insurer}/{contactId}",
            new InsurancePolicyPartyRequest
            {
                Role = InsurancePartyRole.Insurer,
                TargetId = contactId,
                FromDate = DateTime.UtcNow.Date,
            });
        response.EnsureSuccessStatusCode();
        var policy = await response.Content.ReadFromJsonAsync<ExistingInsurancePolicy>();

        var file = Assert.Single(Assert.Single(policy!.Renewals).Files);
        Assert.Equal(DisplayName, file.AttachedByName);
        Assert.Equal(DisplayName, file.FileMetadata.UploadedByName);
    }

    /// <summary>
    /// The budget report is wired for enrichment but cannot exercise it today, and this test pins BOTH
    /// halves of that so the surface stops being an undocumented latent risk.
    /// </summary>
    /// <remarks>
    /// <c>BudgetService.GetTransactions</c> does not <c>Include</c> the transaction files, so the
    /// report's transactions come back with none — even here, where the matching transaction demonstrably
    /// HAS one attached. The first assertion is a tripwire: adding that <c>Include</c> makes it fail,
    /// which is the intended prompt to check enrichment rather than discovering a bare id in production.
    /// The second assertion is the one that survives that change and starts doing real work.
    /// </remarks>
    [Fact]
    public async Task GetBudgetTransactions_CarriesNoFilesToday_AndEnrichesAnyItDoesCarry()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionsRead]);
        using var client = factory.CreateClient();
        var budgetId = await SeedBudgetWithFiledTransactionAsync(factory);

        var report = await client.GetFromJsonAsync<BudgetReport>($"/api/budgets/{budgetId}/transactions");

        var transaction = Assert.Single(report!.Transactions);
        Assert.True(
            transaction.TransactionFiles.Count == 0,
            "The budget report now carries transaction files. That is a real change to what this "
            + "endpoint discloses: confirm BudgetController.GetBudgetTransactions still enriches them "
            + "(issue #106), then replace this tripwire with the resolved-name assertions used by the "
            + "other surfaces.");

        Assert.All(
            report.Transactions.SelectMany(t => t.TransactionFiles),
            file => Assert.NotNull(file.AttachedByName));
    }

    private static async Task<Guid> SeedContractAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        SeedUploader(db);

        var contract = new Contract { Name = "Lease", CreatedAtUtc = DateTime.UtcNow };
        db.Contracts.Add(contract);
        var metadata = AddFile(db, "lease.pdf", "hash-w-contract");
        await db.SaveChangesAsync();

        db.ContractFiles.Add(new ContractFile
        {
            ContractId = contract.ContractId,
            FileMetadataId = metadata.Id,
            AttachedByUserId = UploaderUserId,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = ContractFileType.Signed,
        });
        await db.SaveChangesAsync();
        return contract.ContractId;
    }

    private static async Task<Guid> SeedTaxStatementAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        SeedUploader(db);

        var statement = new TaxStatement
        {
            Name = "2025 return",
            FiscalYear = 2025,
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.TaxStatements.Add(statement);
        var metadata = AddFile(db, "return.pdf", "hash-w-tax");
        await db.SaveChangesAsync();

        db.TaxStatementFiles.Add(new TaxStatementFile
        {
            TaxStatementId = statement.TaxStatementId,
            FileMetadataId = metadata.Id,
            AttachedByUserId = UploaderUserId,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = TaxStatementFileType.TaxReturn,
        });
        await db.SaveChangesAsync();
        return statement.TaxStatementId;
    }

    private static async Task<(Guid PolicyId, Guid RenewalId)> SeedInsurancePolicyAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        SeedUploader(db);

        var policy = new InsurancePolicy { Name = "Home", CreatedAtUtc = DateTime.UtcNow };
        db.InsurancePolicies.Add(policy);
        await db.SaveChangesAsync();

        var renewal = new PolicyRenewal
        {
            InsurancePolicyId = policy.InsurancePolicyId,
            FromDate = DateTime.UtcNow.Date.AddDays(-30),
            ToDate = DateTime.UtcNow.Date.AddDays(300),
            Premium = 1200m,
            CoverageAmount = 500_000m,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.PolicyRenewals.Add(renewal);
        var metadata = AddFile(db, "policy.pdf", "hash-w-policy");
        await db.SaveChangesAsync();

        db.PolicyRenewalFiles.Add(new PolicyRenewalFile
        {
            PolicyRenewalId = renewal.PolicyRenewalId,
            FileMetadataId = metadata.Id,
            AttachedByUserId = UploaderUserId,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = PolicyFileType.PolicyDocument,
        });
        await db.SaveChangesAsync();
        return (policy.InsurancePolicyId, renewal.PolicyRenewalId);
    }

    private static async Task<Guid> SeedContactAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "northern mutual",
            Type = Odyssey.Dtos.ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Northern Mutual" },
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return contact.ContactId;
    }

    private static async Task<Guid> SeedBudgetWithFiledTransactionAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        SeedUploader(db);

        var account = new Account
        {
            Name = "Everyday Checking",
            Description = "Primary",
            Opened = DateTime.UtcNow,
            AccountType = AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        db.Accounts.Add(account);

        var tag = new TransactionTag { Name = "groceries" };
        db.TransactionTags.Add(tag);

        var budget = new Budget
        {
            Name = "2026",
            StartDate = DateTime.UtcNow.AddDays(-10),
            EndDate = DateTime.UtcNow.AddDays(10),
            BaseCurrencyCode = "USD",
        };
        db.Budgets.Add(budget);
        await db.SaveChangesAsync();

        db.BudgetItems.Add(new BudgetItem
        {
            BudgetId = budget.BudgetId,
            TransactionTagId = tag.TransactionTagId,
            PlannedAmount = 100m,
            CategoryType = BudgetCategoryType.Expense,
        });

        var transaction = new Transaction
        {
            AccountId = account.AccountId,
            Amount = 12.5m,
            CurrencyCode = "USD",
            Description = "Lunch",
            TimeStamp = DateTime.UtcNow,
        };
        transaction.TransactionTags.Add(tag);
        db.Transactions.Add(transaction);

        var metadata = AddFile(db, "receipt.pdf", "hash-w-budget");
        await db.SaveChangesAsync();

        db.TransactionFiles.Add(new TransactionFile
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.TransactionId,
            FileMetadataId = metadata.Id,
            AttachedByUserId = UploaderUserId,
            AttachedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return budget.BudgetId;
    }

    // The write paths validate the currency against the Currencies table (the read paths do not), so a
    // PUT would 400 without this even though the seeded rows carry a valid code.
    private static void SeedCurrency(OdysseyContext db)
    {
        if (!db.Currencies.Any(c => c.CurrencyCode == "USD"))
        {
            db.Currencies.Add(new Currency
            {
                CurrencyCode = "USD",
                Name = "US Dollar",
                MinorUnits = 2,
                Symbol = "$",
            });
        }
    }

    private static void SeedUploader(OdysseyContext db)
    {
        SeedCurrency(db);

        db.Users.Add(new ApplicationUser
        {
            Id = UploaderUserId,
            UserName = UploaderEmail,
            Email = UploaderEmail,
        });
        db.UserProfiles.Add(new UserProfile { UserId = UploaderUserId, DisplayName = DisplayName });
    }

    private static FileMetadata AddFile(OdysseyContext db, string fileName, string hash)
    {
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        db.FileBlob.Add(blob);
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = UploaderUserId,
            FileName = fileName,
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = hash,
            FileBlobId = blob.Id,
            UploadedAtUtc = DateTime.UtcNow,
        };
        db.FileMetadata.Add(metadata);
        return metadata;
    }
}
