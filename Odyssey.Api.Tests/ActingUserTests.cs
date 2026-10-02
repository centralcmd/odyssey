using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Controllers learn who is acting one way and check a permission one way (issue #287 M4). The source
/// lints keep the replaced patterns out: reading <c>ClaimTypes.NameIdentifier</c> directly (behind
/// which the <c>null</c> pass-through and the thrown <see cref="InvalidOperationException"/> lived),
/// an <c>"unknown"</c> actor, a bare <c>Unauthorized()</c>, and a raw permission <c>HasClaim</c>.
/// </summary>
public class ActingUserTests
{
    [Fact]
    public void ActingUserId_ReturnsTheNameIdentifier()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1")], "test"));

        Assert.Equal("user-1", user.ActingUserId());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ActingUserId_IsNullForAMissingOrBlankId(string? id)
    {
        var claims = id is null ? Array.Empty<Claim>() : [new Claim(ClaimTypes.NameIdentifier, id)];
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        Assert.Null(user.ActingUserId());
    }

    [Fact]
    public void MissingUserProblem_IsA401ProblemDetails()
    {
        var result = new ProbeController().MissingUserProblem();

        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ProblemDetails>(result.Value).Status);
    }

    [Theory]
    [InlineData("User.HasClaim(PermissionClaims.Type, x)")]
    [InlineData("User.HasClaim(\n    PermissionClaims.Type, x)")]
    [InlineData("caller.HasClaim(type: PermissionClaims.Type, value: x)")]
    public void TheRawCheckLint_MatchesItsVariants(string source) =>
        Assert.Matches(RawPermissionCheck, source);

    [Fact]
    public void HasPermission_TestsThePermissionClaimType()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(PermissionClaims.Type, PermissionClaims.FilesRead), new Claim("other", PermissionClaims.FilesUpdate)],
            "test"));

        Assert.True(user.HasPermission(PermissionClaims.FilesRead));
        Assert.False(user.HasPermission(PermissionClaims.FilesUpdate));
    }

    [Fact]
    public void NoController_ReadsTheUserIdClaimDirectly()
    {
        var offenders = ControllerSources()
            .Where(source => source.Text.Contains("ClaimTypes.NameIdentifier", StringComparison.Ordinal))
            .Select(source => source.Name)
            .ToList();

        Assert.True(offenders.Count == 0, "Use User.ActingUserId(): " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoController_DefaultsTheActorToUnknown_OrReturnsABareUnauthorized()
    {
        var offenders = ControllerSources()
            .Where(source => source.Text.Contains("\"unknown\"", StringComparison.Ordinal)
                || source.Text.Contains("return Unauthorized();", StringComparison.Ordinal))
            .Select(source => source.Name)
            .ToList();

        Assert.True(offenders.Count == 0, "Use MissingUserProblem(): " + string.Join(", ", offenders));
    }

    [Fact]
    public void NothingServerSide_ChecksAPermissionWithARawHasClaim()
    {
        var offenders = new[] { "Odyssey.Api", "Odyssey.Core" }
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot.Path, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => RawPermissionCheck.IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "Use HasPermission(claim): " + string.Join(", ", offenders));
    }

    // Any spacing, line break or named argument between HasClaim( and the claim type.
    private static readonly System.Text.RegularExpressions.Regex RawPermissionCheck =
        new(@"HasClaim\(\s*(type:\s*)?PermissionClaims\.Type\b");

    private static IEnumerable<(string Name, string Text)> ControllerSources()
    {
        var sources = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot.Path, "Odyssey.Api", "Controllers"), "*.cs")
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)))
            .ToList();
        Assert.NotEmpty(sources);
        return sources;
    }

    private sealed class ProbeController : ControllerBase;
}
