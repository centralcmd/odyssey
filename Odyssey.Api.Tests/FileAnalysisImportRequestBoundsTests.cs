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
        // Past model validation the request reaches the service, which refuses it for a reason of its
        // own: the feature ships switched off (503), and with it on the job does not exist (404).
        var response = await PostAsync(Candidate(field, new string('x', max)));

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.NotFound });
    }

    /// <summary>
    /// Over HTTP the currency must arrive as a bare code; the service-level trim only serves non-HTTP
    /// callers. The client's currency picker emits bare codes, so this refuses nothing it sends.
    /// </summary>
    [Fact]
    public async Task A_padded_currency_is_a_400()
    {
        var response = await PostAsync(Candidate(nameof(ImportCandidateRequest.Currency), " usd "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static ImportCandidateRequest Candidate(string field, string value) => field switch
    {
        nameof(ImportCandidateRequest.Description) => new ImportCandidateRequest { CandidateId = Guid.NewGuid(), Description = value },
        nameof(ImportCandidateRequest.Currency) => new ImportCandidateRequest { CandidateId = Guid.NewGuid(), Currency = value },
        nameof(ImportCandidateRequest.ExternalId) => new ImportCandidateRequest { CandidateId = Guid.NewGuid(), ExternalId = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static async Task<HttpResponseMessage> PostAsync(ImportCandidateRequest candidate)
    {
        await using var factory = new OdysseyApiFactory(Claims);
        using var client = factory.CreateClient();
        return await client.PostAsJsonAsync($"/api/file-analysis/{Guid.NewGuid()}/import", new ImportRequest { Candidates = [candidate] });
    }
}
