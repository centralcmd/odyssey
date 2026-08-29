using Odyssey.Dtos;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Maps a <see cref="ContactType"/> to its UI metadata for the Contacts
/// RecordTable (leading avatar, type chip). A typed convenience over the single source
/// of truth in <see cref="OdsTypeRegistries.ContactTypes"/> (the design system's
/// CONTACT_TYPES registry) — keep all colours/icons there, not here.
/// </summary>
public static class ContactTypeMeta
{
    /// <summary>Material Icons ligature for the type (falls back to Organization, the last registry entry).</summary>
    public static string Icon(ContactType type) =>
        OdsTypeRegistries.ContactTypeOf(type.ToString()).Icon;
}
