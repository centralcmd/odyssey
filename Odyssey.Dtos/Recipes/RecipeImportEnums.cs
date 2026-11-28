namespace Odyssey.Dtos.Recipes;

/// <summary>An AI recipe import's lifecycle (issue #602 §4). Ordinals are a wire contract.</summary>
public enum RecipeImportStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>What one source of an import is. Ordinals are a wire contract.</summary>
public enum RecipeImportSourceKind
{
    Text = 0,
    File = 1,
}

/// <summary>A draft recipe's state on its import. Ordinals are a wire contract.</summary>
public enum RecipeImportDraftStatus
{
    Pending = 0,
    Accepted = 1,
    Discarded = 2,
}
