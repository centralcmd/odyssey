using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The string bounds on <see cref="ImportCandidateRequest"/> (issue #237). They sit on a positional
/// record's constructor parameters, so these prove MVC honours them, and that each is the CANDIDATE
/// column's width — the review grid sends the extracted values back verbatim, so a value at that width
/// must still pass model validation.
/// </summary>
public class FileAnalysisImportRequestBoundsTests
{
    private static readonly string[] Claims = [PermissionClaims.FileAnalysisRead, PermissionClaims.FileAnalysisImport];

    public static TheoryData<string, int> Bounds => new()
    {
        { nameof(ImportCandidateRequest.Description), 1024 },
        { nameof(ImportCandidateRequest.Currency), 3 },
        { nameof(ImportCandidateRequest.ExternalId), 256 },
    };

    [Theory]
    [MemberData(nameof(Bounds))]
    public async Task A_value_past_the_bound_is_a_400_on_that_field(string field, int max)
    {
        var response = await PostAsync(Candidate(field, new string('x', max + 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(problem!.Errors.Keys, key => key.EndsWith(field, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Bounds))]
    public async Task A_value_at_the_bound_passes_model_validation(string field, int max)
    {
        // Whatever the service then answers (feature off, unknown job), it is not model validation's 400.
        var response = await PostAsync(Candidate(field, new string('x', max)));

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static ImportCandidateRequest Candidate(string field, string value) => field switch
    {
        nameof(ImportCandidateRequest.Description) => new(Guid.NewGuid(), null, value, null, null),
        nameof(ImportCandidateRequest.Currency) => new(Guid.NewGuid(), null, null, null, value),
        nameof(ImportCandidateRequest.ExternalId) => new(Guid.NewGuid(), null, null, null, null, ExternalId: value),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static async Task<HttpResponseMessage> PostAsync(ImportCandidateRequest candidate)
    {
        await using var factory = new OdysseyApiFactory(Claims);
        using var client = factory.CreateClient();
        return await client.PostAsJsonAsync($"/api/file-analysis/{Guid.NewGuid()}/import", new ImportRequest([candidate]));
    }
}
