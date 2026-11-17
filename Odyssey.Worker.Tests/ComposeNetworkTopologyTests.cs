using Xunit;

namespace Odyssey.Worker.Tests;

/// <summary>
/// The compose network split (issue #470 §7.14, §11.3, AC 39): the worker runs untrusted parsers, so it is on exactly
/// <c>db_worker</c> and <c>egress</c> and shares no network with <c>api</c> or <c>client</c>; the three database
/// networks are internal; the worker gets a 30 s stop grace so a deploy is a graceful stop, never a crash. Read from the
/// compose files' text, like <see cref="FileStorageDeploymentParityTests"/>, so the test needs no Docker.
/// </summary>
public sealed class ComposeNetworkTopologyTests
{
    [Fact]
    public void The_worker_is_on_exactly_its_database_network_and_egress()
    {
        var compose = Lines("docker-compose.yml");

        Assert.Equal(["db_worker", "egress"], Networks(Service(compose, "worker")));
        Assert.Equal(["db_migrations"], Networks(Service(compose, "migrations")));
        Assert.Equal(["db_api", "default"], Networks(Service(compose, "api")));
        // The client declares none, so it is on `default` only.
        Assert.Empty(Networks(Service(compose, "client")));
    }

    [Fact]
    public void No_network_holds_both_the_api_and_the_worker()
    {
        var compose = Lines("docker-compose.yml");
        var members = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var service in new[] { "mariadb", "migrations", "api", "worker", "client" })
        {
            var networks = Networks(Service(compose, service));
            foreach (var network in networks.Count == 0 ? ["default"] : networks)
            {
                (members.TryGetValue(network, out var set) ? set : members[network] = []).Add(service);
            }
        }

        Assert.All(members, pair => Assert.False(pair.Value.Contains("api") && pair.Value.Contains("worker"), $"{pair.Key} holds api and worker"));
        Assert.All(members, pair => Assert.False(pair.Value.Contains("client") && pair.Value.Contains("worker"), $"{pair.Key} holds client and worker"));
    }

    [Fact]
    public void The_database_networks_are_internal_and_egress_is_not()
    {
        var compose = Lines("docker-compose.yml");
        var top = TopLevel(compose, "networks");

        foreach (var network in new[] { "db_api", "db_worker", "db_migrations" })
        {
            Assert.Contains("internal: true", Block(top, network, "  ").Select(l => l.Trim()));
        }

        Assert.DoesNotContain("internal: true", Block(top, "egress", "  ").Select(l => l.Trim()));
    }

    [Fact]
    public void The_production_overlay_keeps_the_database_off_the_default_network()
    {
        var prod = Lines("docker-compose.prod.yml");

        var mariadb = Service(prod, "mariadb");
        Assert.Contains("networks: !override", mariadb.Select(l => l.Trim()));
        Assert.Equal(["db_api", "db_migrations", "db_worker"], Networks(mariadb));
        Assert.Empty(Networks(Service(prod, "worker")));
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.prod.yml")]
    public void The_worker_has_a_thirty_second_stop_grace(string file)
    {
        Assert.Contains("stop_grace_period: 30s", Service(Lines(file), "worker").Select(l => l.Trim()));
    }

    private static string[] Lines(string file) => File.ReadAllLines(Path.Combine(RepositoryRoot(), file));

    private static List<string> Service(string[] lines, string service) => Block(lines.ToList(), service, "  ");

    /// <summary>The lines under a top-level key (<c>networks:</c>, <c>volumes:</c>).</summary>
    private static List<string> TopLevel(string[] lines, string key)
    {
        var start = Array.FindIndex(lines, line => line == $"{key}:");
        Assert.True(start >= 0, $"no top-level {key}");
        return lines.Skip(start + 1).TakeWhile(line => line.Length == 0 || line.StartsWith(' ') || line.StartsWith('#')).ToList();
    }

    /// <summary>The lines of a key at <paramref name="indent"/>, by indentation.</summary>
    private static List<string> Block(List<string> lines, string key, string indent)
    {
        var start = lines.FindIndex(line => line == $"{indent}{key}:" || line.StartsWith($"{indent}{key}: ", StringComparison.Ordinal));
        Assert.True(start >= 0, $"no {key}");
        var block = new List<string> { lines[start] };
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && !line.StartsWith(indent + " ", StringComparison.Ordinal) && !line.TrimStart().StartsWith('#'))
            {
                break;
            }

            block.Add(line);
        }

        return block;
    }

    /// <summary>The list items under a service's <c>networks:</c> key, sorted; empty when it declares none.</summary>
    private static List<string> Networks(List<string> service)
    {
        var start = service.FindIndex(line => line.StartsWith("    networks:", StringComparison.Ordinal));
        if (start < 0)
        {
            return [];
        }

        return service.Skip(start + 1)
            .TakeWhile(line => line.StartsWith("      - ", StringComparison.Ordinal) || line.TrimStart().StartsWith('#'))
            .Where(line => !line.TrimStart().StartsWith('#'))
            .Select(line => line.Trim()[2..].Trim())
            .Order(StringComparer.Ordinal)
            .ToList();
    }

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
