using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// <see cref="PropertyService"/>'s homeowner-association rules for direct (non-HTTP) callers
/// (issue #217 §8.2–§8.3) and the batched read-path resolution (§10, AC 15).
/// </summary>
public class PropertyHomeownerAssociationServiceTests
{
    private const string AssociationKey = "RealEstateDetails.HomeownerAssociationId";

    [Fact]
    public async Task List_WithManyPropertiesSharingFewAssociations_ResolvesThemInOneBatchedLookup()
    {
        await using var context = TestContextFactory.Create();
        var lookup = new CountingContactLookup(TestContextFactory.ContactLookup(context));
        var service = new PropertyService(context, lookup);
        var first = SeedContact(context, "First Borettslag");
        var second = SeedContact(context, "Second Sameie");
        await context.SaveChangesAsync();

        foreach (var (name, association) in new[]
                 {
                     ("A", first), ("B", first), ("C", second), ("D", second), ("E", (Guid?)null),
                 })
        {
            await service.Create(Flat(name, association), userId: null);
        }

        lookup.Reset();

        var rows = (await service.ListAsync(new PropertiesQueryParams())).Items;

        Assert.Equal(1, lookup.ResolveRefsCalls);
        Assert.Equal(new[] { first, second }.Order(), lookup.LastIds.Order());
        Assert.Equal("First Borettslag", rows.Single(r => r.Name == "B").HomeownerAssociation!.Name);
        Assert.Equal("Second Sameie", rows.Single(r => r.Name == "C").HomeownerAssociation!.Name);
        Assert.Null(rows.Single(r => r.Name == "E").HomeownerAssociation);
    }

    [Fact]
    public async Task List_WithNoLinks_MakesNoLookup()
    {
        await using var context = TestContextFactory.Create();
        var lookup = new CountingContactLookup(TestContextFactory.ContactLookup(context));
        var service = new PropertyService(context, lookup);
        await service.Create(Flat("A", null), userId: null);
        await service.Create(PropertyTestData.Car(), userId: null);
        lookup.Reset();

        await service.ListAsync(new PropertiesQueryParams());

        Assert.Equal(0, lookup.ResolveRefsCalls);
    }

    [Fact]
    public async Task Get_WhenTheStoredIdNoLongerResolves_ReturnsNullProjection()
    {
        await using var context = TestContextFactory.Create();
        var service = new PropertyService(context, TestContextFactory.ContactLookup(context));
        var association = SeedContact(context, "Gone Sameie");
        await context.SaveChangesAsync();
        var id = (await service.Create(Flat("A", association), userId: null)).PropertyId;

        // The InMemory provider enforces no FK, so the dangling id survives here — the case the read
        // path must tolerate if a delete ever bypasses the guard.
        // Untracked first, or EF's client-side set-null would clear the tracked detail row as well.
        context.ChangeTracker.Clear();
        context.Contacts.Remove(context.Contacts.Single(c => c.ContactId == association));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var property = await service.Get(id);

        Assert.Equal(association, property!.RealEstateDetails!.HomeownerAssociationId);
        Assert.Null(property.HomeownerAssociation);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("archived")]
    public async Task Create_WithAnInvalidTarget_ThrowsValidation_KeyedOnTheField(string target)
    {
        await using var context = TestContextFactory.Create();
        var service = new PropertyService(context, TestContextFactory.ContactLookup(context));
        var id = target switch
        {
            "empty" => Guid.Empty,
            "unknown" => Guid.NewGuid(),
            _ => SeedContact(context, "Archived Sameie", archived: true),
        };
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(Flat("A", id), userId: null));

        Assert.Contains(AssociationKey, ex.Errors!.Keys);
        Assert.Empty(context.Properties);
    }

    [Fact]
    public async Task Create_WithAPerson_ThrowsUnprocessable_KeyedOnTheField()
    {
        await using var context = TestContextFactory.Create();
        var service = new PropertyService(context, TestContextFactory.ContactLookup(context));
        var person = SeedContact(context, "Kari Nordmann", ContactType.Person);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => service.Create(Flat("A", person), userId: null));

        Assert.Contains(AssociationKey, ex.Errors!.Keys);
        Assert.DoesNotContain("Kari", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_KeepingAStaleLink_IsNotRevalidated()
    {
        await using var context = TestContextFactory.Create();
        var lookup = new CountingContactLookup(TestContextFactory.ContactLookup(context));
        var service = new PropertyService(context, lookup);
        var association = SeedContact(context, "Storgata Borettslag");
        await context.SaveChangesAsync();
        var id = (await service.Create(Flat("A", association), userId: null)).PropertyId;

        var contact = context.Contacts.Single(c => c.ContactId == association);
        contact.Type = ContactType.Person;
        contact.Archived = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var updated = await service.Update(id, Flat("Renamed", association), userId: null);

        Assert.Equal("Renamed", updated!.Name);
        Assert.Equal(association, updated.RealEstateDetails!.HomeownerAssociationId);
    }

    private static NewProperty Flat(string name, Guid? association) => PropertyTestData.House(name) with
    {
        RealEstateDetails = PropertyTestData.House(name).RealEstateDetails! with
        {
            Kind = RealEstateKind.Apartment,
            HomeownerAssociationId = association,
        },
    };

    private static Guid SeedContact(
        OdysseyContext context, string name, ContactType type = ContactType.Organization, bool archived = false)
    {
        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = name.ToUpperInvariant(),
            Type = type,
            Archived = archived ? DateTime.UtcNow : null,
        };
        if (type == ContactType.Organization)
        {
            contact.OrganizationDetails = new() { LegalName = name };
        }
        else
        {
            var parts = name.Split(' ', 2);
            contact.PersonDetails = new() { FirstName = parts[0], LastName = parts[1] };
        }

        context.Contacts.Add(contact);
        return contact.ContactId;
    }

    /// <summary>Counts <see cref="IContactLookup.ResolveRefsAsync"/> calls; everything else delegates.</summary>
    private sealed class CountingContactLookup(IContactLookup inner) : IContactLookup
    {
        public int ResolveRefsCalls { get; private set; }

        public IReadOnlyCollection<Guid> LastIds { get; private set; } = [];

        public void Reset()
        {
            ResolveRefsCalls = 0;
            LastIds = [];
        }

        public Task<IReadOnlyDictionary<Guid, ContactRef>> ResolveRefsAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
        {
            ResolveRefsCalls++;
            LastIds = [.. ids];
            return inner.ResolveRefsAsync(ids, cancellationToken);
        }

        public Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            inner.ExistingIdsAsync(ids, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, ContactEmbed>> ResolveContactsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            inner.ResolveContactsAsync(ids, cancellationToken);

        public Task<IReadOnlyList<Guid>> SearchIdsByNameAsync(string term, CancellationToken cancellationToken = default) =>
            inner.SearchIdsByNameAsync(term, cancellationToken);

        public Task<IReadOnlyList<ContactRef>> ListActiveContactRefsAsync(CancellationToken cancellationToken = default) =>
            inner.ListActiveContactRefsAsync(cancellationToken);

        public Task<IReadOnlySet<Guid>> ExistingPersonIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            inner.ExistingPersonIdsAsync(ids, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, string>> ResolveExternalUidsAsync(IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken = default) =>
            inner.ResolveExternalUidsAsync(contactIds, cancellationToken);

        public Task<IReadOnlyDictionary<string, Guid>> ResolveIdsByExternalUidAsync(IReadOnlyCollection<string> externalUids, CancellationToken cancellationToken = default) =>
            inner.ResolveIdsByExternalUidAsync(externalUids, cancellationToken);
    }
}
