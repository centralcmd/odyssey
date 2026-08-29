using Odyssey.Core;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal;
using Xunit;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

/// <summary>
/// Service-layer coverage for the account custodian link (issue #221): create/update binding, the
/// archived-on-change validation rule (§9), and the explicit read projection that omits the
/// contact free-text Description (§6 data-minimisation).
/// </summary>
public class AccountCustodianServiceTests
{
    private static NewAccount NewAccount(Guid? custodianId = null, string name = "Brokerage") => new()
    {
        Name = name,
        Description = "",
        AccountType = DtoAccountType.InvestmentAccount,
        CurrencyCode = "USD",
        Archived = false,
        CustodianId = custodianId,
    };

    [Fact]
    public async Task Create_WithValidCustodian_PersistsAndResolvesProjection()
    {
        await using var context = TestContextFactory.Create();
        await using var journal = TestContextFactory.CreateJournal();
        var accounts = new AccountService(context, TestContextFactory.ContactLookup(journal));
        var contacts = new ContactService(journal, new NoopContactReferenceGuard());

        var broker = await contacts.Create(new NewContact { Type = ContactType.Organization, Archived = false, OrganizationDetails = new() { LegalName = "Vanguard" } });

        var created = await accounts.Create(NewAccount(broker.ContactId));

        Assert.Equal(broker.ContactId, created.CustodianId);
        Assert.NotNull(created.Custodian);
        Assert.Equal("Vanguard", created.Custodian!.Name);
        Assert.Equal(ContactType.Organization, created.Custodian.Type);
    }

    [Fact]
    public async Task Create_WithNonExistentCustodian_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accounts = new AccountService(context, TestContextFactory.EmptyContactLookup());

        await Assert.ThrowsAsync<DomainValidationException>(
            () => accounts.Create(NewAccount(Guid.NewGuid())));
    }

    [Fact]
    public async Task Create_WithArchivedCustodian_ThrowsWithDistinctMessage()
    {
        await using var context = TestContextFactory.Create();
        await using var journal = TestContextFactory.CreateJournal();
        var accounts = new AccountService(context, TestContextFactory.ContactLookup(journal));
        var contacts = new ContactService(journal, new NoopContactReferenceGuard());

        var archived = await contacts.Create(new NewContact { Type = ContactType.Organization, Archived = true, OrganizationDetails = new() { LegalName = "Old Bank" } });

        var notFound = await Record.ExceptionAsync(() => accounts.Create(NewAccount(Guid.NewGuid())));
        var archivedEx = await Record.ExceptionAsync(() => accounts.Create(NewAccount(archived.ContactId)));

        Assert.IsType<DomainValidationException>(notFound);
        Assert.IsType<DomainValidationException>(archivedEx);
        // The two failure classes must be distinguishable by message (§16-4 / §16-5).
        Assert.NotEqual(notFound!.Message, archivedEx!.Message);
        Assert.Contains("archived", archivedEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_ChangesAndClearsCustodian()
    {
        await using var context = TestContextFactory.Create();
        await using var journal = TestContextFactory.CreateJournal();
        var accounts = new AccountService(context, TestContextFactory.ContactLookup(journal));
        var contacts = new ContactService(journal, new NoopContactReferenceGuard());

        var a = await contacts.Create(new NewContact { Type = ContactType.Organization, Archived = false, OrganizationDetails = new() { LegalName = "Bank A" } });
        var b = await contacts.Create(new NewContact { Type = ContactType.Organization, Archived = false, OrganizationDetails = new() { LegalName = "Bank B" } });

        var account = await accounts.Create(NewAccount(a.ContactId));

        var changed = await accounts.Update(account.AccountId, NewAccount(b.ContactId));
        Assert.Equal(b.ContactId, changed!.CustodianId);
        Assert.Equal("Bank B", changed.Custodian!.Name);

        var cleared = await accounts.Update(account.AccountId, NewAccount(custodianId: null));
        Assert.Null(cleared!.CustodianId);
        Assert.Null(cleared.Custodian);
    }

    [Fact]
    public async Task Update_NoOpResave_PreservesLinkEvenAfterCustodianArchived()
    {
        await using var context = TestContextFactory.Create();
        await using var journal = TestContextFactory.CreateJournal();
        var accounts = new AccountService(context, TestContextFactory.ContactLookup(journal));
        var contacts = new ContactService(journal, new NoopContactReferenceGuard());

        var custodian = await contacts.Create(new NewContact { Type = ContactType.Organization, Archived = false, OrganizationDetails = new() { LegalName = "Provider" } });
        var account = await accounts.Create(NewAccount(custodian.ContactId));

        // Archive the custodian AFTER it was linked.
        await contacts.Update(custodian.ContactId, new NewContact { Type = ContactType.Organization, Archived = true, OrganizationDetails = new() { LegalName = "Provider" } });

        // A resave that does not change CustodianId must succeed and preserve the link (§9 / §16-5a).
        var resaved = await accounts.Update(account.AccountId, NewAccount(custodian.ContactId, name: "Renamed"));

        Assert.NotNull(resaved);
        Assert.Equal(custodian.ContactId, resaved!.CustodianId);
        Assert.NotNull(resaved.Custodian);
        Assert.NotNull(resaved.Custodian!.Archived);
    }

    [Fact]
    public async Task Update_ToArchivedCustodian_Throws()
    {
        await using var context = TestContextFactory.Create();
        await using var journal = TestContextFactory.CreateJournal();
        var accounts = new AccountService(context, TestContextFactory.ContactLookup(journal));
        var contacts = new ContactService(journal, new NoopContactReferenceGuard());

        var archived = await contacts.Create(new NewContact { Type = ContactType.Organization, Archived = true, OrganizationDetails = new() { LegalName = "Old" } });
        var account = await accounts.Create(NewAccount(custodianId: null));

        await Assert.ThrowsAsync<DomainValidationException>(
            () => accounts.Update(account.AccountId, NewAccount(archived.ContactId)));
    }

    [Fact]
    public async Task SearchFor_ResolvesCustodiansAndNullForUnlinked()
    {
        await using var context = TestContextFactory.Create();
        await using var journal = TestContextFactory.CreateJournal();
        var accounts = new AccountService(context, TestContextFactory.ContactLookup(journal));
        var contacts = new ContactService(journal, new NoopContactReferenceGuard());

        var custodian = await contacts.Create(new NewContact { Type = ContactType.Organization, Archived = false, OrganizationDetails = new() { LegalName = "Shared Bank" } });
        var linked1 = await accounts.Create(NewAccount(custodian.ContactId, "L1"));
        var linked2 = await accounts.Create(NewAccount(custodian.ContactId, "L2"));
        var unlinked = await accounts.Create(NewAccount(custodianId: null, "U"));

        var listed = (await accounts.ListAsync(new AccountsQueryParams())).Items;

        Assert.Equal("Shared Bank", listed.Single(a => a.AccountId == linked1.AccountId).Custodian!.Name);
        Assert.Equal("Shared Bank", listed.Single(a => a.AccountId == linked2.AccountId).Custodian!.Name);
        var u = listed.Single(a => a.AccountId == unlinked.AccountId);
        Assert.Null(u.CustodianId);
        Assert.Null(u.Custodian);
    }

    [Fact]
    public async Task Projection_CarriesOrganizationNumber_AndArchivedOnListAndSinglePaths()
    {
        await using var context = TestContextFactory.Create();
        await using var journal = TestContextFactory.CreateJournal();
        var accounts = new AccountService(context, TestContextFactory.ContactLookup(journal));
        var contacts = new ContactService(journal, new NoopContactReferenceGuard());

        // An organization number is part of the slim Custodian projection (§16-3); the archived flag
        // must surface on BOTH the batched list path and the single-get path.
        var custodian = await contacts.Create(new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new() { LegalName = "Pension Co", OrganizationNumber = "ORG-12345" },
        });
        var account = await accounts.Create(NewAccount(custodian.ContactId));

        // Archive the already-linked custodian; the no-op resave path keeps the link (§9).
        await contacts.Update(custodian.ContactId, new NewContact
        {
            Type = ContactType.Organization,
            Archived = true,
            OrganizationDetails = new() { LegalName = "Pension Co", OrganizationNumber = "ORG-12345" },
        });

        var listed = (await accounts.ListAsync(new AccountsQueryParams())).Items.Single(a => a.AccountId == account.AccountId);
        Assert.Equal("ORG-12345", listed.Custodian!.OrganizationNumber);
        Assert.NotNull(listed.Custodian.Archived);

        var single = await accounts.Get(account.AccountId);
        Assert.Equal("ORG-12345", single!.Custodian!.OrganizationNumber);
        Assert.NotNull(single.Custodian.Archived);
    }

    // (Removed Adapt_DoesNotAutoPopulateCustodianOntoDto: it guarded a Mapster Ignore pin for the
    // Account.Custodian navigation, which no longer exists after the Contact move — Account carries only
    // the scalar CustodianId, so Adapt can't populate the DTO's Custodian regardless, and the test could
    // no longer fail. The service still resolves Custodian explicitly via IContactLookup; that behaviour
    // is covered by the resolve/projection tests above.)
}
