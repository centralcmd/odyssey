using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Odyssey.TestData;
using Odyssey.TestData.Catalog;
using Odyssey.TestData.Fixtures;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// The contact-image permission matrix over real HTTP, against the seeded role users (issue #86 §10.5,
/// AC 14, 39, 40).
/// </summary>
/// <remarks>
/// <para>
/// The role model is deliberately NOT changed by this feature. <c>AdminClaims</c> and
/// <c>OwnerClaims</c> carry <c>contacts.update</c>; <c>UserClaims</c> holds <c>contacts.read</c> only —
/// so <b>only Admin and Owner can attach or remove an image</b>, which is consistent, since a User
/// cannot rename or edit a contact today either. The tempting repair, adding <c>ContactsUpdate</c> to
/// <c>UserClaims</c>, would silently grant every User <b>full contact editing</b>; that is a separate
/// product decision and a stated non-goal.
/// </para>
/// <para>
/// This runs against real login and real role claims rather than a synthesised principal, which is what
/// makes it a check on the <i>role model</i> and not just on the <c>[Authorize]</c> attributes.
/// </para>
/// </remarks>
[Collection(ApiStackCollection.Name)]
public class ContactAvatarPermissionTests(ApiStackFixture fixture)
{
    private static DemoUser UserFor(string role) => DemoUsers.All.First(user => user.Role == role);

    [SkippableTheory]
    [InlineData("Admin")]
    [InlineData("Owner")]
    public async Task A_role_holding_contacts_update_can_attach_and_remove_an_image(string role)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var actor = UserFor(role);
        var client = await fixture.CreateAuthenticatedClientAsync(actor.Email, actor.Password);
        var contactId = await CreateContactAsync(client, $"E2E Avatar {role} {Guid.NewGuid():N}");

        try
        {
            var upload = await UploadAsync(client, contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

            var contact = await upload.Content.ReadFromJsonAsync<ExistingContact>();
            Assert.NotNull(contact?.AvatarFileId);

            // The read is gated on contacts.read alone — no files.read — and carries the headers this
            // first inline-served untrusted-bytes surface needs.
            var read = await client.GetAsync($"/api/contacts/{contactId}/avatar?v={contact!.AvatarFileId}");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("same-site", read.Headers.GetValues("Cross-Origin-Resource-Policy").Single());

            var remove = await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contacts/{contactId}/avatar");
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contacts/{contactId}");
        }
    }

    [SkippableTheory]
    [InlineData("User")]
    [InlineData("Guest")]
    public async Task A_role_without_contacts_update_cannot_attach_or_remove_an_image(string role)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        // The seeded User role holds contacts.read only, so it CAN look at a picture and cannot change
        // one — the same line it already sits on for renaming a contact.
        var actor = UserFor(role);
        var client = await fixture.CreateAuthenticatedClientAsync(actor.Email, actor.Password);
        var contactId = Contacts.IdFor(Contacts.Landlord);

        var upload = await UploadAsync(client, contactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);

        var remove = await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contacts/{contactId}/avatar");
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
    }

    [SkippableTheory]
    [InlineData("User")]
    [InlineData("Guest")]
    public async Task A_role_holding_only_contacts_read_can_still_see_a_seeded_image(string role)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var actor = UserFor(role);
        var client = await fixture.CreateAuthenticatedClientAsync(actor.Email, actor.Password);
        var contactId = Contacts.IdFor(Contacts.Landlord);

        var response = await client.GetAsync(
            $"/api/contacts/{contactId}/avatar?v={Contacts.AvatarFileIdFor(Contacts.Landlord)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    public async Task A_revalidation_of_a_seeded_image_is_a_304_that_carries_no_body()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var actor = UserFor("Admin");
        var client = await fixture.CreateAuthenticatedClientAsync(actor.Email, actor.Password);
        var url = $"/api/contacts/{Contacts.IdFor(Contacts.FirstNationalBank)}/avatar"
            + $"?v={Contacts.AvatarFileIdFor(Contacts.FirstNationalBank)}";

        var first = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await client.SendAsync(conditional);

        // Under no-cache, revalidation is the hot path. This is the response that has to stay cheap.
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    private async Task<Guid> CreateContactAsync(HttpClient client, string legalName)
    {
        var response = await fixture.PostWithAntiforgeryAsync(client, "/api/contacts", new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new OrganizationDetailsDto { LegalName = legalName },
        });

        response.EnsureSuccessStatusCode();
        return Guid.Parse(response.Headers.Location!.Segments[^1]);
    }

    private async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid contactId, byte[] bytes, string contentType)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(part, "file", "crop.jpg");

        return await fixture.PostContentWithAntiforgeryAsync(
            client, $"/api/contacts/{contactId}/avatar", content);
    }
}
