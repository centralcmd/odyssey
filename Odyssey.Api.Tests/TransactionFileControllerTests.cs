using System.Security.Claims;
using Odyssey.Api.Controllers;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Journal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FinanceDtos = Odyssey.Dtos.Finance;
using Odyssey.Core.Finance;
using Context = Odyssey.Context;

namespace Odyssey.Api.Tests;

public class TransactionFileControllerTests
{
    private static (TransactionController controller, OdysseyContext financeContext) CreateControllerWithContexts()
    {
        var financeOptions = new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var financeContext = new OdysseyContext(financeOptions);
        financeContext.Currencies.AddRange(
            new Currency { CurrencyCode = "USD", Name = "US Dollar", MinorUnits = 2, Symbol = "$" },
            new Currency { CurrencyCode = "EUR", Name = "Euro", MinorUnits = 2, Symbol = "€" },
            new Currency { CurrencyCode = "SEK", Name = "Swedish Krona", MinorUnits = 2, Symbol = "kr" });
        financeContext.SaveChanges();
        var journalContext = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var fileService = new FileService(financeContext, new FileValidationService());
        var transactionService = new TransactionService(financeContext, new ContactLookup(journalContext));
        var controller = new TransactionController(NullLogger<TransactionController>.Instance, transactionService, fileService);

        // Provide a stable user identity so controller can resolve userId
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "test-user")
                }, "test"))
            }
        };

        return (controller, financeContext);
    }

    [Fact]
    public async Task AttachAndListAndDetachFile_EndToEnd()
    {
        var (controller, financeContext) = CreateControllerWithContexts();

        var account = new Account
        {
            Name = "Test Account",
            Description = "For testing",
            AccountType = Context.AccountType.CheckingAccount,
            Opened = DateTime.UtcNow,
        };
        financeContext.Accounts.Add(account);

        var fileBlob = new FileBlob { Id = Guid.NewGuid(), Content = new byte[] { 1, 2, 3 } };
        var fileMetadataId = Guid.NewGuid();
        var transaction = new Transaction
        {
            AccountId = account.AccountId,
            Amount = 1,
            ContactId = Guid.NewGuid(),
            CurrencyCode = "USD",
            Description = "Transaction",
            TimeStamp = DateTime.UtcNow,
            TransactionFiles = [
                new TransactionFile
                {
                    Id = Guid.NewGuid(),
                    TransactionId = default,
                    Transaction = null,
                    FileMetadataId = fileMetadataId,
                    FileMetadata = new FileMetadata
                    {
                        Id = fileMetadataId,
                        UploadedByUserId = "test-user",
                        FileName = "receipt.pdf",
                        ContentType = "application/pdf",
                        SizeBytes = 1234,
                        Sha256Hash = "abc",
                        UploadedAtUtc = DateTime.UtcNow,
                        Description = "Test file",
                        FileBlobId = fileBlob.Id,
                        FileBlob = fileBlob,
                    },
                    AttachedByUserId = "test-user",
                    AttachedAtUtc = DateTime.UtcNow
                }
            ]
        };
        financeContext.Add(transaction);
        await financeContext.SaveChangesAsync();

        var attachResult = await controller.AttachTransactionFile(transaction.TransactionId, new AttachTransactionFileRequest(fileMetadataId));
        Assert.IsType<NoContentResult>(attachResult);

        var listResult = await controller.GetTransactionFiles(transaction.TransactionId);
        var okResult = Assert.IsType<OkObjectResult>(listResult);
        var returned = Assert.IsType<List<ExistingTransactionFile>>(okResult.Value);

        Assert.Single(returned);
        Assert.Equal(fileMetadataId, returned[0].FileMetadata.Id);
        Assert.Equal("receipt.pdf", returned[0].FileMetadata.FileName);
        Assert.Equal(FinanceDtos.TransactionFileType.Other, returned[0].Type);

        var detachResult = await controller.DetachTransactionFile(transaction.TransactionId, fileMetadataId);
        Assert.IsType<NoContentResult>(detachResult);

        var listAfterDetach = await controller.GetTransactionFiles(transaction.TransactionId);
        var okAfterDetach = Assert.IsType<OkObjectResult>(listAfterDetach);
        var returnedAfterDetach = Assert.IsType<List<ExistingTransactionFile>>(okAfterDetach.Value);
        Assert.Empty(returnedAfterDetach);
    }

    [Fact]
    public async Task AttachFile_WithReceiptType_PersistsCorrectly()
    {
        var (controller, financeContext) = CreateControllerWithContexts();

        var account = new Account
        {
            Name = "Test Account",
            Description = "For testing",
            AccountType = Context.AccountType.CheckingAccount,
            Opened = DateTime.UtcNow,
        };
        financeContext.Accounts.Add(account);

        var fileBlob = new FileBlob { Id = Guid.NewGuid(), Content = new byte[] { 1, 2, 3 } };
        var fileMetadataId = Guid.NewGuid();
        var transaction = new Transaction
        {
            AccountId = account.AccountId,
            Amount = 10,
            CurrencyCode = "USD",
            Description = "Test",
            TimeStamp = DateTime.UtcNow,
            TransactionFiles = [],
        };
        financeContext.Add(transaction);
        financeContext.FileBlob.Add(fileBlob);
        financeContext.FileMetadata.Add(new FileMetadata
        {
            Id = fileMetadataId,
            UploadedByUserId = "test-user",
            FileName = "receipt.pdf",
            ContentType = "application/pdf",
            SizeBytes = 100,
            Sha256Hash = "xyz",
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = fileBlob.Id,
            FileBlob = fileBlob,
        });
        await financeContext.SaveChangesAsync();

        var attachResult = await controller.AttachTransactionFile(
            transaction.TransactionId,
            new AttachTransactionFileRequest(fileMetadataId, FinanceDtos.TransactionFileType.Receipt));
        Assert.IsType<NoContentResult>(attachResult);

        var listResult = await controller.GetTransactionFiles(transaction.TransactionId);
        var okResult = Assert.IsType<OkObjectResult>(listResult);
        var returned = Assert.IsType<List<ExistingTransactionFile>>(okResult.Value);

        Assert.Single(returned);
        Assert.Equal(FinanceDtos.TransactionFileType.Receipt, returned[0].Type);
    }

    [Fact]
    public async Task AttachFile_DefaultType_IsOther()
    {
        var (controller, financeContext) = CreateControllerWithContexts();

        var account = new Account
        {
            Name = "Test Account",
            Description = "For testing",
            AccountType = Context.AccountType.CheckingAccount,
            Opened = DateTime.UtcNow,
        };
        financeContext.Accounts.Add(account);

        var fileBlob = new FileBlob { Id = Guid.NewGuid(), Content = new byte[] { 1 } };
        var fileMetadataId = Guid.NewGuid();
        var transaction = new Transaction
        {
            AccountId = account.AccountId,
            Amount = 5,
            CurrencyCode = "USD",
            Description = "Test",
            TimeStamp = DateTime.UtcNow,
            TransactionFiles = [],
        };
        financeContext.Add(transaction);
        financeContext.FileBlob.Add(fileBlob);
        financeContext.FileMetadata.Add(new FileMetadata
        {
            Id = fileMetadataId,
            UploadedByUserId = "test-user",
            FileName = "doc.pdf",
            ContentType = "application/pdf",
            SizeBytes = 50,
            Sha256Hash = "hash",
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = fileBlob.Id,
            FileBlob = fileBlob,
        });
        await financeContext.SaveChangesAsync();

        // Attach without specifying type — should default to Other
        var attachResult = await controller.AttachTransactionFile(
            transaction.TransactionId,
            new AttachTransactionFileRequest(fileMetadataId));
        Assert.IsType<NoContentResult>(attachResult);

        var listResult = await controller.GetTransactionFiles(transaction.TransactionId);
        var okResult = Assert.IsType<OkObjectResult>(listResult);
        var returned = Assert.IsType<List<ExistingTransactionFile>>(okResult.Value);

        Assert.Single(returned);
        Assert.Equal(FinanceDtos.TransactionFileType.Other, returned[0].Type);
    }

    [Fact]
    public async Task AttachFile_WhenTransactionMissing_ReturnsNotFound()
    {
        var (controller, financeContext) = CreateControllerWithContexts();

        // Create a file entry so file lookup succeeds
        var fileBlob = new FileBlob { Id = Guid.NewGuid(), Content = new byte[] { 1 } };
        financeContext.FileBlob.Add(fileBlob);
        financeContext.FileMetadata.Add(new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = "test-user",
            FileName = "a.txt",
            ContentType = "text/plain",
            SizeBytes = 1,
            Sha256Hash = "a",
            FileBlobId = fileBlob.Id,
            UploadedAtUtc = DateTime.UtcNow,
        });
        await financeContext.SaveChangesAsync();

        var result = await controller.AttachTransactionFile(Guid.NewGuid(), new AttachTransactionFileRequest(fileBlob.Id));
        var notFound = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.IsType<ProblemDetails>(notFound.Value);
    }

    [Fact]
    public async Task AttachFile_WhenFileMissing_ReturnsNotFound()
    {
        var (controller, financeContext) = CreateControllerWithContexts();

        var account = new Account
        {
            Name = "Test Account",
            Description = "For testing",
            AccountType = Odyssey.Context.AccountType.CheckingAccount,
            Opened = DateTime.UtcNow,
        };
        financeContext.Accounts.Add(account);
        await financeContext.SaveChangesAsync();

        var journalContext = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var transactionService = new TransactionService(financeContext, new ContactLookup(journalContext));
        var transaction = await transactionService.Create(new NewTransaction
        {
            Description = "Transaction",
            Amount = 1,
            AccountId = account.AccountId,
        });

        var result = await controller.AttachTransactionFile(transaction.TransactionId, new AttachTransactionFileRequest(Guid.NewGuid()));
        var notFound = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.IsType<ProblemDetails>(notFound.Value);
    }
}
