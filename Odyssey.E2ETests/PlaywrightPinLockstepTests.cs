using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// The run-odyssey driver's <c>playwright</c> npm pin must equal <c>Microsoft.Playwright</c>. A Claude Code
/// session exports <c>PLAYWRIGHT_BROWSERS_PATH</c>, so both resolve their browser from one directory that
/// holds only the build this suite wants; a driver pinned to another version looks for a build that is
/// never there and fails to launch. The rule was documented and still drifted (1.62.0 against 1.63.0), so
/// it is pinned here. Plain file reads: outside <see cref="StackCollection"/>, no stack needed.
/// </summary>
public sealed class PlaywrightPinLockstepTests
{
    [Fact]
    public void The_run_odyssey_driver_pins_the_same_playwright_as_the_dotnet_suites()
    {
        var root = RepoRoot();

        var dotnet = XDocument.Load(Path.Combine(root, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Single(e => (string?)e.Attribute("Include") == "Microsoft.Playwright")
            .Attribute("Version")!.Value;

        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, ".claude", "skills", "run-odyssey", "package.json")));
        var node = manifest.RootElement.GetProperty("dependencies").GetProperty("playwright").GetString();

        Assert.Equal(dotnet, node);

        // npm ci installs what the lockfile records, not what package.json asks for, so it has to agree too.
        using var lockfile = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, ".claude", "skills", "run-odyssey", "package-lock.json")));
        var packages = lockfile.RootElement.GetProperty("packages");
        Assert.Equal(dotnet, packages.GetProperty("node_modules/playwright").GetProperty("version").GetString());
        Assert.Equal(dotnet, packages.GetProperty("node_modules/playwright-core").GetProperty("version").GetString());
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Directory.Packages.props not found above " + AppContext.BaseDirectory);
    }
}
