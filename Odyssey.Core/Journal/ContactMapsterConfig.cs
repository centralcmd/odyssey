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
            // for MudDatePicker (frontend finding CDS-5). DateOfDeath (issue #48) is the same
            // exchange, deliberately — do NOT "fix" either to DateOnly?.
            TypeAdapterConfig<PersonDetails, PersonDetailsDto>
                .NewConfig()
                .Map(dest => dest.DateOfBirth,
                    src => src.DateOfBirth == null ? (DateTime?)null : src.DateOfBirth.Value.ToDateTime(TimeOnly.MinValue))
                .Map(dest => dest.DateOfDeath,
                    src => src.DateOfDeath == null ? (DateTime?)null : src.DateOfDeath.Value.ToDateTime(TimeOnly.MinValue));

            TypeAdapterConfig<OrganizationDetails, OrganizationDetailsDto>
                .NewConfig()
                .Map(dest => dest.EstablishedDate,
                    src => src.EstablishedDate == null ? (DateTime?)null : src.EstablishedDate.Value.ToDateTime(TimeOnly.MinValue))
                .Map(dest => dest.DissolvedDate,
                    src => src.DissolvedDate == null ? (DateTime?)null : src.DissolvedDate.Value.ToDateTime(TimeOnly.MinValue));

            // A contact address is a link row since issue #380; today's address shape is projected from the
            // linked location, which every read loads (ThenInclude). B2: adds LocationId, otherUses and
            // placeHasCoordinates to ExistingAddress.
            TypeAdapterConfig<Address, ExistingAddress>
                .NewConfig()
                .Map(dest => dest.Line1, src => src.Location != null ? src.Location.Line1 ?? string.Empty : string.Empty)
                .Map(dest => dest.Line2, src => src.Location != null ? src.Location.Line2 : null)
                .Map(dest => dest.City, src => src.Location != null ? src.Location.City ?? string.Empty : string.Empty)
                .Map(dest => dest.PostalCode, src => src.Location != null ? src.Location.PostalCode : null)
                .Map(dest => dest.Region, src => src.Location != null ? src.Location.Region : null)
                .Map(dest => dest.CountryCode, src => src.Location != null ? src.Location.CountryCode ?? string.Empty : string.Empty);

            configured = true;
        }
    }
}
