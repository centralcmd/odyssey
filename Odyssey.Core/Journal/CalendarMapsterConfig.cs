using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Mapster;
using ContextRecurrenceFrequency = Odyssey.Context.RecurrenceFrequency;
using ContextDaysOfWeekFlags = Odyssey.Context.DaysOfWeekFlags;
using DtoRecurrenceFrequency = Odyssey.Dtos.Journal.RecurrenceFrequency;
using DtoDaysOfWeekFlags = Odyssey.Dtos.Journal.DaysOfWeekFlags;

namespace Odyssey.Core.Journal;

/// <summary>
/// Mapster does not reliably auto-map between two structurally-identical-but-distinct enum types
/// (confirmed by <c>Odyssey.Core.Finance/MapsterConfig.cs</c>'s equivalent registrations for
/// <c>BillingInterval</c> et al.) — <c>RecurrenceFrequency</c>/<c>DaysOfWeekFlags</c> are each defined
/// once in <c>Odyssey.Context</c> (for the entity) and once in <c>Odyssey.Dtos.Journal</c>
/// (for the DTOs), kept byte-identical by hand, so both conversions are a plain numeric cast.
/// </summary>
public static class CalendarMapsterConfig
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

            TypeAdapterConfig<ContextRecurrenceFrequency, DtoRecurrenceFrequency>
                .NewConfig()
                .MapWith(src => (DtoRecurrenceFrequency)(int)src);

            TypeAdapterConfig<DtoRecurrenceFrequency, ContextRecurrenceFrequency>
                .NewConfig()
                .MapWith(src => (ContextRecurrenceFrequency)(int)src);

            TypeAdapterConfig<ContextDaysOfWeekFlags, DtoDaysOfWeekFlags>
                .NewConfig()
                .MapWith(src => (DtoDaysOfWeekFlags)(int)src);

            TypeAdapterConfig<DtoDaysOfWeekFlags, ContextDaysOfWeekFlags>
                .NewConfig()
                .MapWith(src => (ContextDaysOfWeekFlags)(int)src);

            configured = true;
        }
    }
}
