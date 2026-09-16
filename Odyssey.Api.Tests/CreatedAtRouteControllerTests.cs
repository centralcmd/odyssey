using Odyssey.Core.Journal.Avatar;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Odyssey.Api.Controllers;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;

namespace Odyssey.Api.Tests;

public class CreatedAtRouteControllerTests
{
    /// <summary>
    /// The avatar service a vCard import needs to attach a <c>PHOTO</c>/<c>LOGO</c> (issue #86 §9). A
    /// fixed 64 MB global cap, so the effective avatar cap is the surface constant — <c>min</c> of the
    /// two — exactly as it resolves in production against an untouched setting.
    /// </summary>
    private static ContactAvatarService AvatarServiceFor(Odyssey.Context.OdysseyContext context) =>
        new(context,
            new FileService(context, new FileValidationService()),
            new FixedUploadLimits(64L * 1024 * 1024));

    [Fact]
    public async Task PutAccount_WhenMissing_ReturnsCreatedAtGetAccountRoute()
    {
        await using var context = TestContextFactory.Create();
        await using var journalContext = TestContextFactory.CreateJournal();
        var controller = new AccountController(
            NullLogger<AccountController>.Instance,
            new AccountService(context, new ContactLookup(journalContext)),
            null!,
            null!,
            new AccountTotalsService(context, new CurrencyConversionService(context)),
            new NetWorthHistoryService(context, new CurrencyConversionService(context)),
            TimeProvider.System);

        var missingId = Guid.NewGuid();
        var result = await controller.Put(missingId, new NewAccount
        {
            Name = "Checking",
            Description = "Main",
            AccountType = AccountType.CheckingAccount,
            Archived = false,
        });

        var createdResult = Assert.IsType<CreatedAtRouteResult>(result);
        Assert.Equal("GetAccount", createdResult.RouteName);
        Assert.NotEqual(missingId, Assert.IsType<Guid>(createdResult.RouteValues!["id"]));
    }

    [Fact]
    public async Task PutBudget_WhenMissing_ReturnsCreatedAtGetBudgetRoute()
    {
        await using var context = TestContextFactory.Create();
        await using var journalContext = TestContextFactory.CreateJournal();
        var controller = new BudgetController(NullLogger<BudgetController>.Instance, new BudgetService(context, new ContactLookup(journalContext)));

        var missingId = Guid.NewGuid();
        var result = await controller.Put(missingId, new NewBudget
        {
            Name = "Monthly",
            Description = "Budget",
            StartDate = DateTime.UtcNow.Date,
            EndDate = DateTime.UtcNow.Date.AddDays(30),
            Archived = false,
        });

        var createdResult = Assert.IsType<CreatedAtRouteResult>(result);
        Assert.Equal("GetBudget", createdResult.RouteName);
        Assert.NotEqual(missingId, Assert.IsType<Guid>(createdResult.RouteValues!["id"]));
    }

    [Fact]
    public async Task PutContact_WhenMissing_ReturnsCreatedAtGetContactRoute()
    {
        await using var context = TestContextFactory.Create();
        await using var journalContext = TestContextFactory.CreateJournal();
        var referenceGuard = new ContactReferenceGuard(context);
        var contactService = new ContactService(journalContext, referenceGuard);
        var controller = new ContactController(
            NullLogger<ContactController>.Instance, contactService,
            new ContactVCardService(
                journalContext, contactService, AvatarServiceFor(journalContext),
                new FakeImportExportLimitsLookup(), NullLogger<ContactVCardService>.Instance),
            AvatarServiceFor(journalContext),
            referenceGuard);

        var missingId = Guid.NewGuid();
        var result = await controller.Put(missingId, new NewContact
        {
            Type = ContactType.Organization,
            Notes = "Vendor",
            Archived = false,
            OrganizationDetails = new() { LegalName = "Store" },
        });

        var createdResult = Assert.IsType<CreatedAtRouteResult>(result);
        Assert.Equal("GetContact", createdResult.RouteName);
        Assert.NotEqual(missingId, Assert.IsType<Guid>(createdResult.RouteValues!["id"]));
    }

    [Fact]
    public async Task PutBudgetItem_WhenMissing_ReturnsCreatedAtGetBudgetItemRoute()
    {
        await using var context = TestContextFactory.Create();
        await using var journalContext = TestContextFactory.CreateJournal();
        var budget = await new BudgetService(context, new ContactLookup(journalContext)).Create(new NewBudget
        {
            Name = "Monthly",
            Description = "Budget",
            StartDate = DateTime.UtcNow.Date,
            EndDate = DateTime.UtcNow.Date.AddDays(30),
            Archived = false,
        });

        // A budget item is a plan for a TAG (issue #75), so the upsert needs a real one to name.
        var tag = new Odyssey.Context.TransactionTag { Name = "Groceries", Description = "Food" };
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();

        var controller = new BudgetItemController(NullLogger<BudgetItemController>.Instance, new BudgetItemService(context));
        var missingId = Guid.NewGuid();
        var result = await controller.Put(missingId, new NewBudgetItem
        {
            BudgetId = budget.BudgetId,
            CategoryType = BudgetCategoryType.Expense,
            PlannedAmount = 100,
            TransactionTagId = tag.TransactionTagId,
        });

        var createdResult = Assert.IsType<CreatedAtRouteResult>(result);
        Assert.Equal("GetBudgetItem", createdResult.RouteName);
        Assert.NotEqual(missingId, Assert.IsType<Guid>(createdResult.RouteValues!["id"]));
    }

    [Fact]
    public async Task PutTransaction_WhenMissing_ReturnsCreatedAtGetTransactionRoute()
    {
        await using var context = TestContextFactory.Create();
        await using var journalContext = TestContextFactory.CreateJournal();
        var account = await new AccountService(context, new ContactLookup(journalContext)).Create(new NewAccount
        {
            Name = "Checking",
            Description = "Main",
            AccountType = AccountType.CheckingAccount,
            Archived = false,
        });

        var controller = new TransactionController(NullLogger<TransactionController>.Instance, new TransactionService(context, new ContactLookup(journalContext)), null!);
        var missingId = Guid.NewGuid();
        var result = await controller.Put(missingId, new NewTransaction
        {
            Description = "Lunch",
            Amount = 12.5m,
            AccountId = account.AccountId,
            Status = TransactionStatus.New,
        });

        var createdResult = Assert.IsType<CreatedAtRouteResult>(result);
        Assert.Equal("GetTransaction", createdResult.RouteName);
        Assert.NotEqual(missingId, Assert.IsType<Guid>(createdResult.RouteValues!["id"]));
    }
}
