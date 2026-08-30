using Odyssey.AppHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var builder = DistributedApplication.CreateBuilder(args);

// Aspire (unlike docker compose) does not read the repo .env file. Load it and map the email /
// registration keys onto the Aspire:* configuration the AppHost forwards to the API, so running
// `dotnet run --project Odyssey.AppHost` picks up the same SMTP config as `docker compose`.
LoadDotEnv(builder);

var config = builder.Configuration;

var mariadbVersion = GetRequiredValue("Aspire:MariaDb:Version");
var mariadbRootPassword = GetRequiredValue("Aspire:MariaDb:RootPassword");
var mariadbUser = GetRequiredValue("Aspire:MariaDb:User");
var mariadbPassword = GetRequiredValue("Aspire:MariaDb:Password");
var mariadbDatabase = GetRequiredValue("Aspire:MariaDb:Database");
var mariadbDataVolume = GetRequiredValue("Aspire:MariaDb:DataVolumeName");
var mariadbInitScriptsPath = GetRequiredValue("Aspire:MariaDb:InitScriptsPath");
var mariadbHostPort = GetRequiredInt("Aspire:MariaDb:HostPort");
var mariadbContainerPort = GetRequiredInt("Aspire:MariaDb:ContainerPort");
var mariadbServer = GetRequiredValue("Aspire:MariaDb:Server");

var apiEnvironment = GetRequiredValue("Aspire:Api:Environment");
var apiSwaggerEnabled = GetRequiredBool("Aspire:Api:SwaggerEnabled").ToString().ToLowerInvariant();
// One database for the whole application: identity, finance and journal are a single context with
// real foreign keys between them, so they cannot be pointed at separate schemas.
var odysseyDatabase = GetRequiredValue("Aspire:Api:Databases:Odyssey");

var clientUrls = GetRequiredValue("Aspire:Client:Urls");

// Demo-data seeding for the local Aspire stack. Defaults on; override via Aspire:Seed:DemoData.
var seedDemoData = (config.GetValue<bool?>("Aspire:Seed:DemoData") ?? true).ToString().ToLowerInvariant();

builder.Services.AddHealthChecks()
    .AddCheck("mariadb-ready", new MariaDbHealthCheck(
        $"server={mariadbServer};port={mariadbHostPort};user={mariadbUser};password={mariadbPassword};Connection Timeout=5;"));

var mariadb = builder
    .AddContainer("mariadb", "mariadb", mariadbVersion)
    .WithEnvironment("MARIADB_ROOT_PASSWORD", mariadbRootPassword)
    .WithEnvironment("MARIADB_DATABASE", mariadbDatabase)
    .WithEnvironment("MARIADB_USER", mariadbUser)
    .WithEnvironment("MARIADB_PASSWORD", mariadbPassword)
    .WithVolume(mariadbDataVolume, "/var/lib/mysql")
    .WithBindMount(mariadbInitScriptsPath, "/docker-entrypoint-initdb.d", isReadOnly: true)
    .WithEndpoint(targetPort: mariadbContainerPort, port: mariadbHostPort, name: "tcp", isProxied: false)
    .WithHealthCheck("mariadb-ready");

// The Data Protection key ring for the local Aspire stack (issue #444). Aspire had none at all
// before this, so a secret written through `dotnet run --project Odyssey.AppHost` would have been
// refused by the write-path durability check with nowhere to point the operator.
//
// The API alone gets it. The migrations job had it too, for a config-adoption step that no longer
// exists; it protects nothing now, and a second holder of the ring is a cost with no return. If that
// changes, give it this same directory — a job protecting a row under its own ephemeral ring writes
// one the API can never decrypt.
var dataProtectionKeysPath = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", ".aspire", "dataprotection-keys"));
Directory.CreateDirectory(dataProtectionKeysPath);

var migrations = builder
    .AddProject<Projects.Odyssey_MigrationService>("migrations")
    .WithEnvironment("ConnectionStrings__OdysseyConnection", BuildConnectionString(odysseyDatabase))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", apiEnvironment)
    // The migrations host is Host.CreateApplicationBuilder, which reads DOTNET_ENVIRONMENT and
    // ignores the ASPNETCORE name — without this it runs as Production and never seeds.
    .WithEnvironment("DOTNET_ENVIRONMENT", apiEnvironment)
    .WithEnvironment("Seed__DemoData", seedDemoData)
    .WaitFor(mariadb);

var api = builder
    .AddProject<Projects.Odyssey_Api>("api")
    .WithHttpEndpoint(port: 5188, name: "api-http")
    .WithEnvironment("DataProtection__KeysPath", dataProtectionKeysPath)
    .WithEnvironment("ConnectionStrings__OdysseyConnection", BuildConnectionString(odysseyDatabase))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", apiEnvironment)
    .WithEnvironment("Swagger__Enabled", apiSwaggerEnabled)
    // Confirmation links must point at the client.
    .WithEnvironment("Email__ClientBaseUrl", clientUrls)
    .WithEnvironment("SSL_CERT_FILE", "/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem")
    .WaitFor(mariadb)
    .WithReference(migrations)
    .WaitForCompletion(migrations);

// These settings are optional and come from AppHost user-secrets under Aspire:*. Only forward keys
// that are actually set — emitting an empty value would override the API's appsettings defaults,
// and an empty value for a non-nullable target (e.g. Email__SmtpPort) fails to bind.
ForwardOptionalSetting(api, "Email__SmtpHost", "Aspire:Email:SmtpHost");
ForwardOptionalSetting(api, "Email__SmtpPort", "Aspire:Email:SmtpPort");
ForwardOptionalSetting(api, "Email__UseStartTls", "Aspire:Email:UseStartTls");
// Email__Username / Email__Password are NOT forwarded (issue #445 Wave 2). The relay credential moved
// to the encrypted secret store and is entered once at /settings → Credentials. It is deliberately not
// adopted from configuration either — adoption would require the plaintext to still be in the
// environment at upgrade time, which is most of what the move exists to escape — so there is no
// resource to forward it to.
// The upload transport ceiling (issue #421 Wave 4). The API needs it at startup to size Kestrel and
// the multipart reader; nothing else reads it. The cap a user is validated against is a setting, is
// bounded by this number, and is edited in the UI.
ForwardOptionalSetting(api, "FileStorage__MaxFileSizeBytes", "Aspire:FileStorage:MaxFileSizeBytes");

// Nothing is forwarded to MIGRATIONS. The sender identity (issue #421), the file-analysis tuning
// values (#434) and the switch, model and destination (#439) are all settings edited in the UI, and
// the migrations job's only interest in them was SystemSettingsConfigAdoption — the one-time
// carry-over of a value an older release had configured, removed because no deployment ever ran a
// release it could upgrade from. FileAnalysis:ApiKey and Legal:PseudonymizationSecret are absent for
// a different reason: both live in the encrypted secret store (issue #445), which configuration never
// feeds. Outside Production an unset pseudonymization secret still falls back to the fixed
// development value, so the Aspire stack's delete flow works with nothing configured.

// No ApiBaseAddress environment variable: the client is a WASM app running in the BROWSER, which
// never sees this host process's environment — see the comment in Odyssey.Client/Program.cs for how
// it resolves the API address instead.
builder
    .AddProject<Projects.Odyssey_Client>("client")
    .WithHttpEndpoint(name: "client-http")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();

string BuildConnectionString(string database) =>
    $"server={mariadbServer};port={mariadbHostPort};database={database};user={mariadbUser};password={mariadbPassword};";

void ForwardOptionalSetting(IResourceBuilder<ProjectResource> resource, string envName, string configKey)
{
    var value = config[configKey];
    if (!string.IsNullOrEmpty(value))
    {
        resource.WithEnvironment(envName, value);
    }
}

static void LoadDotEnv(IDistributedApplicationBuilder appBuilder)
{
    var path = Path.Combine(appBuilder.AppHostDirectory, "..", ".env");
    if (!File.Exists(path))
    {
        return;
    }

    // .env uses the same variable names docker compose reads; map them onto the Aspire:* keys.
    var keyMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["EMAIL_SMTP_HOST"] = "Aspire:Email:SmtpHost",
        ["EMAIL_SMTP_PORT"] = "Aspire:Email:SmtpPort",
        ["EMAIL_USE_STARTTLS"] = "Aspire:Email:UseStartTls",
    };

    var values = new Dictionary<string, string?>(StringComparer.Ordinal);
    foreach (var rawLine in File.ReadAllLines(path))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line[0] == '#')
        {
            continue;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            continue;
        }

        var name = line[..separator].Trim();
        if (!keyMap.TryGetValue(name, out var configKey))
        {
            continue;
        }

        var value = line[(separator + 1)..].Trim().Trim('"', '\'');
        if (value.Length > 0)
        {
            values[configKey] = value;
        }
    }

    // Added last → takes precedence over appsettings / user-secrets for these keys.
    if (values.Count > 0)
    {
        appBuilder.Configuration.AddInMemoryCollection(values);
    }
}

// Whitespace is rejected, not just absence: `appsettings.json` shipped empty credential strings
// until issue #458, and `?? throw` let them straight through into the MariaDB container, which then
// refused to initialise with no indication of why. Name the override paths in the message — the
// values that actually matter live in user-secrets or `appsettings.Development.json`, neither of
// which is in the repository.
string GetRequiredValue(string key) =>
    config[key] is { } value && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException(
            $"Configuration value '{key}' was not found or is empty. Set it in " +
            $"Odyssey.AppHost/appsettings.json, in appsettings.Development.json, or with " +
            $"`dotnet user-secrets set \"{key}\" <value> --project Odyssey.AppHost`.");

int GetRequiredInt(string key) =>
    config.GetValue<int?>(key) ?? throw new InvalidOperationException($"Configuration value '{key}' was not found.");

bool GetRequiredBool(string key) =>
    config.GetValue<bool?>(key) ?? throw new InvalidOperationException($"Configuration value '{key}' was not found.");
