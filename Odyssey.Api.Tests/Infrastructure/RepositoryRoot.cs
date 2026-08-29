namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Locates the repository root so a test can read a checked-in infrastructure file — a compose file,
/// a Dockerfile, the deployment guide. Mirrors <c>Odyssey.Client.Tests</c>'s <c>ClientSource</c>, and
/// exists for the same reason: some contracts live in files no assembly compiles, and the only way to
/// keep them in step with the code is to read them.
/// </summary>
internal static class RepositoryRoot
{
    private static readonly Lazy<string> RootPath = new(Locate);

    public static string Path => RootPath.Value;

    public static string ReadAllText(string relativePath) =>
        File.ReadAllText(System.IO.Path.Combine(Path, relativePath));

    private static string Locate()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory, "Odyssey.sln")))
            {
                return directory;
            }

            directory = System.IO.Path.GetDirectoryName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }

        throw new InvalidOperationException(
            "Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
