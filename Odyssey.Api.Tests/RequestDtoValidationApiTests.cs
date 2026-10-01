using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The request DTOs that used to rely on a handler (or on Mapster's fallback to <c>Other</c>) to catch a
/// bad value now carry the data annotations the DTO convention requires, so model validation refuses
/// the body with a <c>400</c> before the action runs (issue #287 M5). Every route here names a record id
/// that does not exist: a <c>400</c> therefore proves validation ran first, where a <c>404</c> would
/// mean the body got through.
/// </summary>
public sealed class RequestDtoValidationApiTests
{
    public static TheoryData<string, string, string> OutOfRangeFileTypes() => new()
    {
        { "/api/accounts/{0}/files", "fileType", PermissionClaims.AccountsUpdate },
        { "/api/tax-statements/{0}/files", "fileType", PermissionClaims.TaxesUpdate },
        { "/api/transactions/{0}/files", "type", PermissionClaims.TransactionsUpdate },
    };

    [Theory]
    [MemberData(nameof(OutOfRangeFileTypes))]
    public async Task Attach_WithAnUndefinedFileType_IsRejectedByModelValidation(
        string route, string field, string updateClaim)
    {
        await using var factory = new OdysseyApiFactory([updateClaim, PermissionClaims.FilesRead]);
        using var client = factory.CreateClient();

        var body = new Dictionary<string, object> { ["fileId"] = Guid.NewGuid(), [field] = 99 };
        var response = await client.PostAsJsonAsync(string.Format(route, Guid.NewGuid()), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();
        Assert.Contains(problem!.Errors.Keys, key => string.Equals(key, field, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"role\":null}")]
    public async Task AssignRole_WithoutARole_IsRejectedByModelValidation(string json)
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.UsersUpdate]);
        using var client = factory.CreateClient();

        var response = await client.PutAsync($"/api/users/{Guid.NewGuid()}/role",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AssignRole_WithARoleLongerThanTheColumn_IsRejectedByModelValidation()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.UsersUpdate]);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/users/{Guid.NewGuid()}/role",
            new { role = new string('r', 257) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();
        Assert.Contains(problem!.Errors.Keys, key => string.Equals(key, "role", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ValidationProblem(Dictionary<string, string[]> Errors);
}
