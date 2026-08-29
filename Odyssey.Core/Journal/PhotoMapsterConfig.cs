using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Mapster;
using Odyssey.Context;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal;

public static class PhotoMapsterConfig
{
    private static readonly object SyncRoot = new();
    private static bool configured;

    // Runs once when the Odyssey.Core.Journal assembly is loaded — before any service, controller, seeder, or
    // test constructs a type from it — so the module Mapster config is registered a single time per
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
            // navigation collection and cross-context nav, so a same-named nested property in a request
            // body can never round-trip into a related or cross-context entity. Link rows are built
            // explicitly in the services from the scalar id arrays.
            TypeAdapterConfig<NewPhoto, Photo>
                .NewConfig()
                .Ignore(dest => dest.Tags)
                .Ignore(dest => dest.People)
                .Ignore(dest => dest.Albums);

            TypeAdapterConfig<UpdatePhoto, Photo>
                .NewConfig()
                .Ignore(dest => dest.Tags)
                .Ignore(dest => dest.People)
                .Ignore(dest => dest.Albums);

            TypeAdapterConfig<NewPhotoAlbum, PhotoAlbum>
                .NewConfig()
                .Ignore(dest => dest.Items)
                .Ignore(dest => dest.CoverPhoto);

            TypeAdapterConfig<UpdatePhotoAlbum, PhotoAlbum>
                .NewConfig()
                .Ignore(dest => dest.Items)
                .Ignore(dest => dest.CoverPhoto);

            configured = true;
        }
    }
}
