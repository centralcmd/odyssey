using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Odyssey.Core.Journal;

/// <summary>Provider-specific DB error helpers for the module's find-or-create hot paths.</summary>
internal static class DbErrors
{
    // MariaDB/MySQL error 1062 = ER_DUP_ENTRY: a unique-index violation. On the concurrent find-or-create
    // paths (keyword→tag) the loser of an insert race hits this; the service catches it
    // and re-fetches the winner instead of letting the DbUpdateException bubble to a 409 (§5, §16.3e).
    public static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is MySqlException { Number: 1062 };

    /// <summary>
    /// A 1062 raised by the unique index <paramref name="indexName"/> specifically. MariaDB names the index only
    /// in the message text (<c>Duplicate entry '…' for key 'IX_…'</c>), so this is the one place that reads it:
    /// a driver or server wording change has one place to fix.
    /// </summary>
    public static bool IsDuplicateKeyOn(DbUpdateException exception, string indexName) =>
        exception.InnerException is MySqlException { Number: 1062 } mysql
        && mysql.Message.Contains($"{indexName}'", StringComparison.Ordinal);

    /// <summary>
    /// A 1451 (ER_ROW_IS_REFERENCED_2): a delete refused by a <c>RESTRICT</c> key — the race a service's in-use
    /// check lost.
    /// </summary>
    public static bool IsRowReferenced(DbUpdateException exception) =>
        exception.InnerException is MySqlException { Number: 1451 or 1217 };

    /// <summary>
    /// A 1452 (ER_NO_REFERENCED_ROW_2) naming <paramref name="table"/>: an insert whose parent row was deleted after
    /// the service validated it. MariaDB names the child table only in the message text.
    /// </summary>
    public static bool IsMissingReferenceFrom(DbUpdateException exception, string table) =>
        exception.InnerException is MySqlException { Number: 1452 or 1216 } mysql
        && mysql.Message.Contains($"`{table}`", StringComparison.Ordinal);
}
