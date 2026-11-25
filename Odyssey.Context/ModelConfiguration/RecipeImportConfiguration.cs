using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

/// <summary>
/// The AI recipe import's keys (issue #602 §4). Sources and drafts cascade from the import (reachable only by a
/// future hard delete: the service tombstones). A source's file link is <c>SET NULL</c>, so a Files-store delete
/// never deletes the record; a draft's accepted-recipe link is <c>SET NULL</c> for the same reason.
/// </summary>
internal sealed class RecipeImportSourceConfiguration : IEntityTypeConfiguration<RecipeImportSource>
{
    public void Configure(EntityTypeBuilder<RecipeImportSource> entity)
    {
        // Written once at create and never re-ordered, so a unique ordinal cannot collide.
        entity.HasIndex(source => new { source.RecipeImportId, source.Ordinal }).IsUnique();
        entity.HasIndex(source => source.FileId);

        entity.HasOne(source => source.RecipeImport)
            .WithMany(import => import.Sources)
            .HasForeignKey(source => source.RecipeImportId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<FileMetadata>()
            .WithMany()
            .HasForeignKey(source => source.FileId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class RecipeImportDraftConfiguration : IEntityTypeConfiguration<RecipeImportDraft>
{
    public void Configure(EntityTypeBuilder<RecipeImportDraft> entity)
    {
        entity.HasIndex(draft => new { draft.RecipeImportId, draft.Ordinal }).IsUnique();
        // Serves the hasPendingDraft EXISTS and the draft counts without loading DraftJson.
        entity.HasIndex(draft => new { draft.RecipeImportId, draft.Status });

        entity.HasOne(draft => draft.RecipeImport)
            .WithMany(import => import.Drafts)
            .HasForeignKey(draft => draft.RecipeImportId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(draft => draft.AcceptedRecipeId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
