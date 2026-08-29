using Xunit;
using Odyssey.Api;
using Microsoft.Extensions.Configuration;

namespace Odyssey.Api.Tests;

public class CorsConfigurationTests
{
    [Fact]
    public void GetAllowedOrigins_ReturnsConfiguredOrigins()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "http://localhost:5199",
                ["Cors:AllowedOrigins:1"] = "https://localhost:7199"
            })
            .Build();

        // Act
        var origins = CorsConfiguration.GetAllowedOrigins(configuration);

        // Assert
        Assert.Equal(["http://localhost:5199", "https://localhost:7199"], origins);
    }

    [Fact]
    public void GetAllowedOrigins_Throws_WhenSectionMissing()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => CorsConfiguration.GetAllowedOrigins(configuration));
    }

    [Theory]
    [InlineData("http://localhost:5199")]
    [InlineData("https://localhost:7199")]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1:5199")]
    [InlineData("http://[::1]:5199")]
    [InlineData("https://fluffy-space-guide-abc123-5199.app.github.dev")]
    public void IsDevelopmentOriginAllowed_ReflectsLocalAndCodespacesOrigins(string origin)
    {
        Assert.True(CorsConfiguration.IsDevelopmentOriginAllowed(origin));
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    // The suffix must be matched as a host suffix, not a substring: both of these end with the
    // Codespaces domain as text but resolve elsewhere.
    [InlineData("https://app.github.dev.evil.example.com")]
    [InlineData("https://notapp.github.dev")]
    // A LAN address is a real remote origin — a phone testing against the dev machine goes through
    // Cors:AllowedOrigins, not through reflection.
    [InlineData("http://192.168.1.20:5199")]
    // Origins are absolute; a bare host, a path, or the null origin are not reflectable.
    [InlineData("localhost:5199")]
    [InlineData("http://localhost:5199/api")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(null)]
    public void IsDevelopmentOriginAllowed_RejectsEverythingElse(string? origin)
    {
        Assert.False(CorsConfiguration.IsDevelopmentOriginAllowed(origin));
    }

    [Fact]
    public void IsDevelopmentOriginAllowed_RejectsNonHttpSchemes()
    {
        // file:// and similar parse as absolute URIs with a matching host, so the scheme is checked
        // separately rather than being assumed.
        Assert.False(CorsConfiguration.IsDevelopmentOriginAllowed("file://localhost/etc/passwd"));
        Assert.False(CorsConfiguration.IsDevelopmentOriginAllowed("ws://localhost:5199"));
    }
}
