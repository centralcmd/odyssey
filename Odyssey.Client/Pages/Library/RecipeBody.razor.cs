using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>A check-off toggled on an ingredient or step line, by its position in the read.</summary>
public sealed record RecipeCheck(int Index, bool Checked);

public partial class RecipeBody
{
    /// <summary>What a nulled attribution reads (the author was deleted).</summary>
    internal const string RemovedUser = "A removed user";

    /// <summary>The card's read — scaled when the cook asked for other servings.</summary>
    [Parameter, EditorRequired] public ExistingRecipe Recipe { get; set; } = default!;

    [Parameter, EditorRequired] public RecipeCookState Cook { get; set; } = default!;

    /// <summary>A unique prefix for this card's element ids.</summary>
    [Parameter, EditorRequired] public string IdPrefix { get; set; } = "rc";

    [Parameter] public bool CanPhotos { get; set; }

    [Parameter] public bool CanFiles { get; set; }

    [Parameter] public bool CanUpdate { get; set; }

    [Parameter] public PhotoImageAccess PhotoAccess { get; set; } = new(false);

    [Parameter] public EventCallback<RecipeCheck> OnToggleIngredient { get; set; }

    [Parameter] public EventCallback<RecipeCheck> OnToggleStep { get; set; }

    [Parameter] public EventCallback OnRetryScale { get; set; }

    [Parameter] public EventCallback<RecipePhotoChange> OnPhotoChange { get; set; }

    [Parameter] public EventCallback<RecipeAttachmentDto> OnDetachFile { get; set; }

    [Parameter] public EventCallback<RecipeAttachmentDto> OnDownloadFile { get; set; }

    [Parameter] public EventCallback<RecipeAttachmentDto> OnCopyFileId { get; set; }

    private bool HasLabel => !string.IsNullOrWhiteSpace(Recipe.ServingsLabel);

    private static readonly OdsTableSort NameSort = new("name", OdsSortDirection.Asc);

    private static RenderFragment Text(string text) => builder => builder.AddContent(0, text);

    private static IReadOnlyList<OdsFilesRow> FileRows(IReadOnlyList<RecipeAttachmentDto> files) =>
    [
        .. files.OrderBy(f => f.FileName, StringComparer.CurrentCultureIgnoreCase).Select(f => new OdsFilesRow
        {
            Id = f.FileId.ToString(),
            Name = f.FileName,
            Kind = RecipeFormat.FileKind(f.ContentType).Label,
            SizeBytes = f.SizeBytes,
            UploadedAtUtc = f.CreatedAt,
        }),
    ];

    private static OdsFileKindMeta KindMeta(OdsFilesRow row)
    {
        var icon = row.Kind switch
        {
            "PDF" => "picture_as_pdf",
            "Image" => "image",
            "Word document" => "description",
            "Text" => "notes",
            _ => "draft",
        };
        var color = row.Kind == "PDF" ? "var(--finance-expense)" : "var(--mud-palette-text-secondary)";
        return new OdsFileKindMeta(icon, color, $"color-mix(in srgb, {color} 16%, transparent)");
    }

    private IReadOnlyList<OdsMenuItem> FileActions(IReadOnlyList<RecipeAttachmentDto> files, OdsFilesRow row)
    {
        var file = files.First(f => f.FileId.ToString() == row.Id);
        return
        [
            new OdsMenuItem
            {
                Icon = "download",
                Label = "Download",
                AriaLabel = $"Download {file.FileName}",
                OnClick = EventCallback.Factory.Create(this, () => OnDownloadFile.InvokeAsync(file)),
            },
            OdsMenuItem.CopyId(EventCallback.Factory.Create(this, () => OnCopyFileId.InvokeAsync(file)), $"Copy ID of {file.FileName}"),
        ];
    }

    private EventCallback<OdsFilesRow> DetachAction(IReadOnlyList<RecipeAttachmentDto> files) => CanUpdate
        ? EventCallback.Factory.Create<OdsFilesRow>(this, row =>
            files.FirstOrDefault(f => f.FileId.ToString() == row.Id) is { } file ? OnDetachFile.InvokeAsync(file) : Task.CompletedTask)
        : default;
}
