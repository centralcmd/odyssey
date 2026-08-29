using Microsoft.Extensions.Configuration;

namespace Odyssey.Core.Configuration;

public static class ConnectionStringConfiguration
{
    /// <summary>
    /// Reads a connection string, treating a declared-but-blank value as missing.
    /// </summary>
    /// <remarks>
    /// Every host's appsettings.json ships the keys as empty strings to document the expected shape,
    /// so <c>GetConnectionString</c> returns <c>""</c> rather than <c>null</c> and a plain
    /// <c>?? throw</c> never fires. The blank then reaches <c>UseMySql</c>, which throws an
    /// <see cref="ArgumentException"/> naming neither the key nor the fix (issue #422).
    /// </remarks>
    public static string GetRequiredConnectionString(this IConfiguration configuration, string name)
    {
        var connectionString = configuration.GetConnectionString(name);

        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException(
                $"Connection string '{name}' is not configured. Set ConnectionStrings:{name} "
                + $"(or the ConnectionStrings__{name} environment variable), or set UseInMemoryDatabase=true.")
            : connectionString;
    }
}
