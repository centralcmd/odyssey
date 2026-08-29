using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Odyssey.Core.Journal;

/// <summary>Provider-specific DB error helpers for the module's find-or-create hot paths.</summary>
internal static class DbErrors
{
    // MariaDB/MySQL error 1062 = ER_DUP_ENTRY: a unique-index violation. On the concurrent find-or-create
    // paths (keyword→tag, Photo-by-FileId) the loser of an insert race hits this; the service catches it
    // and re-fetches the winner instead of letting the DbUpdateException bubble to a 409 (§5, §16.3e).
    public static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is MySqlException { Number: 1062 };
}
