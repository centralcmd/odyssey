using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using System.Net;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The staged-create flow behind every inline "Add ‹name›" row — <see cref="ContactQuickCreate"/> and
/// <see cref="TagQuickCreate{TTag}"/>.
/// </summary>
/// <remarks>
/// <para>
/// A picker's create callback has to hand back the option to select in the same gesture, so it cannot
/// await: the option carries a temporary GUID and the POST runs behind it. Every host then awaits
/// <c>WhenSettledAsync</c> and maps its ids through <c>Resolve</c> before submitting.
/// </para>
/// <para>
/// The rule that matters most is the failure one. A create the server rejected must resolve to
/// <c>null</c>, never to the temporary id — posting one would send an id no server row backs, and on
/// the transaction form it would sink the whole save with it. That is invisible on the happy path,
/// which is exactly why it is pinned here rather than left to the hosts' own tests.
/// </para>
/// </remarks>
public class QuickCreateServiceTests
{
    private static readonly Uri Created = new("http://localhost/api/contacts/3f7c1a92-1111-4c2b-9f4e-0d1a2b3c4d5e");
    private static readonly Guid CreatedId = Guid.Parse("3f7c1a92-1111-4c2b-9f4e-0d1a2b3c4d5e");

    private static ApiResult Ok() => ApiResult.Success(HttpStatusCode.Created, Created);

    private static ApiResult Fail(HttpStatusCode status, string detail) =>
        ApiResult.Failure(status, new ApiProblem { Status = (int)status, Detail = detail });

    // The toast is a side effect these tests don't assert on; a stub keeps them off MudBlazor's
    // service graph, which a plain unit test has no reason to stand up.
    private static ISnackbar Snackbar() => Mock.Of<ISnackbar>();

    // ── ContactQuickCreate ───────────────────────────────────────────────────

    private static (ContactQuickCreate Creator, Mock<IContactsApiClient> Api, Mock<IReferenceDataCache> Cache)
        ContactCreator(Func<NewContact, ApiResult> respond)
    {
        var api = new Mock<IContactsApiClient>();
        api.Setup(a => a.CreateAsync(It.IsAny<NewContact>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NewContact c, CancellationToken _) => respond(c));

        var cache = new Mock<IReferenceDataCache>();
        return (new ContactQuickCreate(api.Object, cache.Object, Snackbar()), api, cache);
    }

    [Fact]
    public async Task A_staged_contact_resolves_to_the_id_the_server_issued()
    {
        var (creator, _, cache) = ContactCreator(_ => Ok());

        var option = creator.Begin("Kiwi Minipris", nameof(ContactType.Organization));
        await creator.WhenSettledAsync();

        Assert.NotNull(option);
        Assert.Equal("Kiwi Minipris", option.Label);
        Assert.Equal(CreatedId.ToString(), creator.Resolve(option.Value));

        // The session-wide contact cache now holds a list without the new record.
        cache.Verify(c => c.InvalidateContacts(), Times.Once);
    }

    /// <summary>
    /// The rule the whole shape exists for: a rejected create resolves to null, so the host drops the
    /// link instead of posting an id the server never issued.
    /// </summary>
    [Fact]
    public async Task A_rejected_contact_resolves_to_null_and_reports_its_temporary_id()
    {
        string? failed = null;
        var (creator, _, _) = ContactCreator(_ => Fail(HttpStatusCode.BadRequest, "nope"));
        creator.OnCreateFailed = id => failed = id;

        var option = creator.Begin("Kiwi Minipris", nameof(ContactType.Organization))!;
        await creator.WhenSettledAsync();

        Assert.Null(creator.Resolve(option.Value));
        Assert.Equal(option.Value, failed);
    }

    /// <summary>A thrown request is the same outcome as a rejected one — never a resolved id.</summary>
    [Fact]
    public async Task A_thrown_request_resolves_to_null()
    {
        var api = new Mock<IContactsApiClient>();
        api.Setup(a => a.CreateAsync(It.IsAny<NewContact>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        var creator = new ContactQuickCreate(api.Object, Mock.Of<IReferenceDataCache>(), Snackbar());

        var option = creator.Begin("Kiwi", nameof(ContactType.Organization))!;
        await creator.WhenSettledAsync();

        Assert.Null(creator.Resolve(option.Value));
    }

    /// <summary>
    /// A 201 with no Location header leaves nothing to resolve to. Null is the honest answer — the
    /// contact exists, but this form cannot name it, and guessing would post a fabricated id.
    /// </summary>
    [Fact]
    public async Task A_create_with_no_location_header_resolves_to_null()
    {
        var (creator, _, _) = ContactCreator(_ => ApiResult.Success(HttpStatusCode.Created));

        var option = creator.Begin("Kiwi", nameof(ContactType.Organization))!;
        await creator.WhenSettledAsync();

        Assert.Null(creator.Resolve(option.Value));
    }

    /// <summary>An id this creator never staged passes straight through — it is already a real one.</summary>
    [Fact]
    public void An_id_that_was_never_staged_passes_through_unchanged()
    {
        var (creator, _, _) = ContactCreator(_ => Ok());
        var real = Guid.NewGuid().ToString();

        Assert.Equal(real, creator.Resolve(real));
        Assert.Null(creator.Resolve(null));
        Assert.Null(creator.Resolve(string.Empty));
    }

    [Fact]
    public void A_blank_name_stages_nothing()
    {
        var (creator, api, _) = ContactCreator(_ => Ok());

        Assert.Null(creator.Begin("   ", nameof(ContactType.Organization)));
        api.Verify(a => a.CreateAsync(It.IsAny<NewContact>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// An over-long name is clamped rather than refused, so the gesture still completes; the server's
    /// own <c>[StringLength]</c> stays the real bound.
    /// </summary>
    [Fact]
    public async Task An_over_long_name_is_truncated_to_the_dto_limit()
    {
        NewContact? sent = null;
        var (creator, _, _) = ContactCreator(c => { sent = c; return Ok(); });

        var option = creator.Begin(new string('x', 400), nameof(ContactType.Organization))!;
        await creator.WhenSettledAsync();

        Assert.Equal(128, option.Label.Length);
        Assert.Equal(128, sent!.OrganizationDetails!.LegalName.Length);
    }

    /// <summary>The picked kind reaches the payload — an Organization row must not file a Person.</summary>
    [Theory]
    [InlineData(nameof(ContactType.Organization), ContactType.Organization)]
    [InlineData(nameof(ContactType.Person), ContactType.Person)]
    public async Task The_picked_kind_decides_the_contact_type(string kind, ContactType expected)
    {
        NewContact? sent = null;
        var (creator, _, _) = ContactCreator(c => { sent = c; return Ok(); });

        var option = creator.Begin("Ada Lovelace", kind)!;
        await creator.WhenSettledAsync();

        Assert.Equal(expected, sent!.Type);
        Assert.Equal(expected == ContactType.Person ? "person" : "corporate_fare", option.Icon);
    }

    /// <summary>An unrecognised kind falls back to an Organization rather than failing the gesture.</summary>
    [Fact]
    public async Task An_unknown_kind_falls_back_to_an_organization()
    {
        NewContact? sent = null;
        var (creator, _, _) = ContactCreator(c => { sent = c; return Ok(); });

        creator.Begin("Kiwi", "Martian");
        await creator.WhenSettledAsync();

        Assert.Equal(ContactType.Organization, sent!.Type);
    }

    /// <summary>Several staged creates all settle on one await, and each resolves independently.</summary>
    [Fact]
    public async Task Several_staged_creates_settle_together()
    {
        var calls = 0;
        var (creator, _, _) = ContactCreator(_ => ++calls == 1 ? Ok() : Fail(HttpStatusCode.BadRequest, "nope"));

        var first = creator.Begin("Kiwi", nameof(ContactType.Organization))!;
        var second = creator.Begin("Rema", nameof(ContactType.Organization))!;
        await creator.WhenSettledAsync();

        Assert.Equal(CreatedId.ToString(), creator.Resolve(first.Value));
        Assert.Null(creator.Resolve(second.Value));
    }

    // ── TagQuickCreate ───────────────────────────────────────────────────────

    private static (TagQuickCreate<ExistingTransactionTag> Creator, Mock<ITagsApiClient<ExistingTransactionTag>> Api)
        TagCreator(Func<TagWrite, ApiResult> respond)
    {
        var api = new Mock<ITagsApiClient<ExistingTransactionTag>>();
        api.Setup(a => a.CreateAsync(It.IsAny<TagWrite>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TagWrite t, CancellationToken _) => respond(t));
        return (new TagQuickCreate<ExistingTransactionTag>(api.Object, Snackbar()), api);
    }

    [Fact]
    public async Task A_staged_tag_resolves_to_the_id_the_server_issued()
    {
        TagWrite? sent = null;
        var (creator, _) = TagCreator(t => { sent = t; return Ok(); });

        var option = creator.Begin("Groceries")!;
        await creator.WhenSettledAsync();

        Assert.Equal(CreatedId.ToString(), creator.Resolve(option.Value));
        Assert.Equal("Groceries", sent!.Name);
        Assert.Null(sent.Description);
        Assert.False(sent.Archived);
    }

    /// <summary>
    /// A conflict is a plain failure here, unlike the contact side: the tag services that DO guard a
    /// duplicate name refuse it outright, and there is nothing this can link to instead.
    /// </summary>
    [Fact]
    public async Task A_conflicting_tag_resolves_to_null_and_reports_its_temporary_id()
    {
        string? failed = null;
        var (creator, _) = TagCreator(_ => Fail(HttpStatusCode.Conflict, "taken"));
        creator.OnCreateFailed = id => failed = id;

        var option = creator.Begin("Groceries")!;
        await creator.WhenSettledAsync();

        Assert.Null(creator.Resolve(option.Value));
        Assert.Equal(option.Value, failed);
    }

    [Fact]
    public async Task An_over_long_tag_name_is_truncated_to_the_dto_limit()
    {
        TagWrite? sent = null;
        var (creator, _) = TagCreator(t => { sent = t; return Ok(); });

        var option = creator.Begin(new string('x', 200))!;
        await creator.WhenSettledAsync();

        Assert.Equal(TagWrite.MaxNameLength, option.Label.Length);
        Assert.Equal(TagWrite.MaxNameLength, sent!.Name.Length);
    }

    [Fact]
    public void A_blank_tag_name_stages_nothing()
    {
        var (creator, api) = TagCreator(_ => Ok());

        Assert.Null(creator.Begin(" "));
        api.Verify(a => a.CreateAsync(It.IsAny<TagWrite>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void An_unstaged_tag_id_passes_through_unchanged()
    {
        var (creator, _) = TagCreator(_ => Ok());
        var real = Guid.NewGuid().ToString();

        Assert.Equal(real, creator.Resolve(real));
    }

    /// <summary>Awaiting with nothing staged is a no-op, not a hang — the common case on any save.</summary>
    [Fact]
    public async Task Settling_with_nothing_staged_completes()
    {
        var (contacts, _, _) = ContactCreator(_ => Ok());
        var (tags, _) = TagCreator(_ => Ok());

        await contacts.WhenSettledAsync();
        await tags.WhenSettledAsync();
    }
}
