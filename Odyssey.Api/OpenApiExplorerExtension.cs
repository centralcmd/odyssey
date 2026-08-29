using System.Reflection;
using Microsoft.OpenApi;

namespace Odyssey.Api;

public static class OpenApiExplorerExtension
{
    internal const string AntiforgeryScheme = "antiforgery";

    // Must match the AddAntiforgery header name configured in Program.cs.
    internal const string AntiforgeryHeaderName = "X-XSRF-TOKEN";

    // Type names that more than one contract-carrying Odyssey namespace defines — ArchivalStatus
    // (Shared.Dtos.Finance and Shared.Dtos.Journal) and Sex (Shared.Dtos.Application and the
    // Shared.Dtos root) today. Swashbuckle keys schemas on the short type name, so any such collision
    // throws while generating the document. Computed by scanning the referenced Odyssey assemblies
    // rather than a hand-kept list, so a new module or a newly duplicated name can't silently
    // reintroduce the failure. Note this groups on FullName, not assembly, which is why merging the
    // four DTO projects into one did not change what it detects.
    private static readonly HashSet<string> AmbiguousTypeNames = ContractAssemblies()
        .SelectMany(assembly => assembly.GetExportedTypes())
        .GroupBy(type => type.Name, StringComparer.Ordinal)
        .Where(group => group.Select(type => type.FullName).Distinct(StringComparer.Ordinal).Count() > 1)
        .Select(group => group.Key)
        .ToHashSet(StringComparer.Ordinal);

    // The *.Context assemblies are excluded on purpose (issue #392). Most finance enums are defined
    // twice — once in Finance.Dtos as the API contract, once in Finance.Context as the stored column
    // type — but only the Dtos copy is ever meant to reach the OpenAPI surface, so counting the
    // entity copy as a collision bought nineteen schemas a namespace prefix they don't need. An
    // entity type that does reach the surface is the defect, not an id to disambiguate, so
    // IsPersistenceType below rejects it by name instead.
    private static IEnumerable<Assembly> ContractAssemblies()
    {
        var api = typeof(OpenApiExplorerExtension).Assembly;
        return api.GetReferencedAssemblies()
            .Where(name => name.Name?.StartsWith("Odyssey.", StringComparison.Ordinal) == true)
            .Select(Assembly.Load)
            .Prepend(api)
            .Where(assembly => !IsPersistenceAssembly(assembly));
    }

    // Odyssey.Context, Odyssey.Context, Odyssey.Context.
    private static bool IsPersistenceAssembly(Assembly assembly) =>
        assembly.GetName().Name is { } name
        && name.StartsWith("Odyssey.", StringComparison.Ordinal)
        && name.EndsWith(".Context", StringComparison.Ordinal);

    private static bool IsPersistenceType(Type type) => IsPersistenceAssembly(type.Assembly);

    private static string SchemaId(Type type, Func<Type, string> defaultSchemaId)
    {
        if (IsPersistenceType(type))
        {
            throw new InvalidOperationException(
                $"{type.FullName} is a persistence entity type and must not appear in the OpenAPI " +
                "document. Bind the API contract to the matching Odyssey.<Module>.Dtos type and cast " +
                "across the boundary explicitly (issue #392).");
        }

        return AmbiguousTypeNames.Contains(type.Name)
            ? ModulePrefix(type) + defaultSchemaId(type)
            : defaultSchemaId(type);
    }

    public static WebApplicationBuilder AddOpenApiExplorer(this WebApplicationBuilder builder)
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Odyssey API",
                Version = "v1",
            });

            // Finance and Journal both define an ArchivalStatus enum, and the default short-name schema
            // ids collide — which failed the entire /swagger/v1/swagger.json document, not just those
            // two schemas. Qualify only the ambiguous names with their module; everything else keeps
            // the default id, so the UI stays readable and existing ids don't churn.
            var defaultSchemaId = options.SchemaGeneratorOptions.SchemaIdSelector;
            options.CustomSchemaIds(type => SchemaId(type, defaultSchemaId));

            options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT Authorization header using the Bearer scheme."
            });

            // The deployed client authenticates with a cookie, not a bearer token, and every write is
            // additionally gated on the antiforgery header (enforced in Program.cs). Advertising only
            // the bearer scheme left "Try it out" on any POST/PUT/DELETE failing with a bare 400 and no
            // hint why (issue #382).
            options.AddSecurityDefinition(AntiforgeryScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = AntiforgeryHeaderName,
                In = ParameterLocation.Header,
                Description =
                    "Antiforgery token for cookie-authenticated writes. GET /api/antiforgery/token " +
                    "returns the value (and sets the paired cookie); echo it here to exercise any " +
                    "POST/PUT/PATCH/DELETE endpoint."
            });

            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("bearer", document)] = [],
                [new OpenApiSecuritySchemeReference(AntiforgeryScheme, document)] = []
            });
        });

        return builder;
    }

    // The module segment of a DTO namespace: "Odyssey.Dtos.Finance" → "Finance". The project's own
    // root is labelled "Shared" rather than "Dtos" — the label names the type's *role* (it crosses
    // modules), and SharedSex against ApplicationSex reads as the contrast it is, where DtosSex would
    // not. So the disambiguated ids are FinanceArchivalStatus / JournalArchivalStatus and
    // ApplicationSex / SharedSex. A type outside the DTO project keeps the whole namespace tail, which
    // is how every collision was disambiguated before the DTO projects were merged into one.
    //
    // These are list patterns over the split namespace, so a rename of the DTO project does NOT reach
    // them by search-and-replace: they would simply stop matching and fall through to the tail case.
    // OpenApiSchemaIdTests pins the resulting ids, which is what catches that.
    private static string ModulePrefix(Type type)
    {
        var segments = (type.Namespace ?? string.Empty).Split('.');
        return segments switch
        {
            ["Odyssey", "Dtos"] => "Shared",
            ["Odyssey", "Dtos", var module, ..] => module,
            ["Odyssey", .. var tail] => string.Concat(tail),
            _ => string.Empty,
        };
    }
}