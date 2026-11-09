namespace Odyssey.Dtos;

/// <summary>
/// Where an uploaded file is placed on disk (issue #448 §5.1): the root folder of one storage area. Chosen by the surface
/// that uploads, never by the user, and fixed for the file's life. <b>Placement only</b> — no read, link or claim ever
/// depends on it.
/// <para>
/// There is deliberately <b>no contacts member</b>: the <c>contact-avatars/</c> area is reachable only through the
/// avatar endpoint, so the generic upload cannot put a document there. <c>0</c> is not a member either, so an omitted or
/// default value fails <c>[EnumDataType]</c>. Lives in the module-neutral root because both modules upload.
/// </para>
/// </summary>
public enum FileUploadArea
{
    Finance = 1,
    Journal = 2,
}
