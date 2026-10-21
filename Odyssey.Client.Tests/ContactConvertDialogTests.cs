using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Convert dialog (issue #335, Odyssey Design System · ContactConvertModal): it renders the
/// server's preview, echoes its <c>UpdatedAt</c> verbatim, sends a name only when it was edited, and
/// turns the two <c>409</c>s into the refusal and the stale state rather than a toast.
/// </summary>
public class ContactConvertDialogTests : IAsyncLifetime
{
    private static readonly DateTime UpdatedAt = new(2026, 10, 21, 9, 12, 44, 123, DateTimeKind.Utc);

    private readonly BunitContext ctx = new();
    private readonly Mock<IContactsApiClient> contacts = new();
    private readonly Mock<IPhotosApiClient> photos = new();

    public ContactConvertDialogTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(contacts.Object);
        ctx.Services.AddSingleton(photos.Object);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    [Fact]
    public void APerson_PreviewsTheOrganizationItWouldBecome()
    {
        SetupPreview(PersonToOrgPreview());

        var dialog = Render(Person());

        var markup = dialog.Markup;
        Assert.Contains("Convert to organization", markup);
        Assert.Contains("Title", markup);
        Assert.Contains("Date of birth", markup);
        Assert.Contains("Established date", markup);
        Assert.Contains("1 contact-method label", markup);
        Assert.Equal("Ada Lovelace", dialog.Find("input").GetAttribute("value"));
    }

    [Fact]
    public async Task Submitting_EchoesUpdatedAt_AndLeavesAnUntouchedNameDerived()
    {
        SetupPreview(PersonToOrgPreview());
        ContactConversionRequest? sent = null;
        contacts.Setup(c => c.ConvertAsync(It.IsAny<Guid>(), It.IsAny<ContactConversionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, ContactConversionRequest, CancellationToken>((_, request, _) => sent = request)
            .ReturnsAsync(ApiResult<ExistingContact>.Success(Organization(), HttpStatusCode.OK));
        ExistingContact? converted = null;

        var dialog = Render(Person(), c => converted = c);
        await dialog.InvokeAsync(() => ClickSubmit(dialog));

        dialog.WaitForAssertion(() => Assert.NotNull(converted));
        Assert.NotNull(sent);
        Assert.Equal(UpdatedAt, sent!.ExpectedUpdatedAt);
        Assert.Equal(ContactType.Organization, sent.TargetType);
        Assert.Null(sent.LegalName);
        Assert.True(sent.CarryLifecycleDates);
        Assert.NotNull(converted);
    }

    [Fact]
    public void ABlockedPreview_ShowsTheRefusal_WithTheCountAndNoConvertAction()
    {
        var preview = PersonToOrgPreview();
        preview.CanConvert = false;
        preview.Blockers.PhotoPersonTags = new ContactPhotoTagBlocker { Count = 3 };
        SetupPreview(preview);

        var dialog = Render(Person());

        var markup = dialog.Markup;
        Assert.Contains("Unable to convert to organization", markup);
        Assert.Contains("Person tag on a photo", markup);
        Assert.Contains("is not shown", markup);
        Assert.DoesNotContain(dialog.FindAll("button"), b => b.TextContent.Contains("Convert to organization"));
    }

    [Fact]
    public async Task AStale409_ShowsTheStaleAlert_AndOnlyClose()
    {
        SetupPreview(PersonToOrgPreview());
        contacts.Setup(c => c.ConvertAsync(It.IsAny<Guid>(), It.IsAny<ContactConversionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingContact>.Failure(HttpStatusCode.Conflict,
                new ApiProblem { Status = 409, Code = ContactConversionCodes.Stale, Detail = "changed" }));

        var dialog = Render(Person());
        await dialog.InvokeAsync(() => ClickSubmit(dialog));

        dialog.WaitForAssertion(() => Assert.Contains("This contact changed since you opened this dialog", dialog.Markup));
        Assert.DoesNotContain(dialog.FindAll("button"), b => b.TextContent.Contains("Convert to organization"));
    }

    [Fact]
    public async Task ABlocked409OnSubmit_SwitchesToTheRefusal()
    {
        SetupPreview(PersonToOrgPreview());
        var blocker = JsonSerializer.SerializeToElement(new { count = 2, photoIds = Array.Empty<Guid>(), truncated = false });
        contacts.Setup(c => c.ConvertAsync(It.IsAny<Guid>(), It.IsAny<ContactConversionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingContact>.Failure(HttpStatusCode.Conflict, new ApiProblem
            {
                Status = 409,
                Code = ContactConversionCodes.Blocked,
                Extensions = new Dictionary<string, JsonElement> { ["photoPersonTags"] = blocker },
            }));

        var dialog = Render(Person());
        await dialog.InvokeAsync(() => ClickSubmit(dialog));

        dialog.WaitForAssertion(() => Assert.Contains("Unable to convert to organization", dialog.Markup));
    }

    [Fact]
    public void AOneWordLegalName_AsksForALastName_WithoutAnErrorBeforeSubmit()
    {
        SetupPreview(new ContactConversionPreview
        {
            CurrentType = ContactType.Organization,
            TargetType = ContactType.Person,
            UpdatedAt = UpdatedAt,
            PersonDetails = new PersonDetailsDto { FirstName = "Equinor", LastName = "" },
            DerivationIssues = [new ContactConversionIssue { Field = ContactConversionFields.LastName, Message = "single word" }],
        }, ContactType.Person);

        var dialog = Render(Organization());

        Assert.Contains("The legal name is one word, so enter a last name.", dialog.Markup);
        Assert.DoesNotContain("aria-invalid=\"true\"", dialog.Markup);
    }

    /// <summary>
    /// The edit dialog no longer claims the type is fixed, and the row menu carries the one conversion
    /// that exists for the contact — the conversion lives in the menu, never in the edit dialog.
    /// </summary>
    [Fact]
    public void TheEditDialogPointsAtTheMenu_AndTheMenuOffersTheConversion()
    {
        var dialog = File.ReadAllText(Path.Combine(ClientRoot(), "Pages", "Finance", "ContactDialog.razor"));
        var tiles = File.ReadAllText(Path.Combine(ClientRoot(), "Pages", "Finance", "ContactInfoTiles.razor"));
        var card = File.ReadAllText(Path.Combine(ClientRoot(), "Pages", "Finance", "ContactsCard.razor.cs"));

        Assert.DoesNotContain("can't be changed after creation", dialog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixed after creation", tiles, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ContactConvertDialog", dialog);
        Assert.Contains("\"Convert to organization\"", card);
        Assert.Contains("\"Convert to person\"", card);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The dialog is an inline MudDialog, which renders into the provider — so the PROVIDER's markup is
    /// what the user sees. Waits for the preview load before handing it back.
    /// </summary>
    private IRenderedComponent<MudDialogProvider> Render(ExistingContact contact, Action<ExistingContact>? onConverted = null)
    {
        var provider = ctx.Render<MudDialogProvider>();
        ctx.Render<ContactConvertDialog>(parameters => parameters
            .Add(p => p.Open, true)
            .Add(p => p.Contact, contact)
            .Add(p => p.OnConverted, (ExistingContact c) => onConverted?.Invoke(c)));
        provider.WaitForAssertion(() => Assert.Contains("ods-modal", provider.Markup));
        provider.WaitForAssertion(() => Assert.DoesNotContain("Loading the conversion preview", provider.Markup));
        return provider;
    }

    private static void ClickSubmit(IRenderedComponent<MudDialogProvider> dialog) =>
        dialog.FindAll("button").Last(b => b.TextContent.Contains("Convert to")).Click();

    private void SetupPreview(ContactConversionPreview preview, ContactType target = ContactType.Organization) =>
        contacts.Setup(c => c.PreviewConversionAsync(It.IsAny<Guid>(), target, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ContactConversionPreview>.Success(preview, HttpStatusCode.OK));

    private static ContactConversionPreview PersonToOrgPreview() => new()
    {
        CurrentType = ContactType.Person,
        TargetType = ContactType.Organization,
        UpdatedAt = UpdatedAt,
        CanConvert = true,
        OrganizationDetails = new OrganizationDetailsDto { LegalName = "Ada Lovelace", EstablishedDate = new DateTime(1815, 12, 10) },
        LostFields = [ContactConversionFields.Title],
        ConvertedFields =
        [
            new ContactConvertedField { From = ContactConversionFields.FirstName, To = ContactConversionFields.LegalName },
            new ContactConvertedField { From = ContactConversionFields.DateOfBirth, To = ContactConversionFields.EstablishedDate },
        ],
        RemappedLabels = new ContactRemappedLabels { Emails = 1 },
    };

    private static ExistingContact Person() => new()
    {
        ContactId = Guid.NewGuid(),
        ExternalUid = "urn:uuid:ada",
        NormalizedName = "ADA LOVELACE",
        ResolvedDisplayName = "Ada Lovelace",
        Type = ContactType.Person,
        PersonDetails = new PersonDetailsDto { FirstName = "Ada", LastName = "Lovelace" },
    };

    private static ExistingContact Organization() => new()
    {
        ContactId = Guid.NewGuid(),
        ExternalUid = "urn:uuid:equinor",
        NormalizedName = "EQUINOR",
        ResolvedDisplayName = "Equinor",
        Type = ContactType.Organization,
        OrganizationDetails = new OrganizationDetailsDto { LegalName = "Equinor" },
    };

    private static string ClientRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Odyssey.Client")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "Odyssey.Client");
    }
}
