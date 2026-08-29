namespace Odyssey.Dtos.Finance;

/// <summary>
/// List-filter kind for files, derived at query time from the stored MIME content type
/// (<c>application/pdf</c> → <see cref="Pdf"/>, <c>image/*</c> → <see cref="Image"/>, everything else
/// → <see cref="File"/>).
/// </summary>
public enum FileKind
{
    Pdf,
    Image,
    File,
}
