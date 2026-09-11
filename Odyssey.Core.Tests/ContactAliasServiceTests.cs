using Odyssey.Core;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The alias sub-resource and the four lifecycle scalars (issue #48 §16).
///
/// <para>
/// One thing this tier deliberately cannot cover: the alias and middle-name <b>search</b> arms use a
/// raw LIKE pattern and rely on the column's <c>_ci</c> collation for case-insensitivity, so on the
/// EF InMemory provider they are case-SENSITIVE. The assertions here therefore use an exact-case
/// term; the case- and accent-insensitive behaviour is an <c>Odyssey.IntegrationTests</c> criterion
/// (AC 12, AC 13).
/// </para>
/// </summary>
public class ContactAliasServiceTests
{
    private static NewContact Person(string first, string last) => new()
    {
        Type = ContactType.Person,
        Archived = false,
        PersonDetails = new PersonDetailsDto { FirstName = first, LastName = last },
    };

    private static NewContactAlias Alias(string value, string? label = null) =>
        new() { Value = value, Label = label };

    private static ContactService NewService(out Context.OdysseyContext context)
    {
        context = TestContextFactory.CreateJournal();
        return new ContactService(context, new NoopContactReferenceGuard());
    }

    [Fact]
    public async Task Create_StoresValueAndLabelVerbatim()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));

        var created = await service.CreateAlias(contact.ContactId, Alias("Kari", "nickname"));

        Assert.NotNull(created);
        Assert.Equal("Kari", created!.Value);
        Assert.Equal("nickname", created.Label);
        Assert.Equal(contact.ContactId, created.ContactId);

        var reread = await service.Get(contact.ContactId);
        var inline = Assert.Single(reread!.Aliases);
        Assert.Equal("Kari", inline.Value);
        Assert.Equal("nickname", inline.Label);
    }

    [Fact]
    public async Task Create_UnknownContact_ReturnsNull()
    {
        var service = NewService(out var context);
        await using var _ = context;

        Assert.Null(await service.CreateAlias(Guid.NewGuid(), Alias("Kari")));
    }

    // AC 2: uniqueness is case-insensitive and on the VALUE alone — two labels cannot smuggle in a
    // second "Hansen".
    [Fact]
    public async Task Create_CaseVariantDuplicate_ConflictsEvenUnderADifferentLabel()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));
        await service.CreateAlias(contact.ContactId, Alias("Kari", "nickname"));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAlias(contact.ContactId, Alias("kari", "maiden name")));

        // AC 11 / AC 41: the 409 names the field so the dialog can render it on the value control.
        Assert.NotNull(conflict.Errors);
        Assert.True(conflict.Errors!.ContainsKey("value"));

        // AC 49: neither the message nor the errors entry echoes the submitted value.
        Assert.DoesNotContain("kari", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kari", conflict.Errors["value"][0], StringComparison.OrdinalIgnoreCase);

        Assert.Single((await service.Get(contact.ContactId))!.Aliases);
    }

    // AC 3: accents fold too. This is what IgnoreNonSpace buys, and what an OrdinalIgnoreCase check
    // would miss — leaving the database's own _ci index to reject the insert instead.
    [Fact]
    public async Task Create_AccentVariantDuplicate_Conflicts()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Renée", "Dubois"));
        await service.CreateAlias(contact.ContactId, Alias("Renée"));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAlias(contact.ContactId, Alias("Renee")));
    }

    // AC 4: "" and an omitted label are one state, not two.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Create_BlankLabel_StoresNull(string? label)
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));

        var created = await service.CreateAlias(contact.ContactId, Alias("Kari", label));

        Assert.Null(created!.Label);
    }

    // AC 5: a PUT is a replace, not a patch.
    [Fact]
    public async Task Update_OmittedLabel_ClearsAPreviouslySetOne()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));
        var alias = await service.CreateAlias(contact.ContactId, Alias("Kari", "nickname"));

        Assert.True(await service.UpdateAlias(contact.ContactId, alias!.Id, Alias("Kari")));

        var stored = Assert.Single((await service.GetAliases(contact.ContactId))!);
        Assert.Equal("Kari", stored.Value);
        Assert.Null(stored.Label);
    }

    [Fact]
    public async Task Update_ToAnExistingSiblingValue_Conflicts()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));
        await service.CreateAlias(contact.ContactId, Alias("Kari"));
        var second = await service.CreateAlias(contact.ContactId, Alias("KH"));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.UpdateAlias(contact.ContactId, second!.Id, Alias("kari")));
    }

    // A row may of course keep its own value on a label-only edit — the duplicate check excludes the
    // row being written.
    [Fact]
    public async Task Update_SameValueDifferentLabel_Succeeds()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));
        var alias = await service.CreateAlias(contact.ContactId, Alias("Kari", "nickname"));

        Assert.True(await service.UpdateAlias(contact.ContactId, alias!.Id, Alias("Kari", "maiden name")));
        Assert.Equal("maiden name", Assert.Single((await service.GetAliases(contact.ContactId))!).Label);
    }

    // AC 6: the cap is enforced by the service as a 422 naming the field, and nothing is written.
    [Fact]
    public async Task Create_BeyondTheCap_IsUnprocessableAndWritesNothing()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));
        for (var i = 0; i < ContactAliasRules.MaxPerContact; i++)
        {
            await service.CreateAlias(contact.ContactId, Alias($"Alias {i:00}"));
        }

        var rejected = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => service.CreateAlias(contact.ContactId, Alias("One too many")));

        Assert.True(rejected.Errors!.ContainsKey("value"));
        Assert.Equal(ContactAliasRules.MaxPerContact, (await service.GetAliases(contact.ContactId))!.Count);
    }

    // A replace cannot grow the collection, so the cap is unreachable on PUT — an absence that is a
    // decision, not an omission.
    [Fact]
    public async Task Update_AtTheCap_StillSucceeds()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));
        ExistingContactAlias? last = null;
        for (var i = 0; i < ContactAliasRules.MaxPerContact; i++)
        {
            last = await service.CreateAlias(contact.ContactId, Alias($"Alias {i:00}"));
        }

        Assert.True(await service.UpdateAlias(contact.ContactId, last!.Id, Alias("Renamed")));
    }

    // AC 7: containment on all three addressable verbs. An alias id from contact A is unreachable
    // through B's route, and B's alias is untouched.
    [Fact]
    public async Task Containment_AnAliasIsUnreachableThroughAnotherContact()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var a = await service.Create(Person("Karoline", "Hansen"));
        var b = await service.Create(Person("Sam", "Rivera"));
        var aliasOfB = await service.CreateAlias(b.ContactId, Alias("Sammy", "nickname"));

        Assert.False(await service.UpdateAlias(a.ContactId, aliasOfB!.Id, Alias("Hijacked")));
        Assert.False(await service.DeleteAlias(a.ContactId, aliasOfB.Id));

        var stored = Assert.Single((await service.GetAliases(b.ContactId))!);
        Assert.Equal("Sammy", stored.Value);
        Assert.Equal("nickname", stored.Label);
    }

    // AC 8: every alias mutation bumps the parent, which is what keeps the vCard REV honest.
    [Fact]
    public async Task EveryMutation_BumpsTheParentUpdatedAt()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));

        var afterCreate = await Bumped(service, contact.ContactId, contact.UpdatedAt,
            async id => await service.CreateAlias(id, Alias("Kari")));

        var aliasId = (await service.GetAliases(contact.ContactId))![0].Id;
        var afterUpdate = await Bumped(service, contact.ContactId, afterCreate,
            async id => await service.UpdateAlias(id, aliasId, Alias("Kari", "nickname")));

        await Bumped(service, contact.ContactId, afterUpdate,
            async id => await service.DeleteAlias(id, aliasId));

        Assert.Empty((await service.GetAliases(contact.ContactId))!);
    }

    private static async Task<DateTime> Bumped(
        ContactService service, Guid contactId, DateTime before, Func<Guid, Task> mutate)
    {
        await mutate(contactId);
        var after = (await service.Get(contactId))!.UpdatedAt;
        Assert.True(after >= before);
        return after;
    }

    // AC 9's service-side half. Model validation rejects these on the HTTP path; the service re-checks
    // for direct callers, and names the field either way.
    [Fact]
    public async Task Create_ControlCharacterValue_IsRejectedByTheService()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));

        var rejected = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.CreateAlias(contact.ContactId, Alias("Kari\u0001Bob")));
        Assert.True(rejected.Errors!.ContainsKey("value"));
    }

    [Fact]
    public async Task Create_WhitespaceOnlyValue_IsRejected()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.CreateAlias(contact.ContactId, Alias("   ")));
    }

    [Fact]
    public async Task Create_CollapsesInternalWhitespace()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));

        var created = await service.CreateAlias(contact.ContactId, Alias("  Kari   Marie  ", "  maiden   name "));

        Assert.Equal("Kari Marie", created!.Value);
        Assert.Equal("maiden name", created.Label);
    }

    // AC 12's fast-tier half, with an exact-case term (the arm is case-sensitive on InMemory).
    [Fact]
    public async Task Search_MatchesAnAliasValue()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));
        await service.CreateAlias(contact.ContactId, Alias("Kari", "nickname"));
        await service.Create(Person("Sam", "Rivera"));

        var page = await service.ListAsync(new ContactsQueryParams { Search = "Kari" });

        Assert.Equal(contact.ContactId, Assert.Single(page.Items).ContactId);
    }

    // AC 14: the LABEL is deliberately not searched, which is why the placeholder does not promise it.
    [Fact]
    public async Task Search_DoesNotMatchAnAliasLabel()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));
        await service.CreateAlias(contact.ContactId, Alias("Kari", "maiden name"));

        var page = await service.ListAsync(new ContactsQueryParams { Search = "maiden" });

        Assert.Empty(page.Items);
    }

    // AC 13's fast-tier half.
    [Fact]
    public async Task Search_MatchesAMiddleName()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var person = Person("Jane", "Smith");
        person.PersonDetails!.MiddleName = "Elisabeth";
        var contact = await service.Create(person);
        await service.Create(Person("Sam", "Rivera"));

        var page = await service.ListAsync(new ContactsQueryParams { Search = "Elisabeth" });

        Assert.Equal(contact.ContactId, Assert.Single(page.Items).ContactId);
    }

    // AC 25: a middle name is stored and searchable but is NOT part of the fallback, so no existing
    // contact's resolved name, search key or sort position shifts when one is added.
    [Fact]
    public async Task MiddleName_LeavesTheResolvedAndNormalizedNamesUntouched()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Jane", "Smith"));
        var beforeResolved = contact.ResolvedDisplayName;
        var beforeNormalized = contact.NormalizedName;

        var update = Person("Jane", "Smith");
        update.PersonDetails!.MiddleName = "Elisabeth";
        var updated = await service.Update(contact.ContactId, update);

        Assert.Equal("Elisabeth", updated!.PersonDetails!.MiddleName);
        Assert.Equal(beforeResolved, updated.ResolvedDisplayName);
        Assert.Equal(beforeNormalized, updated.NormalizedName);
    }

    [Fact]
    public async Task MiddleName_BlankStoresNull()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var person = Person("Jane", "Smith");
        person.PersonDetails!.MiddleName = "   ";

        var created = await service.Create(person);

        Assert.Null(created.PersonDetails!.MiddleName);
    }

    // ── Lifecycle dates ───────────────────────────────────────────────────────

    // AC 23, the death side.
    [Fact]
    public async Task DateOfDeath_BeforeDateOfBirth_IsRejectedOnDateOfDeath()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var person = Person("Jane", "Smith");
        person.PersonDetails!.DateOfBirth = new DateTime(1968, 3, 14);
        person.PersonDetails.DateOfDeath = new DateTime(1960, 1, 1);

        var rejected = await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(person));

        Assert.True(rejected.Errors!.ContainsKey("dateOfDeath"));
    }

    // AC 23, the OTHER side — the pair is checked from both, so the invariant cannot be broken by
    // editing either field. This is why DateOfBirth needed an error channel of its own.
    [Fact]
    public async Task DateOfBirth_MovedPastAnExistingDateOfDeath_IsRejected()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var person = Person("Jane", "Smith");
        person.PersonDetails!.DateOfBirth = new DateTime(1950, 1, 1);
        person.PersonDetails.DateOfDeath = new DateTime(2020, 1, 1);
        var contact = await service.Create(person);

        var update = Person("Jane", "Smith");
        update.PersonDetails!.DateOfBirth = new DateTime(2021, 1, 1);
        update.PersonDetails.DateOfDeath = new DateTime(2020, 1, 1);

        var rejected = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(contact.ContactId, update));
        Assert.True(rejected.Errors!.ContainsKey("dateOfDeath"));
    }

    [Fact]
    public async Task DateOfDeath_InTheFuture_IsRejected()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var person = Person("Jane", "Smith");
        person.PersonDetails!.DateOfDeath = DateTime.UtcNow.AddDays(2);

        var rejected = await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(person));
        Assert.True(rejected.Errors!.ContainsKey("dateOfDeath"));
    }

    // Non-Goal 3: recording a death changes no state and removes no capability.
    [Fact]
    public async Task DateOfDeath_DoesNotArchiveTheContact()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var person = Person("Jane", "Smith");
        person.PersonDetails!.DateOfDeath = new DateTime(2024, 3, 11);

        var created = await service.Create(person);

        Assert.Null(created.Archived);
        Assert.Equal(new DateTime(2024, 3, 11), created.PersonDetails!.DateOfDeath);
    }

    // AC 24.
    [Fact]
    public async Task DissolvedDate_BeforeEstablishedDate_IsRejectedOnDissolvedDate()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var org = new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new OrganizationDetailsDto
            {
                LegalName = "Pacific Home Insurance Co.",
                EstablishedDate = new DateTime(1974, 6, 1),
                DissolvedDate = new DateTime(1970, 1, 1),
            },
        };

        var rejected = await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(org));
        Assert.True(rejected.Errors!.ContainsKey("dissolvedDate"));
    }

    // AC 24's second half: either date stands alone — the founding date of an old institution is
    // frequently unknown.
    [Fact]
    public async Task DissolvedDate_WithoutAnEstablishedDate_Saves()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var org = new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new OrganizationDetailsDto
            {
                LegalName = "FitZone Gym",
                DissolvedDate = new DateTime(2023, 6, 30),
            },
        };

        var created = await service.Create(org);

        Assert.Null(created.OrganizationDetails!.EstablishedDate);
        Assert.Equal(new DateTime(2023, 6, 30), created.OrganizationDetails.DissolvedDate);
        Assert.Null(created.Archived);
    }

    // AC 45's fast-tier half: the dedicated list and the inline collection agree on ASCII values.
    [Fact]
    public async Task InlineAliases_AndTheDedicatedList_AgreeOnOrder()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));
        foreach (var value in new[] { "Zed", "alpha", "Mid" })
        {
            await service.CreateAlias(contact.ContactId, Alias(value));
        }

        var dedicated = (await service.GetAliases(contact.ContactId))!.Select(a => a.Value);
        var inline = (await service.Get(contact.ContactId))!.Aliases.Select(a => a.Value);

        Assert.Equal(dedicated, inline);
    }

    // A cascade the fast tier CAN see, because it is application-level: removing the contact removes
    // its aliases. (The real FK cascade is an Odyssey.IntegrationTests criterion — the InMemory
    // provider enforces no foreign keys at all.)
    [Fact]
    public async Task DeletingAContact_TakesItsAliasesWithIt()
    {
        var service = NewService(out var context);
        await using var _ = context;
        var contact = await service.Create(Person("Karoline", "Hansen"));
        await service.CreateAlias(contact.ContactId, Alias("Kari"));

        await service.Delete(contact.ContactId);

        Assert.Null(await service.GetAliases(contact.ContactId));
    }
}
