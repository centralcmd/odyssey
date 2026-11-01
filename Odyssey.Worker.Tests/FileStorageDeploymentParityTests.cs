using Xunit;

namespace Odyssey.Worker.Tests;

/// <summary>
/// A3-4 (issue #346, extended by #362 Amendment v7.1 §8): the <c>api</c> and <c>worker</c> services are given the
/// same file volumes, mounted at the same paths, with the same <c>FileStorage__Volumes__*</c> and
/// <c>FileStorage__Bindings__*</c> settings — in both compose
/// files and in the Aspire AppHost. The worker erases what the API stores, so a volume configured on one and not
/// the other is a defect, and one the running stack would hide until the first erasure went missing.
/// </summary>
public sealed class FileStorageDeploymentParityTests
{
    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.prod.yml")]
    public void Api_and_worker_declare_identical_file_storage_settings_and_mounts(string composeFile)
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), composeFile));

        var api = FileStorageLines(ServiceBlock(lines, "api"));
        var worker = FileStorageLines(ServiceBlock(lines, "worker"));

        Assert.Equal(api, worker);
        if (composeFile == "docker-compose.yml")
        {
            Assert.Contains("FileStorage__Volumes__primary__RootPath: /var/odyssey/files/primary", api);
            Assert.Contains("- files_primary:/var/odyssey/files/primary", api);
        }
    }

    [Fact]
    public void Only_api_and_worker_mount_a_file_volume()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docker-compose.yml"));

        foreach (var service in new[] { "mariadb", "migrations", "client" })
        {
            Assert.Empty(FileStorageLines(ServiceBlock(lines, service)));
        }
    }

    [Fact]
    public void The_apphost_gives_api_and_worker_the_same_file_storage_settings()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Odyssey.AppHost", "AppHost.cs"));

        Assert.Equal(2, source.Split(".WithFileStorage(fileStorageSettings)").Length - 1);
        var api = Between(source, "AddProject<Projects.Odyssey_Api>", ".WaitForCompletion(migrations);");
        var worker = Between(source, "AddProject<Projects.Odyssey_Worker>", ".WaitFor(api);");
        Assert.Contains(".WithFileStorage(fileStorageSettings)", api, StringComparison.Ordinal);
        Assert.Contains(".WithFileStorage(fileStorageSettings)", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("\"FileStorage__Volumes", source.Replace("[\"FileStorage__Volumes__primary__RootPath\"]", string.Empty), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Odyssey.Api/Dockerfile")]
    [InlineData("Odyssey.Worker/Dockerfile")]
    public void Both_images_create_the_mount_point_owned_by_app(string dockerfile)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), dockerfile));

        Assert.Contains("mkdir -p /var/odyssey/files/primary && chown -R app:app /var/odyssey/files", source, StringComparison.Ordinal);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        return source[from..source.IndexOf(end, from, StringComparison.Ordinal)];
    }

    /// <summary>The lines of one service under <c>services:</c>, by indentation: two spaces for a service key.</summary>
    private static IReadOnlyList<string> ServiceBlock(string[] lines, string service)
    {
        var start = Array.FindIndex(lines, line => line == $"  {service}:");
        if (start < 0)
        {
            return [];
        }

        var block = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && !line.StartsWith("   ", StringComparison.Ordinal) && !line.TrimStart().StartsWith('#'))
            {
                break;
            }

            block.Add(line);
        }

        return block;
    }

    private static List<string> FileStorageLines(IEnumerable<string> block) =>
        [.. block.Select(line => line.Trim())
            .Where(line => !line.StartsWith('#'))
            .Where(line => line.StartsWith("FileStorage__Volumes__", StringComparison.Ordinal)
                || line.StartsWith("FileStorage__Bindings__", StringComparison.Ordinal)
                || line.Contains("/var/odyssey/files", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Odyssey.sln not found above the test output.");
    }
}
