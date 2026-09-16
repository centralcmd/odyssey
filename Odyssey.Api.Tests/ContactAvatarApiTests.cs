using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Odyssey.TestData.Fixtures;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-tier coverage for the three contact-image endpoints (issue #86 §7): the claim matrix, the
/// response headers this first inline-served untrusted-bytes surface carries, conditional requests,
/// the release rule's read-path and replace-path halves, and the mass-assignment refusal.
///
/// <para>
/// What is NOT here, deliberately: the transactional row-count assertions (AC 2–4) and the
/// third-party metadata oracle (AC 10), both of which live in <c>Odyssey.IntegrationTests</c>. The EF
/// InMemory provider honours neither transactions nor foreign keys, so asserting atomicity here would
/// assert nothing.
/// </para>
/// </summary>
public class ContactAvatarApiTests
{
    private const string ActorUserId = "avatar-actor-id";

    private static readonly string[] ReadOnly = [PermissionClaims.ContactsRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate,
        PermissionClaims.ContactsUpdate, PermissionClaims.ContactsDelete,
    ];

    /// <summary>
    /// The write claims a contact image needs and <b>nothing else</b> — no <c>files.create</c>, no
    /// <c>files.delete</c>. Passed explicitly so a future widening of the gate fails these tests rather
    /// than passing quietly under a role that happens to hold both.
    /// </summary>
    private static readonly string[] ContactsOnly =
    [
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate, PermissionClaims.ContactsUpdate,
    ];

    // ── Claim matrix (AC 14) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reading_an_image_needs_only_contacts_read()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);
        await UploadAsync(client, contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        await using var reader = new ApiFactory(ReadOnly, sharingStoreWith: factory);
        using var readerClient = reader.CreateClient();

        var response = await readerClient.GetAsync($"/api/contacts/{contactId}/avatar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Reading_an_image_with_files_read_but_not_contacts_read_is_forbidden()
    {
        // The image IS contact data. Gating it on files.read as well would mirror, on the read path,
        // exactly the claim-coupling the write path refuses.
        await using var factory = new ApiFactory([PermissionClaims.FilesRead]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/contacts/{Guid.NewGuid()}/avatar");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Writing_an_image_succeeds_without_files_create_or_files_delete()
    {
        // The boundedness argument in the flesh: contacts.update alone buys "<= 2 MB, one of three
        // still image types, magic-checked, <= 1024 square, non-animated, stripped and re-validated,
        // with a generated filename" — and nothing files.create would buy.
        await using var factory = new ApiFactory(ContactsOnly);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);

        var upload = await UploadAsync(client, contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var delete = await client.DeleteAsync($"/api/contacts/{contactId}/avatar");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Writing_an_image_without_contacts_update_is_forbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var upload = await UploadAsync(client, Guid.NewGuid(), ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        var delete = await client.DeleteAsync($"/api/contacts/{Guid.NewGuid()}/avatar");

        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ── The response (AC 1) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_uploaded_image_comes_back_with_the_headers_an_inline_untrusted_body_needs()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);
        await UploadAsync(client, contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        var response = await client.GetAsync($"/api/contacts/{contactId}/avatar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal("default-src 'none'; sandbox", Single(response, "Content-Security-Policy"));

        // same-site, NOT same-origin: CORP compares scheme, host AND port, and the client serves from a
        // different port than the API outside Docker — so same-origin would have blocked every avatar
        // there, silently, since a load failure degrades to the type glyph.
        Assert.Equal("same-site", Single(response, "Cross-Origin-Resource-Policy"));

        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
    }

    [Fact]
    public async Task The_stored_bytes_are_returned_not_the_uploaded_ones()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);

        var source = ContactImageFixtures.JpegWithMpfSecondImage();
        await UploadAsync(client, contactId, source, "image/jpeg");

        var stored = await (await client.GetAsync($"/api/contacts/{contactId}/avatar")).Content.ReadAsByteArrayAsync();

        Assert.True(stored.Length < source.Length);
        Assert.DoesNotContain("MPF", System.Text.Encoding.Latin1.GetString(stored), StringComparison.Ordinal);
    }

    // ── Conditional requests (AC 18's HTTP half) ──────────────────────────────────────────────────

    [Fact]
    public async Task A_matching_if_none_match_returns_304_with_no_body()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);
        await UploadAsync(client, contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        var first = await client.GetAsync($"/api/contacts/{contactId}/avatar");
        var etag = first.Headers.ETag!;

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/contacts/{contactId}/avatar");
        conditional.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_stale_if_none_match_returns_the_body()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);
        await UploadAsync(client, contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/contacts/{contactId}/avatar");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"not-the-current-hash\""));
        var response = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    // ── Replace and remove ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_second_upload_replaces_the_first_and_re_keys_the_url()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);

        var first = await ReadContactAsync(
            await UploadAsync(client, contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg"));
        var second = await ReadContactAsync(
            await UploadAsync(client, contactId, ContactImageFixtures.BaselinePng(), "image/png"));

        Assert.NotNull(first.AvatarFileId);
        Assert.NotNull(second.AvatarFileId);
        Assert.NotEqual(first.AvatarFileId, second.AvatarFileId);

        // The re-key is the point: it is what makes revalidation after a replace a guaranteed 304
        // rather than a full re-download.
        var response = await client.GetAsync($"/api/contacts/{contactId}/avatar");
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Removing_an_image_leaves_the_contact_intact_and_its_image_absent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);
        await UploadAsync(client, contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        var delete = await client.DeleteAsync($"/api/contacts/{contactId}/avatar");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var contact = await client.GetFromJsonAsync<ExistingContact>($"/api/contacts/{contactId}");
        Assert.NotNull(contact);
        Assert.Null(contact!.AvatarFileId);
        Assert.Null(contact.Archived);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/contacts/{contactId}/avatar")).StatusCode);
    }

    [Fact]
    public async Task Removing_an_image_a_contact_does_not_have_is_a_404()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/contacts/{contactId}/avatar")).StatusCode);
    }

    [Fact]
    public async Task Uploading_to_a_contact_that_does_not_exist_is_a_404()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await UploadAsync(client, Guid.NewGuid(), ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Validation surfaces as a 400 that names the limit (AC 6, 7, 8) ─────────────────────────────

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    public async Task A_type_outside_the_avatar_allow_list_is_a_400_naming_the_accepted_types(string contentType)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);

        var body = contentType == "image/gif"
            ? ContactImageFixtures.Gif()
            : "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray();

        var response = await UploadAsync(client, contactId, body, contentType);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(ContactAvatarLimits.TypeLabel, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_png_declared_as_jpeg_is_a_400()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);

        var response = await UploadAsync(client, contactId, ContactImageFixtures.BaselinePng(), "image/jpeg");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_over_dimension_image_is_a_400_naming_the_dimension_cap()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);

        var response = await UploadAsync(client, contactId, ContactImageFixtures.OverDimensionPng(), "image/png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            ContactAvatarLimits.MaxAvatarDimension.ToString(),
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_animated_image_is_a_400()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);

        var response = await UploadAsync(client, contactId, ContactImageFixtures.AnimatedWebp(), "image/webp");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task No_error_body_carries_a_filename_a_hash_or_a_byte_count_of_the_stored_image()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);

        var response = await UploadAsync(
            client, contactId, ContactImageFixtures.Gif(), "image/gif", fileName: "holiday-in-oslo-2019.gif");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("holiday-in-oslo", body, StringComparison.OrdinalIgnoreCase);
    }

    // ── The stored row (AC 19) ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/jpeg", ".jpg")]
    public async Task The_stored_filename_is_generated_and_its_extension_follows_the_validated_type(
        string contentType, string extension)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client, firstName: "Ada", lastName: "Lovelace");

        var body = contentType switch
        {
            "image/png" => ContactImageFixtures.BaselinePng(),
            "image/webp" => ContactImageFixtures.StillWebp(),
            _ => ContactImageFixtures.BaselineJpeg(),
        };
        await UploadAsync(client, contactId, body, contentType, fileName: "my-holiday-snap.bin");

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var contact = context.Contacts.Single(c => c.ContactId == contactId);
        var file = context.FileMetadata.Single(fm => fm.Id == contact.AvatarFileId);

        Assert.EndsWith(extension, file.FileName, StringComparison.Ordinal);
        Assert.Equal(contentType, file.ContentType);

        // Neither the user's original filename (which can itself carry personal data) nor the contact's
        // name, and a fixed non-PII description.
        Assert.DoesNotContain("holiday", file.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lovelace", file.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Contact image", file.Description);
    }

    // ── Mass assignment (AC 15) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_form_body_carrying_a_fileId_neither_repoints_the_avatar_nor_touches_that_file()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);

        // A file the caller must not be able to reach through a contacts.* claim.
        Guid foreignFileId;
        using (var seedScope = factory.Services.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var blob = new FileBlob { Id = Guid.NewGuid(), Content = "%PDF-1.7 confidential"u8.ToArray() };
            var metadata = new FileMetadata
            {
                Id = Guid.NewGuid(),
                FileName = "tax-statement.pdf",
                ContentType = "application/pdf",
                SizeBytes = blob.Content.LongLength,
                Sha256Hash = new string('a', 64),
                FileBlobId = blob.Id,
                UploadedAtUtc = DateTime.UtcNow,
            };
            context.FileBlob.Add(blob);
            context.FileMetadata.Add(metadata);
            context.SaveChanges();
            foreignFileId = metadata.Id;
        }

        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(ContactImageFixtures.BaselineJpeg());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(part, "file", "crop.jpg");
        // The over-post: a caller-supplied id, plus a second file part under a name the action does not
        // bind. Neither is a parameter of the action, so neither reaches anything.
        content.Add(new StringContent(foreignFileId.ToString()), "fileId");
        content.Add(new StringContent(foreignFileId.ToString()), "avatarFileId");
        var nested = new ByteArrayContent("%PDF-1.7 attacker"u8.ToArray());
        nested.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(nested, "file.fileId", "nested.pdf");

        var response = await client.PostAsync($"/api/contacts/{contactId}/avatar", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var verifyContext = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var contact = verifyContext.Contacts.Single(c => c.ContactId == contactId);

        Assert.NotEqual(foreignFileId, contact.AvatarFileId);

        // The foreign row is untouched, and exactly ONE new file row was created — the one built from
        // the uploaded bytes.
        var foreign = verifyContext.FileMetadata.Single(fm => fm.Id == foreignFileId);
        Assert.Equal("tax-statement.pdf", foreign.FileName);
        Assert.Equal("application/pdf", foreign.ContentType);
        Assert.Equal(2, verifyContext.FileMetadata.Count());
    }

    // ── The release rule, read and replace halves (AC 16) ─────────────────────────────────────────

    [Fact]
    public async Task A_contact_mis_pointed_at_a_non_image_reads_as_having_no_image()
    {
        // The unique index prevents SHARING a file; it does not prevent a row pointing somewhere it
        // should not. Without the read-path content-type check this would stream a tax statement to any
        // contacts.read holder — a Guest included.
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);
        var pdfId = MisPoint(factory, contactId);

        var response = await client.GetAsync($"/api/contacts/{contactId}/avatar");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(FileExists(factory, pdfId));
    }

    [Fact]
    public async Task Deleting_a_mis_pointed_image_detaches_without_destroying_the_file()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);
        var pdfId = MisPoint(factory, contactId);

        var response = await client.DeleteAsync($"/api/contacts/{contactId}/avatar");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(AvatarFileIdOf(factory, contactId));

        // Detach-never-delete: a mis-pointed reference is a defect to investigate, not a licence to
        // destroy what it points at.
        Assert.True(FileExists(factory, pdfId));
    }

    [Fact]
    public async Task Replacing_a_mis_pointed_image_repoints_without_destroying_the_file()
    {
        // The site an earlier draft MISSED, and the likeliest of the three to be reached: replacing an
        // image is an ordinary action while deleting one is not.
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var contactId = await CreatePersonAsync(client);
        var pdfId = MisPoint(factory, contactId);

        var response = await UploadAsync(client, contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(pdfId, AvatarFileIdOf(factory, contactId));
        Assert.True(FileExists(factory, pdfId));
    }

    // ── The read DTO's boundary (AC 20, 21) ───────────────────────────────────────────────────────

    [Fact]
    public void The_read_dto_gains_AvatarFileId_and_no_other_member()
    {
        var members = typeof(ExistingContact).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(ExistingContact.AvatarFileId), members);
        Assert.Equal(typeof(Guid?), typeof(ExistingContact).GetProperty(nameof(ExistingContact.AvatarFileId))!.PropertyType);

        // Data minimisation: a Guid? and nothing else — no dimensions, filename, uploader or timestamp.
        Assert.DoesNotContain("AvatarWidth", members);
        Assert.DoesNotContain("AvatarHeight", members);
        Assert.DoesNotContain("AvatarFileName", members);
        Assert.DoesNotContain("AvatarUploadedAtUtc", members);
        Assert.DoesNotContain("AvatarUploadedByUserId", members);
    }

    [Fact]
    public void The_cross_claim_contact_projection_is_not_widened_with_an_image()
    {
        // ContactEmbed is the boundary keeping a transactions.read-only caller from receiving a
        // photograph of a natural person. Its own guard test pins the member count; this one names the
        // member that must never appear on it.
        var members = typeof(ContactEmbed).GetProperties().Select(p => p.Name).ToList();

        Assert.Equal(2, members.Count);
        Assert.DoesNotContain(nameof(ExistingContact.AvatarFileId), members);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static string? Single(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<Guid> CreatePersonAsync(
        HttpClient client, string firstName = "Ada", string lastName = "Lovelace")
    {
        var response = await client.PostAsJsonAsync("/api/contacts", new NewContact
        {
            Type = ContactType.Person,
            Archived = false,
            PersonDetails = new PersonDetailsDto { FirstName = firstName, LastName = lastName },
        });

        response.EnsureSuccessStatusCode();
        return Guid.Parse(response.Headers.Location!.Segments[^1]);
    }

    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid contactId, byte[] bytes, string contentType, string fileName = "crop.bin")
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(part, "file", fileName);
        return client.PostAsync($"/api/contacts/{contactId}/avatar", content);
    }

    private static async Task<ExistingContact> ReadContactAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingContact>())!;
    }

    /// <summary>Points a contact at a non-image file, the anomaly the release rule exists to survive.</summary>
    private static Guid MisPoint(ApiFactory factory, Guid contactId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = "%PDF-1.7 tax statement"u8.ToArray() };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            FileName = "tax-statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = blob.Content.LongLength,
            Sha256Hash = new string('b', 64),
            FileBlobId = blob.Id,
            UploadedAtUtc = DateTime.UtcNow,
        };
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(metadata);

        var contact = context.Contacts.Single(c => c.ContactId == contactId);
        contact.AvatarFileId = metadata.Id;
        context.SaveChanges();

        return metadata.Id;
    }

    /// <summary>
    /// Both halves of the file are keyed on <paramref name="fileId"/>, deliberately.
    /// </summary>
    /// <remarks>
    /// An earlier version asked the blob half only whether SOME blob existed anywhere in the store
    /// (<c>Any(b =&gt; b.Id != Guid.Empty)</c>), which the replace test satisfies with the NEW avatar's
    /// blob — so a mis-pointed file whose blob row was wrongly destroyed while its metadata row
    /// survived would still have read as intact. That orphan-metadata state is exactly what the
    /// release rule exists to prevent, so the check has to name the row it means.
    /// </remarks>
    private static bool FileExists(ApiFactory factory, Guid fileId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var blobId = context.FileMetadata
            .Where(fm => fm.Id == fileId)
            .Select(fm => (Guid?)fm.FileBlobId)
            .SingleOrDefault();

        return blobId is not null && context.FileBlob.Any(b => b.Id == blobId);
    }

    private static Guid? AvatarFileIdOf(ApiFactory factory, Guid contactId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return context.Contacts.Single(c => c.ContactId == contactId).AvatarFileId;
    }

    private sealed class ApiFactory(
        IReadOnlyCollection<string>? permissions, OdysseyApiFactory? sharingStoreWith = null)
        : OdysseyApiFactory(permissions, ActorUserId, sharingStoreWith: sharingStoreWith);
}
