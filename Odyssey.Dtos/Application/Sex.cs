namespace Odyssey.Dtos.Application;

/// <summary>
/// A user's sex (issue #316, §6). A deliberately separate identity-side enum whose ordinals are aligned
/// with the pre-existing <c>Odyssey.Dtos.Sex</c> (<c>Male = 1, Female = 2</c>) so the two never
/// conflate when both persist as <c>int</c>. Starting at <c>1</c> follows the repo convention that a
/// defaulted/zero <c>int</c> is never a valid value. Collected under a stated purpose (planned
/// retirement / long-term financial planning) and visible only to the owning user.
/// </summary>
public enum Sex
{
    Male = 1,
    Female = 2,
}
