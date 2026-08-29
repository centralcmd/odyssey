using Odyssey.Core;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// <see cref="IContactMutationLock"/> over a MariaDB session advisory lock (<c>GET_LOCK</c>/
/// <c>RELEASE_LOCK</c>). The lock is acquired on a pinned <see cref="OdysseyContext"/> connection and
/// released (and the connection closed) on dispose. Because the lock name is server-scoped, a hold taken
/// on one connection serializes against a hold taken on any other connection to the same server —
/// including the delete path's — which is what closes the check-and-write race on a contact.
/// </summary>
/// <remarks>
/// The race it closes used to span two DbContexts and now spans one, which makes the lock's job easier
/// rather than unnecessary: the insurer check and the delete are still separate statements, so a
/// concurrent policy write can still land between them. Being server-scoped rather than
/// connection-scoped is what makes it work across the request handling both.
/// </remarks>
public sealed class ContactMutationLock(OdysseyContext context) : IContactMutationLock
{
    // Long enough to outlast a normal check-and-write, short enough that a genuine deadlock/stall surfaces
    // as a 409 rather than hanging the request.
    private const int TimeoutSeconds = 10;

    /// <summary>Shared no-op handle for the non-relational (in-memory) path and service defaults.</summary>
    public static readonly IContactMutationLock None = new NoopLock();

    public async Task<IAsyncDisposable> AcquireAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        // The in-memory test provider has neither advisory locks nor real write concurrency.
        if (!context.Database.IsRelational())
        {
            return NoopHandle.Instance;
        }

        var key = LockKey(contactId);
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // GET_LOCK returns 1 on success, 0 on timeout, NULL on error.
            var acquired = await ScalarAsync("SELECT GET_LOCK(@k, @t)", key, TimeoutSeconds, cancellationToken);
            if (acquired != 1)
            {
                throw new DomainConflictException(
                    "The contact is being modified by another operation; please retry.");
            }
        }
        catch
        {
            await context.Database.CloseConnectionAsync();
            throw;
        }

        return new ReleaseHandle(context, key);
    }

    private async Task<int?> ScalarAsync(string sql, string key, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var keyParam = command.CreateParameter();
        keyParam.ParameterName = "@k";
        keyParam.Value = key;
        command.Parameters.Add(keyParam);

        if (timeoutSeconds is not null)
        {
            var timeoutParam = command.CreateParameter();
            timeoutParam.ParameterName = "@t";
            timeoutParam.Value = timeoutSeconds.Value;
            command.Parameters.Add(timeoutParam);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    // MariaDB lock names are capped at 64 chars; "odyssey:contact:" + 32 hex = 48.
    private static string LockKey(Guid contactId) => $"odyssey:contact:{contactId:N}";

    private sealed class ReleaseHandle(OdysseyContext context, string key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                var connection = context.Database.GetDbConnection();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT RELEASE_LOCK(@k)";
                var keyParam = command.CreateParameter();
                keyParam.ParameterName = "@k";
                keyParam.Value = key;
                command.Parameters.Add(keyParam);
                await command.ExecuteScalarAsync();
            }
            finally
            {
                // Closing the connection ends the session, which also releases any held advisory lock.
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    private sealed class NoopHandle : IAsyncDisposable
    {
        public static readonly NoopHandle Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopLock : IContactMutationLock
    {
        public Task<IAsyncDisposable> AcquireAsync(Guid contactId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(NoopHandle.Instance);
    }
}
