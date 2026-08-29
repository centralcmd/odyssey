using Xunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Odyssey.Core.Configuration;

namespace Odyssey.Api.Tests;

public class DatabaseConfigurationTests
{
    [Fact]
    public void GetRequiredConnectionString_ReturnsValue_WhenConfigured()
    {
        // Arrange
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OdysseyConnection"] = "server=localhost;database=odyssey;"
        });

        // Act
        var connectionString = configuration.GetRequiredConnectionString("OdysseyConnection");

        // Assert
        Assert.Equal("server=localhost;database=odyssey;", connectionString);
    }

    // The shipped appsettings.json declares every key as "", so the old `?? throw` guard could not
    // fire and UseMySql("") threw an ArgumentException naming neither the key nor the fix (#422).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetRequiredConnectionString_Throws_WhenMissingOrBlank(string? value)
    {
        // Arrange
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OdysseyConnection"] = value
        });

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(
            () => configuration.GetRequiredConnectionString("OdysseyConnection"));

        Assert.Contains("Connection string 'OdysseyConnection' is not configured.", exception.Message);
        Assert.Contains("ConnectionStrings:OdysseyConnection", exception.Message);
        Assert.Contains("UseInMemoryDatabase=true", exception.Message);
    }

    [Fact]
    public void AddDatabases_ThrowsNamingTheKey_WhenAConnectionStringIsBlank()
    {
        // Arrange
        var builder = CreateApiBuilder(new Dictionary<string, string?>
        {
            ["UseInMemoryDatabase"] = "false",
            ["ConnectionStrings:OdysseyConnection"] = ""
        });

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddDatabases());

        Assert.Contains("Connection string 'OdysseyConnection' is not configured.", exception.Message);
    }

    [Fact]
    public void AddDatabases_Succeeds_WhenEveryConnectionStringIsConfigured()
    {
        // Arrange
        var builder = CreateApiBuilder(new Dictionary<string, string?>
        {
            ["UseInMemoryDatabase"] = "false",
            ["ConnectionStrings:OdysseyConnection"] = "server=localhost;database=odyssey;"
        });

        // Act & Assert (registration must not touch the server)
        builder.AddDatabases();
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // Development rather than Testing: AddDatabases short-circuits to the in-memory provider for the
    // Testing environment, which is the branch these tests are not about.
    private static WebApplicationBuilder CreateApiBuilder(Dictionary<string, string?> values)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        // Added last, so it wins over any ConnectionStrings__* left in the ambient environment.
        builder.Configuration.AddInMemoryCollection(values);

        return builder;
    }
}
