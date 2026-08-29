namespace Odyssey.Dtos.Application;

/// <summary>
/// The effective upload cap, for any authenticated caller — no permission claim, so every upload
/// dialog can pre-validate against the real limit regardless of which claims the signed-in user holds
/// (issue #421 Wave 4).
///
/// <para>
/// A sibling of <see cref="ImportLimitsDto"/> rather than another field on it: that DTO is pinned to
/// exactly sixteen properties by a test asserting the import contract, and the upload cap is a
/// different surface with a different consumer set.
/// </para>
/// </summary>
public sealed record UploadLimitsDto
{
    /// <summary>The cap on file content, in bytes — what a dialog compares a selected file against.</summary>
    public long MaxUploadBytes { get; set; }

    /// <summary>The same cap in megabytes, so a message can name a round number rather than derive one.</summary>
    public int MaxUploadMegabytes { get; set; }
}
