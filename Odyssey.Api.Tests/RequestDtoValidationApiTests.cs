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
        foreach (var ordinal in new[] { 99, -1 })
        {
            await AssertRejectedAsync(route, field, updateClaim, ordinal);
        }
    }

    /// <summary>
    /// The positive control: a defined ordinal passes validation and reaches the action, which then
    /// finds no such record. Without it a 400 above could come from something other than the enum.
    /// </summary>
    [Theory]
    [MemberData(nameof(OutOfRangeFileTypes))]
    public async Task Attach_WithADefinedFileType_PassesValidation(string route, string field, string updateClaim)
    {
        await using var factory = new OdysseyApiFactory([updateClaim, PermissionClaims.FilesRead]);
        using var client = factory.CreateClient();

        var body = new Dictionary<string, object> { ["fileId"] = Guid.NewGuid(), [field] = 1 };
        var response = await client.PostAsJsonAsync(string.Format(route, Guid.NewGuid()), body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// <c>FileId</c> is required on all three attach requests (issue #287 M5). As a positional record an
    /// omitted id bound to <see cref="Guid.Empty"/> and surfaced as a confusing "file not found".
    /// </summary>
    [Theory]
    [MemberData(nameof(OutOfRangeFileTypes))]
    public async Task Attach_WithoutAFileId_IsRejectedByModelValidation(string route, string field, string updateClaim)
    {
        await using var factory = new OdysseyApiFactory([updateClaim, PermissionClaims.FilesRead]);
        using var client = factory.CreateClient();

        var body = new Dictionary<string, object> { [field] = 1 };
        var response = await client.PostAsJsonAsync(string.Format(route, Guid.NewGuid()), body);

        // The `required` member is what refuses the body — [Required] alone is inert on a non-nullable
        // Guid — and the problem names the missing property rather than some other failure.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("fileId", text, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertRejectedAsync(string route, string field, string updateClaim, int ordinal)
    {
        await using var factory = new OdysseyApiFactory([updateClaim, PermissionClaims.FilesRead]);
        using var client = factory.CreateClient();

        var body = new Dictionary<string, object> { ["fileId"] = Guid.NewGuid(), [field] = ordinal };
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

    /// <summary>
    /// The boundary itself: 256 characters fits the column, so validation passes and the service
    /// refuses the unknown role on its own terms — still a 400, but without a <c>role</c> model error.
    /// </summary>
    [Fact]
    public async Task AssignRole_WithARoleExactlyTheColumnWidth_PassesModelValidation()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.UsersUpdate]);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/users/{Guid.NewGuid()}/role",
            new { role = new string('r', 256) });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not exist", body, StringComparison.Ordinal);
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
