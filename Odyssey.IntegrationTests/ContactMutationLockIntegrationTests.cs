using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Proves the MariaDB advisory lock behind <see cref="ContactMutationLock"/> actually serializes — the
/// mechanism that closes the insurer TOCTOU race (a policy write validating an insurer must not
/// interleave with that contact's delete). Runs against real MariaDB; the in-memory provider has no
/// advisory locks and the lock no-ops there, so this behaviour can only be verified here.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class ContactMutationLockIntegrationTests(MariaDbFixture fixture)
{
    [SkippableFact]
    public async Task Same_contact_id_is_mutually_exclusive_across_connections()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var contactId = Guid.NewGuid();
        // Two independent contexts → two DB sessions, so the advisory lock isn't re-entrant across them.
        await using var contextA = NewContext();
        await using var contextB = NewContext();
        var lockA = new ContactMutationLock(contextA);
        var lockB = new ContactMutationLock(contextB);

        var heldByA = await lockA.AcquireAsync(contactId);

        // B must not be able to take the same id while A holds it.
        var acquireB = lockB.AcquireAsync(contactId);
        var finishedFirst = await Task.WhenAny(acquireB, Task.Delay(TimeSpan.FromMilliseconds(750)));
        Assert.NotSame(acquireB, finishedFirst); // still blocked

        // Releasing A lets B proceed.
        await heldByA.DisposeAsync();
        var heldByB = await acquireB; // completes now
        await heldByB.DisposeAsync();
    }

    [SkippableFact]
    public async Task Different_contact_ids_do_not_block_each_other()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using var contextA = NewContext();
        await using var contextB = NewContext();
        var lockA = new ContactMutationLock(contextA);
        var lockB = new ContactMutationLock(contextB);

        var heldByA = await lockA.AcquireAsync(Guid.NewGuid());
        // A different id is independent — this returns promptly rather than blocking on A.
        var heldByB = await lockB.AcquireAsync(Guid.NewGuid());

        await heldByB.DisposeAsync();
        await heldByA.DisposeAsync();
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.RelationalConnectionString, ServerVersion.AutoDetect(fixture.RelationalConnectionString))
            .Options);
}
