using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MySqlConnector;

namespace Odyssey.Core.Internet.News.Reading;

/// <summary>
/// The news reader's statement time limit (issue #470 §8.4, H3). Every query the news API runs is tagged
/// <see cref="Tag"/>; on MariaDB, <see cref="NewsQueryCommandInterceptor"/> runs such a command under
/// <c>SET STATEMENT max_statement_time=5 FOR</c> with a 6 s client-side backstop, and <see cref="RunAsync{T}"/> turns
/// the resulting timeout — or a lock wait timeout — into <c>503 news-busy</c>. Only news endpoints call it, so a lock
/// wait elsewhere keeps its ordinary mapping.
/// </summary>
public static class NewsQueryGuard
{
    /// <summary>The <c>TagWith</c> text every news query carries.</summary>
    public const string Tag = "news-query";

    /// <summary>MariaDB <c>ER_STATEMENT_TIMEOUT</c>: <c>max_statement_time</c> exceeded.</summary>
    private const int StatementTimeout = 1969;

    /// <summary>Runs <paramref name="work"/>, mapping a statement or lock-wait timeout to <c>503 news-busy</c>.</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            return await work();
        }
        catch (Exception ex) when (IsBusy(ex))
        {
            throw NewsErrors.Busy();
        }
    }

    public static async Task RunAsync(Func<Task> work) =>
        await RunAsync(async () =>
        {
            await work();
            return true;
        });

    /// <summary>
    /// True when <paramref name="exception"/> (or anything it wraps) is a statement timeout or a lock wait, or an
    /// interrupted news transaction (<see cref="NewsTransactionRetryException"/>, issue #508 A2): its commit outcome is
    /// unknown, so a retry that re-evaluates is the answer, exactly as for a timeout.
    /// </summary>
    public static bool IsBusy(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NewsTransactionRetryException)
            {
                return true;
            }

            if (current is MySqlException mySql && (mySql.Number == StatementTimeout
                    || mySql.ErrorCode is MySqlErrorCode.LockWaitTimeout or MySqlErrorCode.QueryTimeout or MySqlErrorCode.CommandTimeoutExpired))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Applies the statement limit to every command carrying the <see cref="NewsQueryGuard.Tag"/> comment (§8.4): the
/// server-side <c>max_statement_time</c> and a client-side command timeout one second longer. Untagged commands are
/// untouched. Registered on the API's relational context only; EF InMemory runs no commands.
/// </summary>
public sealed class NewsQueryCommandInterceptor(int statementSeconds = NewsQueryCommandInterceptor.DefaultStatementSeconds) : DbCommandInterceptor
{
    public const int DefaultStatementSeconds = 5;

    private static readonly string Marker = $"-- {NewsQueryGuard.Tag}";

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Apply(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Apply(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Apply(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Apply(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Apply(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(command);
        return ValueTask.FromResult(result);
    }

    private void Apply(DbCommand command)
    {
        var text = command.CommandText;
        if (!text.StartsWith(Marker, StringComparison.Ordinal))
        {
            return;
        }

        // The tag is a line comment, so the statement itself starts on the next line and the prefix applies to it.
        command.CommandText = $"SET STATEMENT max_statement_time={statementSeconds} FOR {text}";
        command.CommandTimeout = statementSeconds + 1;
    }
}

/// <summary>The tag every news query carries; <see cref="NewsQueryCommandInterceptor"/> keys on it.</summary>
public static class NewsQueryTagExtensions
{
    public static IQueryable<T> News<T>(this IQueryable<T> query) => query.TagWith(NewsQueryGuard.Tag);
}
