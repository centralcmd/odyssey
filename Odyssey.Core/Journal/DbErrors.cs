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

    /// <summary>1451: a delete or update refused by a RESTRICT foreign key that still references the row.</summary>
    public static bool IsReferencedRow(DbUpdateException exception) =>
        exception.GetBaseException() is MySqlException { Number: 1451 };

    /// <summary>1452: an insert or update naming a parent row that does not exist (it was deleted meanwhile).</summary>
    public static bool IsMissingReference(DbUpdateException exception) =>
        exception.GetBaseException() is MySqlException { Number: 1452 };

    /// <summary>Whether a <c>1452</c>'s message names the referenced table <paramref name="table"/> (backtick-quoted).</summary>
    public static bool MissingReferenceNames(DbUpdateException exception, string table) =>
        exception.GetBaseException() is MySqlException { Number: 1452 } mysql
        && mysql.Message.Contains($"REFERENCES `{table}`", StringComparison.Ordinal);

    /// <summary>
    /// A 1062 raised by the unique index <paramref name="indexName"/> specifically. MariaDB names the index only
    /// in the message text (<c>Duplicate entry '…' for key 'IX_…'</c>), so this is the one place that reads it:
    /// a driver or server wording change has one place to fix.
    /// </summary>
    public static bool IsDuplicateKeyOn(DbUpdateException exception, string indexName) =>
        exception.InnerException is MySqlException { Number: 1062 } mysql
        && mysql.Message.Contains($"{indexName}'", StringComparison.Ordinal);
}
