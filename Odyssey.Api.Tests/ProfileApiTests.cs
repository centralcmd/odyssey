using System.Net;
using System.Net.Http.Json;
using Odyssey.Dtos.Application;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

/// <summary>
/// API tests for the self-service profile endpoints (issue #316): round-trip, completeness, the §9
/// validation rules, and that <c>IsComplete</c> is server-computed (never over-postable).
/// </summary>
public sealed class ProfileApiTests
{
    private const string Path = "/api/profile";

    // An authenticated caller with no permission claims — the profile endpoints need only a session.
    private static readonly string[] Authenticated = [];

    private static ProfileDto CompleteProfile() => new()
    {
        FirstName = "Ada",
        LastName = "Lindqvist",
        MiddleName = "Marie",
        DisplayName = "Ada L.",
        Title = "Dr.",
        BirthDate = new DateOnly(1985, 3, 12),
        Sex = Sex.Female,
    };

    [Fact]
    public async Task Get_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new OdysseyApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_NewUser_ReturnsEmptyIncompleteProfile()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        var profile = await client.GetFromJsonAsync<ProfileDto>(Path);

        Assert.NotNull(profile);
        Assert.Null(profile!.FirstName);
        Assert.Null(profile.Sex);
        Assert.False(profile.IsComplete);
    }

    [Fact]
    public async Task Put_Then_Get_RoundTrips_AndReportsComplete()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync(Path, CompleteProfile());
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await put.Content.ReadFromJsonAsync<ProfileDto>();
        Assert.True(saved!.IsComplete);
        Assert.Equal("Ada", saved.FirstName);
        Assert.Equal("Ada L.", saved.DisplayName);
        Assert.Equal(Sex.Female, saved.Sex);
        Assert.Equal(new DateOnly(1985, 3, 12), saved.BirthDate);

        var fetched = await client.GetFromJsonAsync<ProfileDto>(Path);
        Assert.Equal("Lindqvist", fetched!.LastName);
        Assert.Equal("Marie", fetched.MiddleName);
        Assert.Equal("Dr.", fetched.Title);
        Assert.True(fetched.IsComplete);
    }

    [Fact]
    public async Task Put_MissingRequiredField_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        var request = CompleteProfile();
        request.LastName = null;

        var put = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Put_EmailFormatDisplayName_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        var request = CompleteProfile();
        request.DisplayName = "attacker@example.com";

        var put = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Put_ControlCharacterInName_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        var request = CompleteProfile();
        request.FirstName = "Ada\nInjected";

        var put = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Put_FutureBirthDate_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        var request = CompleteProfile();
        request.BirthDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);

        var put = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Put_BirthDateBefore1900_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        var request = CompleteProfile();
        request.BirthDate = new DateOnly(1899, 12, 31);

        var put = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Put_UndefinedSex_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        // Sex = 99 is not a defined enum member → [EnumDataType] model validation rejects it.
        var request = new
        {
            FirstName = "Ada",
            LastName = "Lindqvist",
            BirthDate = "1985-03-12",
            Sex = 99,
        };

        var put = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Put_OverLengthFirstName_ReturnsBadRequest()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        // 129 chars exceeds the [StringLength(128)] on FirstName → rejected by [ApiController] validation.
        var request = CompleteProfile();
        request.FirstName = new string('a', 129);

        var put = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Put_IsCompleteFalseOnCompleteBody_IsIgnored_ServerComputesTrue()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        // A complete body that over-posts IsComplete=false — the server ignores the flag and computes it.
        var request = new
        {
            FirstName = "Ada",
            LastName = "Lindqvist",
            BirthDate = "1985-03-12",
            Sex = 2, // Female
            IsComplete = false,
        };

        var put = await client.PutAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await put.Content.ReadFromJsonAsync<ProfileDto>();

        Assert.True(saved!.IsComplete);
    }

    [Fact]
    public async Task Put_OptionalFieldsClearOnBlank()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        await client.PutAsJsonAsync(Path, CompleteProfile());

        var clear = CompleteProfile();
        clear.MiddleName = "";
        clear.Title = "   ";
        clear.DisplayName = null;

        var put = await client.PutAsJsonAsync(Path, clear);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await put.Content.ReadFromJsonAsync<ProfileDto>();

        Assert.Null(saved!.MiddleName);
        Assert.Null(saved.Title);
        Assert.Null(saved.DisplayName);
        Assert.True(saved.IsComplete);
    }

    [Fact]
    public async Task Put_CannotOverpostCompletenessOnIncompleteBody()
    {
        await using var factory = new OdysseyApiFactory(Authenticated);
        using var client = factory.CreateClient();

        // IsComplete is response-only; supplying it on an incomplete body cannot force a save.
        var request = new
        {
            FirstName = "Ada",
            // no LastName / BirthDate / Sex
            IsComplete = true,
        };

        var put = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }
}
