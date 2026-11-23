using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Odyssey.MigrationService.Fingerprint;

/// <summary>
/// The schema fingerprint (issue #581 §5): a deterministic, normalised text description of a database, compared
/// against the committed <c>&lt;baseline-id&gt;.fingerprint.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// This type writes the three OPERATOR sections — <c>[schema]</c>, <c>[seed-rows]</c>, <c>[seed-keys]</c> — and
/// nothing else, because it is what the migrations job runs against production. The committed file adds a
/// <c>[parity-rows]</c> section that only the integration tier writes, over a database it created itself; the
/// operator output is by construction the committed file with that section removed.
/// </para>
/// <para>
/// Every row this type reads goes through <see cref="ReadSeededAsync"/>, which takes a classified
/// <see cref="SeededTable"/> and refuses <see cref="SeededTableClass.Excluded"/>. A source-lint holds that to one
/// method, so no later change can make the job print a user, a credential or a setting value.
/// </para>
/// </remarks>
internal static partial class SchemaFingerprint
{
    public const string SchemaSection = "[schema]";
    public const string SeedRowsSection = "[seed-rows]";
    public const string SeedKeysSection = "[seed-keys]";
    public const string ParityRowsSection = "[parity-rows]";

    /// <summary>Writes the header and the three operator sections for the connection's current database.</summary>
    public static async Task WriteOperatorSectionsAsync(DbConnection connection, TextWriter output, CancellationToken ct = default)
    {
        var version = await SchemaCapture.ReadServerVersionAsync(connection, ct);
        var (charset, collation) = await SchemaCapture.ReadDefaultsAsync(connection, ct);
        var tables = await SchemaCapture.ReadBaseTablesAsync(connection, ct);
        var columns = await SchemaCapture.ReadColumnsAsync(connection, ct);
        var keys = await SchemaCapture.ReadPrimaryKeysAsync(connection, ct);

        output.Write("# odyssey schema fingerprint v1 — header lines start with '#' and are never compared\n");
        output.Write($"# server: {version}; default charset: {charset}; default collation: {collation}\n");

        output.Write($"{SchemaSection}\n");
        var unsupported = await SchemaCapture.ReadUnsupportedObjectsAsync(connection, ct);
        output.Write(unsupported.Count == 0
            ? "other objects: none\n"
            : string.Concat(unsupported.Order(StringComparer.Ordinal).Select(o => $"other object: {o}\n")));
        foreach (var table in tables.Where(t => t != HistoryTable))
        {
            output.Write($"table {SchemaCapture.Quote(table)}\n");
            output.Write(NormalizeCreateTable(await SchemaCapture.ShowCreateTableAsync(connection, table, ct), charset, collation));
            output.Write("\n");
        }

        output.Write($"{SeedRowsSection}\n");
        foreach (var seeded in SeededTables.All.Where(s => s.Class == SeededTableClass.Rows).OrderBy(s => s.Table, StringComparer.Ordinal))
        {
            await ReadSeededAsync(connection, seeded, columns, keys, output, ct);
        }

        output.Write($"{SeedKeysSection}\n");
        foreach (var seeded in SeededTables.All.Where(s => s.Class == SeededTableClass.Keys).OrderBy(s => s.Table, StringComparer.Ordinal))
        {
            await ReadSeededAsync(connection, seeded, columns, keys, output, ct);
        }
    }

    /// <summary>The migration history table, which the fingerprint never describes.</summary>
    public const string HistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// <c>SHOW CREATE TABLE</c> output with only what varies between equal schemas removed: the
    /// <c>AUTO_INCREMENT=n</c> table option, <c>\r\n</c>, and the ORDER of the column and key lines, which are sorted
    /// (columns first, then keys and constraints). Each line itself is kept verbatim — engine, charset, collation,
    /// <c>ROW_FORMAT</c>, defaults and every literal survive unchanged.
    /// </summary>
    /// <remarks>
    /// Order is discounted because a rebaseline cannot reproduce it: a column a later migration added was appended
    /// to its table, while the baseline creates every column in model order (the November 2026 rebaseline moved 40
    /// tables' columns that way, and nothing else about them). EF addresses columns and indexes by name, so the
    /// order is not part of what the application depends on.
    /// </remarks>
    /// <param name="databaseCharset">The database's default charset; with <paramref name="databaseCollation"/>, a table
    /// option equal to both is written as <see cref="InheritedDefaults"/>.</param>
    /// <param name="databaseCollation">The database's default collation.</param>
    public static string NormalizeCreateTable(string createTable, string? databaseCharset = null, string? databaseCollation = null)
    {
        var text = AutoIncrementOption().Replace(createTable.Replace("\r\n", "\n", StringComparison.Ordinal), "");
        if (databaseCharset is not null && databaseCollation is not null)
        {
            // A table that inherited the database's defaults says so, rather than naming them: the defaults are the
            // server's configuration (MariaDB 11.4 ships utf8mb4_uca1400_ai_ci, a database created with an explicit
            // collation differs), not something a migration chose. A table whose options differ keeps them.
            text = text.Replace(
                $" DEFAULT CHARSET={databaseCharset} COLLATE={databaseCollation}\n", $" {InheritedDefaults}\n", StringComparison.Ordinal);
            if (text.EndsWith($" DEFAULT CHARSET={databaseCharset} COLLATE={databaseCollation}", StringComparison.Ordinal))
            {
                text = text[..^$" DEFAULT CHARSET={databaseCharset} COLLATE={databaseCollation}".Length] + $" {InheritedDefaults}";
            }
        }

        var lines = text.Split('\n');
        if (lines.Length < 3)
        {
            return string.Join('\n', lines) + "\n";
        }

        var body = lines[1..^1].Select(line => line.TrimEnd(',')).ToList();
        var columns = body.Where(line => line.StartsWith("  `", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
        var keys = body.Where(line => !line.StartsWith("  `", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
        return lines[0] + "\n" + string.Join(",\n", columns.Concat(keys)) + "\n" + lines[^1] + "\n";
    }

    /// <summary>Written in place of a table's charset and collation options when they equal the database's defaults.</summary>
    public const string InheritedDefaults = "DEFAULT CHARSET=<database default> COLLATE=<database default>";

    /// <summary>The comparable part of a fingerprint: every line that is not a header line.</summary>
    public static string WithoutHeader(string fingerprint) =>
        string.Concat(fingerprint.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => !line.StartsWith('#'))
            .Select(line => line + "\n")).TrimEnd('\n') + "\n";

    /// <summary>A fingerprint up to, not including, its <c>[parity-rows]</c> section.</summary>
    public static string OperatorSectionsOf(string fingerprint)
    {
        var text = fingerprint.Replace("\r\n", "\n", StringComparison.Ordinal);
        var at = text.IndexOf("\n" + ParityRowsSection + "\n", StringComparison.Ordinal);
        return at < 0 ? text : text[..(at + 1)];
    }

    /// <summary>
    /// One value as fingerprint text: <c>\N</c> for NULL, and tab, newline, carriage return and backslash escaped,
    /// so a row is always exactly one line of tab-separated fields.
    /// </summary>
    public static string FormatValue(object? value) => value switch
    {
        null or DBNull => @"\N",
        string text => Escape(text),
        bool flag => flag ? "1" : "0",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime instant => instant.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset instant => instant.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        Guid id => id.ToString("D"),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
    };

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '\\' => @"\\",
                '\t' => @"\t",
                '\n' => @"\n",
                '\r' => @"\r",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// THE one row read of this type: the columns a seeded table's class allows, in primary-key order.
    /// <see cref="SeededTableClass.Rows"/> reads every column, <see cref="SeededTableClass.Keys"/> the key columns
    /// only, and <see cref="SeededTableClass.Excluded"/> is refused.
    /// </summary>
    private static async Task ReadSeededAsync(
        DbConnection connection, SeededTable seeded,
        IReadOnlyDictionary<string, List<(string Column, bool Generated)>> columns,
        IReadOnlyDictionary<string, List<string>> keys,
        TextWriter output, CancellationToken ct)
    {
        if (seeded.Class == SeededTableClass.Excluded)
        {
            throw new InvalidOperationException($"'{seeded.Table}' is excluded from the fingerprint.");
        }

        output.Write($"table {SchemaCapture.Quote(seeded.Table)}\n");
        if (!columns.TryGetValue(seeded.Table, out var tableColumns) || !keys.TryGetValue(seeded.Table, out var keyColumns))
        {
            output.Write("absent\n");
            return;
        }

        var selected = seeded.Class == SeededTableClass.Rows
            ? tableColumns.Select(c => c.Column).Order(StringComparer.Ordinal).ToList()
            : keyColumns;
        output.Write(string.Join('\t', selected) + "\n");

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", selected.Select(SchemaCapture.Quote))} FROM {SchemaCapture.Quote(seeded.Table)} " +
            $"ORDER BY {string.Join(", ", keyColumns.Select(SchemaCapture.Quote))}";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var fields = new string[reader.FieldCount];
            for (var i = 0; i < fields.Length; i++)
            {
                fields[i] = FormatValue(reader.GetValue(i));
            }

            output.Write(string.Join('\t', fields) + "\n");
        }
    }

    [GeneratedRegex(@" AUTO_INCREMENT=\d+", RegexOptions.CultureInvariant)]
    private static partial Regex AutoIncrementOption();
}
