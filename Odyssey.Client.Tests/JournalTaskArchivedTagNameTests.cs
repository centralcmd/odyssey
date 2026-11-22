using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
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
/// A journal or task tag name is unique archived tags included (JournalTagService and
/// JournalTaskTagService refuse it with a 409), and an archived tag is not among the picker's options. So
/// the entry and task dialogs pass their archived names to the Tags picker, which refuses the name with
/// the design system's sentence instead of offering a create row the server would reject.
/// </summary>
public sealed class JournalTaskArchivedTagNameTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    static JournalTaskArchivedTagNameTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    public JournalTaskArchivedTagNameTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IFilesApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IPhotosApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IJournalApiClient>());
        ctx.Services.AddSingleton(Mock.Of<ITaskApiClient>());
        ctx.Services.AddSingleton(Mock.Of<ILocationsApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        ctx.Services.AddSingleton(Mock.Of<ITagQuickCreate<ExistingJournalTag>>());
        ctx.Services.AddSingleton(Mock.Of<ITagQuickCreate<ExistingJournalTaskTag>>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new WithPermissions(
            PermissionClaims.JournalTagsCreate, PermissionClaims.TaskTagsCreate));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await ctx.DisposeAsync();

    [Theory]
    [InlineData(typeof(CreateJournalEntryDialog), "Journal tags")]
    [InlineData(typeof(CreateTaskDialog), "Task tags")]
    public void An_archived_tag_name_is_refused_in_the_tags_picker(Type dialog, string page)
    {
        var cut = ctx.Render<DialogHost>(p => p.Add(h => h.Dialog, dialog));
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OdsTagMultiSelect>(), p => p.Instance.Label == "Tags"));
        var picker = cut.FindComponents<OdsTagMultiSelect>().Single(p => p.Instance.Label == "Tags");

        picker.Find("button.odc-tagms-trigger").Click();
        cut.WaitForElement(".odc-tagms-search input").Input("old garden");

        cut.WaitForAssertion(() =>
            Assert.Equal($"“Old garden” is an archived tag. Restore it on the {page} page to use it.",
                cut.Find(".odc-tagms-blocked").TextContent.Trim()));
        Assert.Empty(cut.FindAll("button.odc-tagms-create"));
    }

    private sealed class WithPermissions(params string[] permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(p => new Claim(PermissionClaims.Type, p)), "test"))));
    }

    /// <summary>The dialog beside MudBlazor's providers, with one active and one archived tag.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public Type Dialog { get; set; } = typeof(CreateJournalEntryDialog);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent(2, Dialog);
            builder.AddComponentParameter(3, "Open", true);
            builder.AddComponentParameter(4, "TagOptions", (IReadOnlyList<OdsOption>)[new OdsOption(Guid.NewGuid().ToString(), "Garden")]);
            builder.AddComponentParameter(5, "ArchivedTagNames", (IReadOnlyCollection<string>)["Old garden"]);
            builder.CloseComponent();
        }
    }
}
