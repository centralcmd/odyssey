using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Controllers learn who is acting one way and check a permission one way (issue #287 M4). The source
/// lints keep the five former patterns — a <c>"unknown"</c> default, a thrown
/// <see cref="InvalidOperationException"/>, a bare <c>Unauthorized()</c>, a hand-rolled problem and a
/// null passed to the service — from coming back one call site at a time.
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
    public void NothingServerSide_ChecksAPermissionWithARawHasClaim()
    {
        var offenders = new[] { "Odyssey.Api", "Odyssey.Core" }
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot.Path, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("HasClaim(PermissionClaims.Type", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "Use HasPermission(claim): " + string.Join(", ", offenders));
    }

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
