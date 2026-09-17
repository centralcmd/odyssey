using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.TestData.Fixtures;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-tier coverage for the three profile-picture endpoints (issue #94 §7) — the token's presence
/// semantics, the validation refusals, the mass-assignment refusal, the claim matrix, the response
/// headers, and the two lockout predicates the read path must not confuse.
///
/// <para>
/// What is NOT here, deliberately: the cascade and transaction assertions (AC 15–21, 40), which need a
/// real engine — EF InMemory enforces no foreign keys and honours no transactions, so asserting
/// atomicity here would assert nothing. Those live in <c>Odyssey.IntegrationTests</c>.
/// </para>
///
/// <para>
/// The five claim/limit/header criteria run HERE rather than on <c>Odyssey.E2ETests.Api</c>, which is
/// <c>[SkippableFact]</c> and so <b>reports success having never run</b> without a live stack — that
/// would have left the CORP assertion, the one header value the spec argues about at length, silently
/// unenforced in CI. Only the real-role-claim criterion genuinely needs that tier.
/// </para>
/// </summary>
public class UserProfileImageApiTests
{
    private const string ActorUserId = "profile-image-actor";
    private const string OtherUserId = "profile-image-subject";

    /// <summary>Every role holds this, so it is the ordinary case rather than a privileged one.</summary>
    private static readonly string[] ImageReader = [PermissionClaims.ProfileImagesRead];

    /// <summary>
    /// A principal with a rich permission set but <b>not</b> <c>profile-images.read</c> — the
    /// pre-deploy session, and the shape a revocation produces. Passed explicitly so a future widening
    /// of the gate fails here rather than passing quietly under a role that happens to hold it.
    /// </summary>
    private static readonly string[] WithoutImageRead =
    [
        PermissionClaims.UsersRead, PermissionClaims.UsersUpdate, PermissionClaims.ContactsRead,
    ];

    // ── The token is the presence signal (AC 1, 2, 3) ─────────────────────────────────────────────

    [Fact]
    public async Task A_user_with_no_picture_has_a_null_token_and_uploading_sets_it()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);

        var before = await client.GetFromJsonAsync<ProfileDto>("/api/profile");
        Assert.Null(before!.ProfileImageVersion);

        var version = await UploadAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        Assert.NotEqual(Guid.Empty, version);

        var after = await client.GetFromJsonAsync<ProfileDto>("/api/profile");
        Assert.Equal(version, after!.ProfileImageVersion);
    }

    [Fact]
    public async Task A_replace_yields_a_new_token_and_leaves_exactly_one_row()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);

        var first = await UploadAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        // Byte-IDENTICAL, on purpose: the version describes the WRITE, not the bytes. A client that
        // reasoned about hash equality against its own prior state would get this wrong.
        var second = await UploadAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        Assert.NotEqual(first, second);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(1, await context.UserProfileImages.CountAsync(image => image.UserId == ActorUserId));
        Assert.Equal(1, await context.UserProfileImageBlobs.CountAsync());
    }

    [Fact]
    public async Task Deleting_clears_the_token_and_makes_the_read_path_404()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);
        await UploadAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        var removed = await client.DeleteAsync("/api/profile/image");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var profile = await client.GetFromJsonAsync<ProfileDto>("/api/profile");
        Assert.Null(profile!.ProfileImageVersion);

        var read = await client.GetAsync($"/api/profile-images/{ActorUserId}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    // ── The ?v= parameter is a cache key and nothing else (AC 4) ───────────────────────────────────

    [Fact]
    public async Task The_version_query_parameter_does_not_affect_the_response()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);
        var version = await UploadAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        var withoutV = await client.GetAsync($"/api/profile-images/{ActorUserId}");
        var withTrueV = await client.GetAsync($"/api/profile-images/{ActorUserId}?v={version}");
        var withNonsense = await client.GetAsync($"/api/profile-images/{ActorUserId}?v={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, withoutV.StatusCode);
        Assert.Equal("image/jpeg", withoutV.Content.Headers.ContentType?.MediaType);

        var expected = await withoutV.Content.ReadAsByteArrayAsync();
        Assert.Equal(expected, await withTrueV.Content.ReadAsByteArrayAsync());

        // Byte-identical for an ARBITRARY value, which is the point: the action does not bind `v` at
        // all, so "it does not affect the response" is true by construction rather than by discipline.
        Assert.Equal(expected, await withNonsense.Content.ReadAsByteArrayAsync());
    }

    // ── Validation refusals (AC 5, 6, 7) ──────────────────────────────────────────────────────────

    public static TheoryData<string, byte[], string> RejectedUploads() => new()
    {
        { "animated GIF", ContactImageFixtures.Gif(), "image/gif" },
        { "animated WebP", ContactImageFixtures.AnimatedWebp(), "image/webp" },
        { "animated PNG", ContactImageFixtures.AnimatedPng(), "image/png" },
        { "SVG", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), "image/svg+xml" },
        { "PDF renamed to .png", "%PDF-1.7\n%âãÏÓ\n"u8.ToArray(), "image/png" },
    };

    [Theory]
    [MemberData(nameof(RejectedUploads))]
    public async Task A_rejected_upload_is_a_400_and_writes_no_row(string _, byte[] bytes, string contentType)
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);

        var response = await PostAsync(client, bytes, contentType);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body));

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.UserProfileImages.AnyAsync());
        Assert.False(await context.UserProfileImageBlobs.AnyAsync());
    }

    [Fact]
    public async Task The_dimension_cap_binds_at_1024_and_the_message_names_the_actual_size()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);

        var over = await PostAsync(client, ContactImageFixtures.OverDimensionPng(), "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);

        var detail = await over.Content.ReadAsStringAsync();

        // The ACTUAL dimensions of the rejected image AND the cap. The fixture is 1100 × 900 — wider
        // than the cap but shorter, which is the more interesting shape: a message that named only the
        // cap, or that assumed a square, would read as wrong to the person looking at their own file.
        Assert.Contains("1100", detail, StringComparison.Ordinal);
        Assert.Contains("900", detail, StringComparison.Ordinal);
        Assert.Contains(UserProfileImageLimits.MaxImageDimension.ToString(), detail, StringComparison.Ordinal);

        var atCap = await PostAsync(client, ContactImageFixtures.MaxDimensionPng(), "image/png");
        Assert.Equal(HttpStatusCode.OK, atCap.StatusCode);
    }

    [Fact]
    public async Task A_lowered_instance_cap_binds_and_a_raised_one_does_not_widen_the_surface()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);

        // min(instance, surface) — a surface may tighten an instance-wide cap but must never override
        // one an administrator has lowered.
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileStorageMaxUploadMegabytes, "1");

        var lowered = await PostAsync(client, OneAndAHalfMegabytes(), "image/jpeg");
        Assert.Equal(HttpStatusCode.BadRequest, lowered.StatusCode);
        // Names the LOWERED number, not the compiled 2 MB.
        Assert.Contains("1", await lowered.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileStorageMaxUploadMegabytes, "64");

        var raised = await PostAsync(client, ThreeMegabytes(), "image/jpeg");
        Assert.Equal(HttpStatusCode.BadRequest, raised.StatusCode);
    }

    // ── The 404 is not an oracle (AC 8) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_404_is_byte_identical_for_a_missing_user_a_pictureless_one_and_a_disabled_one()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);

        // A subject who HAS a picture but whose account is administratively disabled. The sentinel is
        // set here as SETUP — it is the arrangement, not a second copy of the production predicate,
        // which is AccountLockout.IsAdministrativelyDisabled.
        await SeedSubjectAsync(factory, OtherUserId, AccountLockout.DisabledLockoutEnd);
        await SeedImageAsync(factory, OtherUserId);

        var pictureless = await SeedSubjectAsync(factory, "no-picture-user", lockoutEnd: null);

        var unknown = await client.GetAsync($"/api/profile-images/{Guid.NewGuid()}");
        var noPicture = await client.GetAsync($"/api/profile-images/{pictureless}");
        var disabled = await client.GetAsync($"/api/profile-images/{OtherUserId}");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noPicture.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);

        var first = await unknown.Content.ReadAsStringAsync();
        Assert.Equal(first, await noPicture.Content.ReadAsStringAsync());
        Assert.Equal(first, await disabled.Content.ReadAsStringAsync());

        // And it names no identifier at all — a user id is a different class of thing from the contact
        // endpoint's GUID echo, and the generic message costs nothing here.
        Assert.DoesNotContain(OtherUserId, first, StringComparison.Ordinal);
        Assert.DoesNotContain(pictureless, first, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 39 — <b>the criterion that separates the two lockout predicates</b>. A sentinel-disabled user
    /// also satisfies <c>LockoutEnd &gt; now</c>, so AC 8 and AC 37 pass under either one; only this
    /// fails if the read path uses <c>AccountLockout.IsEnabled</c>. Without it the <c>200 → 404 → 200</c>
    /// password-spray oracle ships silently.
    /// </summary>
    [Fact]
    public async Task A_transiently_locked_out_user_still_has_their_picture_served_and_their_token_kept()
    {
        await using var factory = new ApiFactory([.. ImageReader, PermissionClaims.UsersRead]);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);

        // Five mistyped passwords, not an administrative disable: a near-future LockoutEnd that
        // reverts on its own in about five minutes.
        await SeedSubjectAsync(factory, OtherUserId, DateTimeOffset.UtcNow.AddMinutes(4));
        await SeedImageAsync(factory, OtherUserId);

        var read = await client.GetAsync($"/api/profile-images/{OtherUserId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var listed = await client.GetFromJsonAsync<PagedUsers>("/api/users?limit=50");
        var row = Assert.Single(listed!.Items, item => item.Id == OtherUserId);
        Assert.NotNull(row.ProfileImageVersion);

        // The row legitimately reads "not enabled" beside a rendering picture. Deriving one from the
        // other is the tempting consistency fix that reintroduces the oracle.
        Assert.False(row.Enabled);
    }

    /// <summary>
    /// AC 37. Asserted through <c>MapUserAsync</c> — so it holds for the list, the detail panel's
    /// <c>GET /api/users/{id}</c> AND the <c>ExistingUser</c> the admin update returns, which is the
    /// first render after a disable and so the likeliest moment for a stale non-null token. Asserting
    /// only the list projection would pass while shipping the flicker one surface over.
    /// </summary>
    [Fact]
    public async Task An_administratively_disabled_subject_has_a_null_token_on_every_projection()
    {
        await using var factory = new ApiFactory([.. ImageReader, PermissionClaims.UsersRead, PermissionClaims.UsersUpdate]);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);

        await SeedSubjectAsync(factory, OtherUserId, lockoutEnd: null);
        await SeedImageAsync(factory, OtherUserId);

        // Enabled first: the token is present, which is what makes the nulling below meaningful.
        var enabledRow = await client.GetFromJsonAsync<ExistingUser>($"/api/users/{OtherUserId}");
        Assert.NotNull(enabledRow!.ProfileImageVersion);

        // The write that performs the disable — its own response is the first render afterwards.
        var patched = await client.PatchAsJsonAsync($"/api/users/{OtherUserId}", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var returned = await patched.Content.ReadFromJsonAsync<ExistingUser>();
        Assert.Null(returned!.ProfileImageVersion);

        // The detail panel's own fetch.
        var detail = await client.GetFromJsonAsync<ExistingUser>($"/api/users/{OtherUserId}");
        Assert.Null(detail!.ProfileImageVersion);

        // And the list.
        var listed = await client.GetFromJsonAsync<PagedUsers>("/api/users?limit=50");
        Assert.Null(Assert.Single(listed!.Items, item => item.Id == OtherUserId).ProfileImageVersion);
    }

    // ── Mass assignment (AC 9) ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Extra_form_fields_reach_nothing()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);

        using var content = new MultipartFormDataContent();
        var bytes = ContactImageFixtures.BaselineJpeg();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(part, "file", "picture.jpg");

        // None of these is a parameter of the action, so none of them reaches anything.
        content.Add(new StringContent(OtherUserId), "userId");
        content.Add(new StringContent(Guid.NewGuid().ToString()), "fileId");
        content.Add(new StringContent("application/pdf"), "contentType");
        content.Add(new StringContent("99999999"), "sizeBytes");

        var response = await client.PostAsync("/api/profile/image", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await context.UserProfileImages.AsNoTracking().SingleAsync();

        Assert.Equal(ActorUserId, row.UserId);
        Assert.Equal("image/jpeg", row.ContentType);
        Assert.True(row.SizeBytes > 0 && row.SizeBytes <= bytes.Length);
        Assert.NotEqual(99999999, row.SizeBytes);
    }

    // ── Route shape and gates (AC 10, 11) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task No_write_action_binds_a_user_id_and_neither_is_password_change_exempt()
    {
        await using var factory = new ApiFactory(ImageReader);
        _ = factory.CreateClient();

        var endpoints = factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("api/profile/image", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Equal(2, endpoints.Count);

        foreach (var endpoint in endpoints)
        {
            // The whole IDOR mitigation is structural: there is no id to tamper with, in route or body.
            Assert.DoesNotContain(endpoint.RoutePattern.Parameters, parameter =>
                parameter.Name.Contains("user", StringComparison.OrdinalIgnoreCase)
                || parameter.Name.Equals("id", StringComparison.OrdinalIgnoreCase));

            var descriptor = endpoint.Metadata
                .GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>();
            Assert.NotNull(descriptor);
            Assert.DoesNotContain(descriptor!.Parameters, parameter =>
                parameter.Name.Contains("user", StringComparison.OrdinalIgnoreCase));

            // A gated user changes their password first. The exemption is matched on method AND exact
            // route, so this is the default — but it is asserted rather than assumed.
            Assert.Null(endpoint.Metadata.GetMetadata<Odyssey.Api.Identity.IPasswordChangeExemptMetadata>());
        }
    }

    // ── Onboarding (AC 12) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_picture_stores_and_reads_back_before_the_user_has_a_profile_row()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();

        // The identity row alone — no UserProfile. The onboarding gate renders before a profile exists,
        // and the picture is keyed to the identity row rather than the profile row precisely so it
        // works there.
        await SeedSubjectAsync(factory, ActorUserId, lockoutEnd: null);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.UserProfiles.AnyAsync(profile => profile.UserId == ActorUserId));

        var version = await UploadAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        var read = await client.GetAsync($"/api/profile-images/{ActorUserId}?v={version}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var profile = await client.GetFromJsonAsync<ProfileDto>("/api/profile");
        Assert.Equal(version, profile!.ProfileImageVersion);
        Assert.False(profile.IsComplete);
    }

    // ── The claim matrix (AC 22) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reading_a_picture_needs_profile_images_read()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);
        await UploadAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        // The revocation lever, proved here and only here: the role→claim mapping is compiled into
        // RolePermissions and guarded by AuthorizationPolicyTests, so "revoke the claim from a role"
        // cannot be committed as a fixture at all. TestAuthHandler mints an arbitrary set instead.
        await using var denied = new ApiFactory(WithoutImageRead, sharingStoreWith: factory);
        using var deniedClient = denied.CreateClient();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await deniedClient.GetAsync($"/api/profile-images/{ActorUserId}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/api/profile-images/{ActorUserId}")).StatusCode);
    }

    /// <summary>
    /// The writes are claim-free and self-scoped, like <c>PUT /api/profile</c>. A caller who cannot
    /// READ pictures can still set and remove their own — which is precisely the pre-deploy session,
    /// and why <c>/account</c> disables the control rather than letting them write something they
    /// cannot see.
    /// </summary>
    [Fact]
    public async Task Writing_your_own_picture_needs_no_claim()
    {
        await using var factory = new ApiFactory(WithoutImageRead);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/profile/image")).StatusCode);
    }

    // ── Response headers (AC 25) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_read_response_carries_the_inline_untrusted_bytes_header_set()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);
        await UploadAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        var response = await client.GetAsync($"/api/profile-images/{ActorUserId}");

        // Compared as a SET: HttpClient's typed header parser reorders directives, so an ordered string
        // comparison would pin the parser's behaviour rather than the policy.
        var cacheControl = response.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl!.Private, "Cache-Control must be private.");
        Assert.True(cacheControl.NoCache, "no-cache, so a removed picture 404s on the next revalidation.");
        // no-STORE would forbid the bodiless 304 the revalidation path depends on.
        Assert.False(cacheControl.NoStore, "no-store would forbid the 304 this read path is built around.");
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(
            "default-src 'none'; sandbox",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));

        // same-SITE, not same-origin. CORP is enforced on no-cors subresource loads — exactly how an
        // <img src> loads — and compares scheme, host AND port, so same-origin would block every
        // picture outside Docker (client 5199, API 5188), and silently: a load failure degrades to the
        // monogram with nothing surfaced.
        var corp = Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy"));
        Assert.Equal("same-site", corp);
        Assert.NotEqual("same-origin", corp);

        // Strong, not weak: a weak ETag cannot license the 304 the revalidation path depends on.
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
    }

    [Fact]
    public async Task A_matching_If_None_Match_is_a_304_with_no_body()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);
        await UploadAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        var first = await client.GetAsync($"/api/profile-images/{ActorUserId}");
        var etag = first.Headers.ETag!;

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/profile-images/{ActorUserId}");
        conditional.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// The read path re-checks the STORED content type against the allow-list, so a row that somehow
    /// held a disallowed type reads as absent rather than streaming untrusted bytes inline. Not
    /// redundant with the write path's validation: this is what a hand-edited or restored row hits.
    /// </summary>
    [Fact]
    public async Task A_row_holding_a_disallowed_content_type_reads_as_absent()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);
        await UploadAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var row = await context.UserProfileImages.SingleAsync();
            row.ContentType = "application/pdf";
            await context.SaveChangesAsync();
        }

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/profile-images/{ActorUserId}")).StatusCode);
    }

    // ── Concurrency (AC 41) ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 41. Executable here as a deterministic <b>two-context sequence</b> rather than a true race:
    /// InMemory honours concurrency tokens but has no row locks, so "racing" would imply a
    /// non-determinism this tier cannot provide. What matters is that the stale DELETE is never a
    /// <c>500</c> — the concurrency token arms every tracked write, not only the replace path.
    /// </summary>
    [Fact]
    public async Task A_delete_against_a_bumped_version_is_never_a_500()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);
        await UploadAsync(client, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        // Context A loads the row; context B (the HTTP POST) bumps its version underneath.
        using var scopeA = factory.Services.CreateScope();
        var contextA = scopeA.ServiceProvider.GetRequiredService<OdysseyContext>();
        var stale = await contextA.UserProfileImages.SingleAsync(image => image.UserId == ActorUserId);

        await UploadAsync(client, ContactImageFixtures.BaselinePng(), "image/png");

        contextA.UserProfileImages.Remove(stale);
        var conflicted = await Record.ExceptionAsync(() => contextA.SaveChangesAsync());
        Assert.IsType<DbUpdateConcurrencyException>(conflicted);

        // And through HTTP, the user's intent is satisfied rather than 500ing.
        var response = await client.DeleteAsync("/api/profile/image");
        Assert.True(
            response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Conflict,
            $"Expected 204 or 409, got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Deleting_when_there_is_no_picture_is_a_404()
    {
        await using var factory = new ApiFactory(ImageReader);
        using var client = factory.CreateClient();
        await SeedEnabledActorAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/profile/image")).StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the acting user AND clears their lockout.
    ///
    /// <para>
    /// The clear is not incidental: <c>OdysseyContext</c> applies the admin-approval gate to every
    /// newly-added <c>ApplicationUser</c>, so a bare <c>SeedActorUserAsync</c> produces an account
    /// carrying the <i>disabled sentinel</i> — and the read path would then correctly <c>404</c> every
    /// picture in this class. Same step <c>PasswordGateFactory</c> and <c>LegalLoginFactory</c> take.
    /// </para>
    /// </summary>
    private static async Task SeedEnabledActorAsync(ApiFactory factory)
    {
        await factory.SeedActorUserAsync();
        await SeedSubjectAsync(factory, ActorUserId, lockoutEnd: null);
    }

    private static async Task<Guid> UploadAsync(HttpClient client, byte[] bytes, string contentType)
    {
        var response = await PostAsync(client, bytes, contentType);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ProfileImageVersionDto>();
        return body!.ImageVersion;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, byte[] bytes, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(part, "file", "picture");
        return await client.PostAsync("/api/profile/image", content);
    }

    /// <summary>
    /// Seeds an identity row and puts it in the requested lockout state. <paramref name="lockoutEnd"/>
    /// is the ARRANGEMENT — the sentinel for an administrative disable, a near-future value for a
    /// transient one — and is deliberately not routed through the production predicate, which is the
    /// thing under test.
    /// </summary>
    /// <remarks>
    /// The insert and the lockout are TWO saves on purpose. <c>OdysseyContext</c> applies the
    /// admin-approval gate to every newly-<i>Added</i> <c>ApplicationUser</c> during
    /// <c>SaveChangesAsync</c>, stamping the disabled sentinel over whatever the fixture set — so
    /// setting <c>LockoutEnd</c> on the new entity and saving once silently produces a disabled account
    /// every time, which would make the transient-lockout criterion (the one that separates the two
    /// predicates) pass for the wrong reason.
    /// </remarks>
    private static async Task<string> SeedSubjectAsync(ApiFactory factory, string userId, DateTimeOffset? lockoutEnd)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var user = await context.Users.FirstOrDefaultAsync(candidate => candidate.Id == userId);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = userId,
                UserName = $"{userId}@example.com",
                NormalizedUserName = $"{userId}@EXAMPLE.COM",
                Email = $"{userId}@example.com",
                NormalizedEmail = $"{userId}@EXAMPLE.COM",
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
        }

        user.LockoutEnd = lockoutEnd;
        await context.SaveChangesAsync();
        return userId;
    }

    /// <summary>
    /// Plants a picture for a subject the test caller cannot write to — there is no administrator
    /// write path, by design, so the store is the only way to arrange another user's picture.
    /// </summary>
    private static async Task SeedImageAsync(ApiFactory factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var bytes = ContactImageFixtures.BaselineJpeg();
        var id = Guid.NewGuid();
        context.UserProfileImages.Add(new UserProfileImage
        {
            UserProfileImageId = id,
            UserId = userId,
            ImageVersion = Guid.NewGuid(),
            ContentType = "image/jpeg",
            SizeBytes = bytes.LongLength,
            Sha256Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            Width = 8,
            Height = 8,
            UploadedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        context.UserProfileImageBlobs.Add(new UserProfileImageBlob
        {
            UserProfileImageId = id,
            Content = bytes,
        });

        await context.SaveChangesAsync();
    }

    /// <summary>A body over 1 MB but under 2 MB, so only a LOWERED instance cap rejects it.</summary>
    private static byte[] OneAndAHalfMegabytes() => PaddedJpeg(1_572_864);

    /// <summary>A body over both caps, so raising the instance cap cannot admit it.</summary>
    private static byte[] ThreeMegabytes() => PaddedJpeg(3 * 1024 * 1024);

    /// <summary>
    /// A JPEG-headed buffer of a given length. It never reaches the walk — the action's own byte check
    /// rejects it first, which is the thing under test — so the padding does not have to parse.
    /// </summary>
    private static byte[] PaddedJpeg(int length)
    {
        var buffer = new byte[length];
        var header = ContactImageFixtures.BaselineJpeg();
        header.CopyTo(buffer, 0);
        return buffer;
    }

    private sealed record PagedUsers(List<ExistingUser> Items, int TotalCount);

    private sealed class ApiFactory(
        IReadOnlyCollection<string>? permissions, OdysseyApiFactory? sharingStoreWith = null)
        : OdysseyApiFactory(permissions, ActorUserId, sharingStoreWith: sharingStoreWith);
}
