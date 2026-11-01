using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The flat Files page's Edit-file dialog (design system · FilesTable → FTEditModal): an emptied
/// name is refused on the field itself and nothing is handed to the host.
/// </summary>
public class FilesMetaEditDialogTests
{
    private static readonly OdsFilesRow File = new()
    {
        Id = "f-1",
        Name = "statement-2026-04.pdf",
        Kind = "Statement",
        SizeBytes = 25_800,
        UploadedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task An_empty_name_is_refused_on_the_field_and_never_saved()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var saves = 0;
        var host = ctx.Render<DialogHost>(p => p.Add(h => h.OnSave, _ => { saves++; return Task.FromResult(true); }));

        var name = host.FindComponents<OdsField>().Single(f => f.Instance.Label == "File name");
        Assert.Null(name.Instance.Error);

        await host.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync("   "));
        await host.InvokeAsync(() => host.FindAll("button").First(b => b.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click());

        host.WaitForAssertion(() => Assert.Equal("File name is required.", name.Instance.Error));
        Assert.Contains("File name is required.", host.Markup);
        Assert.Equal(0, saves);
    }

    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public Func<object?, Task<bool>>? OnSave { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<FilesMetaEditDialog>(2);
            builder.AddComponentParameter(3, nameof(FilesMetaEditDialog.File), File);
            builder.AddComponentParameter(4, nameof(FilesMetaEditDialog.Open), true);
            builder.AddComponentParameter(5, nameof(FilesMetaEditDialog.OnSave), OnSave);
            builder.CloseComponent();
        }
    }
}
