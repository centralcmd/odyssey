using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Source-lints for issue #380's security review (S1): location authorization fails closed. A service that
/// takes <c>LocationClaims? claims = null</c> must treat a missing argument as <c>LocationClaims.None</c>, so
/// a future controller, job or import that forgets to pass the caller's claims loses capabilities instead of
/// silently gaining every one of them. <c>LocationClaims.Trusted</c> is an explicit opt-in for the seeder and
/// tests only, and never appears in a host or the business-logic project.
/// </summary>
public partial class LocationClaimsFailClosedLintTests
{
    private static readonly string[] ProductionProjects = ["Odyssey.Core", "Odyssey.Api", "Odyssey.Worker"];

    [Fact]
    public void No_service_falls_back_to_trusted_claims()
    {
        var fallbacks = ProductionFiles()
            .SelectMany(file => ClaimsFallback().Matches(file.Code).Select(m => (file.Path, Target: m.Groups["target"].Value)))
            .ToList();

        // The scan set must not be empty: the services this guards really do take nullable claims.
        Assert.True(fallbacks.Count >= 10, $"Expected the nullable-claims fallbacks to be found; found {fallbacks.Count}.");
        Assert.All(fallbacks, f => Assert.True(f.Target == "None", $"{f.Path} falls back to LocationClaims.{f.Target}."));
    }

    [Fact]
    public void Trusted_claims_are_never_named_by_production_code()
    {
        var offenders = ProductionFiles()
            .Where(file => !file.Path.EndsWith("Odyssey.Core/Locations/LocationClaims.cs", StringComparison.Ordinal))
            .Where(file => file.Code.Contains("LocationClaims.Trusted", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToList();

        Assert.Empty(offenders);
    }

    private static IEnumerable<(string Path, string Code)> ProductionFiles()
    {
        var root = SolutionRoot();
        return ProductionProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(path))
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), StripComments(File.ReadAllText(path))));
    }

    [GeneratedRegex(@"claims\s*\?\?=?\s*LocationClaims\.(?<target>\w+)")]
    private static partial Regex ClaimsFallback();

    private static string StripComments(string code) =>
        Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"//.*?$", string.Empty, RegexOptions.Multiline);

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the solution root from the test binary.");
    }
}
