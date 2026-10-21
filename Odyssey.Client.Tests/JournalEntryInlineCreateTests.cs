using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A tag or contact minted from the New entry dialog's pickers renders under its name at once. A
/// picker's ValueChanged re-renders only <see cref="JournalEntryFields"/>, so unless the dialog
/// re-renders too the fields keep the option list from before the create and the new chip reads
/// "Unknown".
/// </summary>
public sealed class JournalEntryInlineCreateTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    static JournalEntryInlineCreateTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    public JournalEntryInlineCreateTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IFilesApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IJournalApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        var contacts = new Mock<IContactQuickCreate>();
        contacts.Setup(c => c.Begin(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string name, string? _) => new OdsOption(Guid.NewGuid().ToString(), name));
        ctx.Services.AddSingleton(contacts.Object);

        var tags = new Mock<ITagQuickCreate<ExistingJournalTag>>();
        tags.Setup(t => t.Begin(It.IsAny<string>()))
            .Returns((string name) => new OdsOption(Guid.NewGuid().ToString(), name));
        ctx.Services.AddSingleton(tags.Object);

        ctx.Services.AddSingleton<AuthenticationStateProvider>(new WithPermissions(PermissionClaims.JournalTagsCreate, PermissionClaims.ContactsCreate));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await ctx.DisposeAsync();

    [Theory]
    [InlineData("Tags", "Hiking")]
    [InlineData("Contacts", "Ada Lovelace")]
    public void A_member_created_inline_renders_under_its_name_not_unknown(string field, string name)
    {
        var cut = ctx.Render<DialogHost>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OdsTagMultiSelect>(), p => p.Instance.Label == field));
        var picker = cut.FindComponents<OdsTagMultiSelect>().Single(p => p.Instance.Label == field);

        // The popover renders in MudPopoverProvider, outside the picker's own subtree.
        picker.Find("button.odc-tagms-trigger").Click();
        cut.WaitForElement(".odc-tagms-search input").Input(name);
        cut.Find("button.odc-tagms-create").Click();

        cut.WaitForAssertion(() =>
        {
            var chips = picker.FindAll(".odc-tagms-chip").Select(c => c.TextContent).ToList();
            Assert.Single(chips);
            Assert.Contains(name, chips[0]);
            Assert.DoesNotContain("Unknown", chips[0]);
        });
    }

    private sealed class WithPermissions(params string[] permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(p => new Claim(PermissionClaims.Type, p)), "test"))));
    }

    /// <summary>The dialog beside MudBlazor's providers, which host its modal and its pickers.</summary>
    public sealed class DialogHost : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<CreateJournalEntryDialog>(2);
            builder.AddComponentParameter(3, nameof(CreateJournalEntryDialog.Open), true);
            builder.CloseComponent();
        }
    }
}
