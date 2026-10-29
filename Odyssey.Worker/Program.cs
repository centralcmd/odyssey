using Odyssey.Core.Configuration;
using Odyssey.Core.FileStorage;
using Odyssey.Worker;

var builder = Host.CreateApplicationBuilder(args);

// The same placeholder guard as the API and the migrations job (issue #451 §1.4).
builder.Configuration.ThrowIfPlaceholderValues(builder.Environment.EnvironmentName);

// The weather job composes site-local days from IANA zones; the alpine image has none without tzdata.
TimeZoneDataGuard.EnsureTimeZoneDataAvailable();

builder.AddOdysseyWorker();

var host = builder.Build();
if (string.IsNullOrWhiteSpace(builder.Configuration["DataProtection:KeysPath"]))
{
    // Not fatal, matching the API's posture for an unconfigured path: every secret then reads Unreadable,
    // each consumer fails closed and logs, and the jobs that need no secret keep running.
    host.Services.GetRequiredService<ILogger<Program>>().LogWarning(
        "DataProtection:KeysPath is not set, so this worker cannot read the API's key ring and no stored "
        + "secret can be decrypted. Mount the API's keys volume read-only and set the same path.");
}

// File storage (issue #346 A3.2): the same checks as the API, verify-only. A volume the API has yet to initialise
// or adopt is waited for — the API is the only host that writes a marker — and any other failing verdict stops the
// worker here, with every job in it, before anything runs.
await host.Services.VerifyFileStorageForWorkerAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));

host.Run();
