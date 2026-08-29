using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Mapster;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;

namespace Odyssey.Core.Journal;

/// <summary>
/// Mapster config for the Contact aggregate (issue #325), moved here with the entity from Finance.
/// The Contact entities live in <see cref="OdysseyContext"/>; their read DTOs live alongside them in
/// <c>Odyssey.Dtos.Journal</c>.
/// </summary>
public static class ContactMapsterConfig
{
    private static readonly object SyncRoot = new();
    private static bool configured;

    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Deliberate: registering the Mapster config once on assembly load is the point — "
            + "it is what keeps every service constructor from re-registering it.")]
    [ModuleInitializer]
    internal static void Initialize() => Register();

    public static void Register()
    {
        if (configured)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (configured)
            {
                return;
            }

            // Contact read projection (issue #325, architect finding #5): the discriminated detail
            // sub-objects are mapped conditionally on Type (the non-matching one is forced null even if a
            // stray navigation were loaded), and ResolvedDisplayName is a computed value with no matching
            // source property so it needs an explicit resolver rather than convention member-matching.
            TypeAdapterConfig<Contact, ExistingContact>
                .NewConfig()
                .Map(dest => dest.ResolvedDisplayName, src => ContactNaming.Resolve(src))
                .Map(dest => dest.PersonDetails,
                    src => src.Type == ContactType.Person ? src.PersonDetails : null)
                .Map(dest => dest.OrganizationDetails,
                    src => src.Type == ContactType.Organization ? src.OrganizationDetails : null);

            // DateOfBirth is DateOnly? server-side but surfaces as DateTime? (midnight, time ignored)
            // for MudDatePicker (frontend finding CDS-5).
            TypeAdapterConfig<PersonDetails, PersonDetailsDto>
                .NewConfig()
                .Map(dest => dest.DateOfBirth,
                    src => src.DateOfBirth == null ? (DateTime?)null : src.DateOfBirth.Value.ToDateTime(TimeOnly.MinValue));

            configured = true;
        }
    }
}
