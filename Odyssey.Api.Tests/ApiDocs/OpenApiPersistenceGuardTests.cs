using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Odyssey.Api.ApiDocs;
using Xunit;

namespace Odyssey.Api.Tests.ApiDocs;

/// <summary>
/// A persistence entity type must never reach the OpenAPI document (issue #392). The guard throws from the schema-id
/// callback; it is tested here, below HTTP, because over HTTP the exception now passes through
/// <c>GlobalExceptionHandler</c>, whose 500 body need not carry the message (issue #569 §9.2).
/// </summary>
public class OpenApiPersistenceGuardTests
{
    private static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    [Theory]
    [InlineData(typeof(Odyssey.Context.Account))]
    [InlineData(typeof(Odyssey.Context.AccountType))]
    [InlineData(typeof(Odyssey.Context.AccountType?))]
    public void AnEntityType_FailsTheDocument_NamingIssue392(Type entity)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            OpenApiDocumentRegistration.SchemaReferenceId(Options.GetTypeInfo(entity)));

        Assert.Contains("persistence entity type", failure.Message, StringComparison.Ordinal);
        Assert.Contains("#392", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(Odyssey.Dtos.Finance.ExistingAccount), "ExistingAccount")]
    [InlineData(typeof(Odyssey.Dtos.PagedResult<Odyssey.Dtos.Finance.ExistingAccount>), "ExistingAccountPagedResult")]
    [InlineData(typeof(Odyssey.Dtos.Sex?), "SharedSex")]
    [InlineData(typeof(Odyssey.Dtos.Application.Sex), "ApplicationSex")]
    public void AContractType_GetsItsPublishedName(Type contract, string expected)
    {
        Assert.Equal(expected, OpenApiDocumentRegistration.SchemaReferenceId(Options.GetTypeInfo(contract)));
    }

    [Fact]
    public void APrimitive_StaysInline()
    {
        Assert.Null(OpenApiDocumentRegistration.SchemaReferenceId(Options.GetTypeInfo(typeof(string))));
    }
}
