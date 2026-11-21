using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

/// <summary>
/// The Recipes module's keys (issue #525 §4). Children cascade from the recipe; a photo link also cascades from
/// the library photo (the <see cref="JournalEntryPhotoConfiguration"/> shape); a tag link restricts the tag.
/// </summary>
internal sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> entity)
    {
        entity.Property(recipe => recipe.Version).IsConcurrencyToken();
    }
}

internal sealed class RecipeIngredientConfiguration : IEntityTypeConfiguration<RecipeIngredient>
{
    public void Configure(EntityTypeBuilder<RecipeIngredient> entity)
    {
        // Not unique on purpose: a PUT replaces the rows in one save, and a unique position would collide.
        entity.HasIndex(ingredient => new { ingredient.RecipeId, ingredient.Position });

        entity.HasOne(ingredient => ingredient.Recipe)
            .WithMany(recipe => recipe.Ingredients)
            .HasForeignKey(ingredient => ingredient.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RecipeStepConfiguration : IEntityTypeConfiguration<RecipeStep>
{
    public void Configure(EntityTypeBuilder<RecipeStep> entity)
    {
        entity.HasIndex(step => new { step.RecipeId, step.Position });

        entity.HasOne(step => step.Recipe)
            .WithMany(recipe => recipe.Steps)
            .HasForeignKey(step => step.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RecipePhotoConfiguration : IEntityTypeConfiguration<RecipePhoto>
{
    public void Configure(EntityTypeBuilder<RecipePhoto> entity)
    {
        // Safe as unique because the service diffs links in place rather than deleting and re-inserting a pair.
        entity.HasIndex(link => new { link.RecipeId, link.PhotoId }).IsUnique();
        entity.HasIndex(link => new { link.RecipeId, link.Position });

        entity.HasOne(link => link.Recipe)
            .WithMany(recipe => recipe.Photos)
            .HasForeignKey(link => link.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Photo>()
            .WithMany()
            .HasForeignKey(link => link.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RecipeTagLinkConfiguration : IEntityTypeConfiguration<RecipeTagLink>
{
    public void Configure(EntityTypeBuilder<RecipeTagLink> entity)
    {
        entity.HasIndex(link => new { link.RecipeId, link.RecipeTagId }).IsUnique();

        entity.HasOne(link => link.Recipe)
            .WithMany(recipe => recipe.Tags)
            .HasForeignKey(link => link.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict so an in-use tag cannot be hard-deleted out from under a recipe; the service answers 409 first.
        entity.HasOne(link => link.RecipeTag)
            .WithMany(tag => tag.Recipes)
            .HasForeignKey(link => link.RecipeTagId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
