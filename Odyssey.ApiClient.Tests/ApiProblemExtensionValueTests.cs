using System.Net;
using System.Text;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// <see cref="ApiProblem.TryGetExtensionValue{T}"/> (issue #452 §5.2): the value-type reader a <c>409</c> duplicate's
/// id goes through. A malformed member is an absent one — it returns <see langword="false"/>, never throws.
/// </summary>
public class ApiProblemExtensionValueTests
{
    private static async Task<ApiProblem> ParseAsync(string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/problem+json"),
        };
        return await response.ReadProblemAsync();
    }

    [Fact]
    public async Task Reads_a_guid()
    {
        var problem = await ParseAsync(
            """{"code":"instrument-duplicate","existingInstrumentId":"7c9e6679-7425-40de-944b-e07fc1f90ae7"}""");

        Assert.True(problem.TryGetExtensionValue<Guid>("existingInstrumentId", out var id));
        Assert.Equal(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"), id);
    }

    [Fact]
    public async Task Reads_an_int()
    {
        var problem = await ParseAsync("""{"code":"purge-count-mismatch","actualCount":251}""");

        Assert.True(problem.TryGetExtensionValue<int>("actualCount", out var count));
        Assert.Equal(251, count);
    }

    [Fact]
    public async Task A_missing_key_is_false()
    {
        var problem = await ParseAsync("""{"code":"purge-count-mismatch"}""");

        Assert.False(problem.TryGetExtensionValue<int>("actualCount", out var count));
        Assert.Equal(0, count);
        Assert.False(new ApiProblem().TryGetExtensionValue<Guid>("existingInstrumentId", out _));
    }

    [Theory]
    [InlineData("""{"existingInstrumentId":"not-a-guid"}""")]
    [InlineData("""{"existingInstrumentId":42}""")]
    [InlineData("""{"existingInstrumentId":{"id":"7c9e6679-7425-40de-944b-e07fc1f90ae7"}}""")]
    [InlineData("""{"existingInstrumentId":null}""")]
    public async Task A_wrong_typed_guid_is_false_and_never_throws(string json)
    {
        var problem = await ParseAsync(json);

        Assert.False(problem.TryGetExtensionValue<Guid>("existingInstrumentId", out var id));
        Assert.Equal(Guid.Empty, id);
    }

    [Theory]
    [InlineData("""{"actualCount":"many"}""")]
    [InlineData("""{"actualCount":2.5}""")]
    [InlineData("""{"actualCount":[1]}""")]
    [InlineData("""{"actualCount":true}""")]
    public async Task A_wrong_typed_int_is_false_and_never_throws(string json)
    {
        var problem = await ParseAsync(json);

        Assert.False(problem.TryGetExtensionValue<int>("actualCount", out var count));
        Assert.Equal(0, count);
    }
}
