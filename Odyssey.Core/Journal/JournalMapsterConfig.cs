using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Mapster;
using Odyssey.Context;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal;

public static class JournalMapsterConfig
{
    private static readonly object SyncRoot = new();
    private static bool configured;

    // Runs once when the Odyssey.Core.Journal assembly is loaded — before any service, controller, seeder,
    // or test constructs a type from it — so the module Mapster config is registered a single time per
    // process instead of on every service-constructor call.
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

            // Mass-assignment / nav-leak guard (§6/§9): the inbound (DTO → entity) maps ignore every
            // navigation collection, so a same-named nested property in a request body can never
            // round-trip into a related or cross-context entity. Link rows are built explicitly in
            // the services from the scalar id arrays.
            TypeAdapterConfig<NewJournalEntry, JournalEntry>
                .NewConfig()
                .Ignore(dest => dest.EntryTags)
                .Ignore(dest => dest.Contacts)
                .Ignore(dest => dest.Photos)
                .Ignore(dest => dest.Attachments);

            TypeAdapterConfig<UpdateJournalEntry, JournalEntry>
                .NewConfig()
                .Ignore(dest => dest.EntryTags)
                .Ignore(dest => dest.Contacts)
                .Ignore(dest => dest.Photos)
                .Ignore(dest => dest.Attachments);

            TypeAdapterConfig<NewJournalTask, JournalTask>
                .NewConfig()
                .Ignore(dest => dest.ItemTags)
                .Ignore(dest => dest.Attachments);

            TypeAdapterConfig<UpdateJournalTask, JournalTask>
                .NewConfig()
                .Ignore(dest => dest.ItemTags)
                .Ignore(dest => dest.Attachments);

            configured = true;
        }
    }
}
