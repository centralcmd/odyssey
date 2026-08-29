using Odyssey.ApiClient;
using Odyssey.ApiClient.Auth;
using Odyssey.Client;
using Odyssey.Client.Auth;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Client.Theme;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var configuredApiBaseAddress = builder.Configuration["ApiBaseAddress"];

var shouldUseRelativeBase = string.IsNullOrWhiteSpace(configuredApiBaseAddress);

// "ApiBaseAddress" can only come from wwwroot/appsettings*.json, served as a static file: this code
// runs in the BROWSER, which never sees the dev-server host process's environment variables — so
// setting the variable on the Aspire client resource would be a no-op, and AppHost.cs deliberately
// does not (issue #422). Blazor WASM's own environment auto-detection (which would otherwise fetch
// appsettings.Development.json) also doesn't fire for an Aspire-launched project resource here, so
// falling back to the same-origin "/api/" path (correct under Docker, where nginx proxies it) leaves
// local `dotnet run`/Aspire dev with nothing to talk to and every request 404s to the SPA fallback.
// Debug vs. Release is a reliable, compile-time-fixed signal instead: Docker always publishes Release
// (see Odyssey.Client/Dockerfile), while every local run (Aspire's AppHost, or a bare
// `dotnet run --project Odyssey.Client`) is Debug by default — and the API's local port is fixed at
// 5188 in both of those cases (see Odyssey.AppHost/AppHost.cs, docker-compose.yml).
#if DEBUG
var localDebugApiBaseAddress = "http://localhost:5188";
#else
var localDebugApiBaseAddress = (string?)null;
#endif

var apiBaseAddress = shouldUseRelativeBase
    ? localDebugApiBaseAddress ?? new Uri(new Uri(builder.HostEnvironment.BaseAddress), "api/").ToString()
    : configuredApiBaseAddress;

// The request pipeline is browser-specific only at its outermost layer: BrowserCredentialsHandler
// opts fetch into sending the auth cookie cross-origin, then hands off to the library's portable
// antiforgery handler. A non-browser consumer swaps the outer handler for an HttpClientHandler with
// a cookie container and reuses everything below it.
//
// LegalComplianceHandler sits outermost of all (issue #354 §5): it inspects the final response of
// EVERY typed client, so a mid-session 451 routes to the acceptance interstitial no matter which
// client — or which background writer, e.g. PageStateService's debounced saves — made the call. It is
// browser-side for the same reason BrowserCredentialsHandler is: redirecting is a presentation
// decision, and Odyssey.ApiClient returns results rather than acting on them.
//
// PasswordChangeRequiredHandler sits alongside it (issue #406 §7), for the same reason and with the
// same reach: a forced password reset triggered mid-session shows up as a 403 on whatever call happens
// next, and one handler turns that into one redirect instead of ~200 call sites each checking. It
// signals through a singleton notifier rather than navigating itself — handler instances are built into
// the pipeline below, so no component can resolve *this* instance from DI.
builder.Services.AddTransient<BrowserCredentialsHandler>();
builder.Services.AddTransient<LegalComplianceHandler>();
builder.Services.AddSingleton<PasswordChangeRequiredNotifier>();
builder.Services.AddTransient<PasswordChangeRequiredHandler>();
builder.Services.AddScoped(sp =>
{
    var legalCompliance = sp.GetRequiredService<LegalComplianceHandler>();
    var passwordChange = sp.GetRequiredService<PasswordChangeRequiredHandler>();
    var browserCredentials = sp.GetRequiredService<BrowserCredentialsHandler>();
    var antiforgery = sp.GetRequiredService<AntiforgeryHandler>();
    legalCompliance.InnerHandler = passwordChange;
    passwordChange.InnerHandler = browserCredentials;
    browserCredentials.InnerHandler = antiforgery;
    antiforgery.InnerHandler = new HttpClientHandler();

    return new HttpClient(legalCompliance)
    {
        BaseAddress = new Uri(apiBaseAddress!)
    };
});

// Every typed API client + the transport core live in Odyssey.ApiClient.
builder.Services.AddOdysseyApiClient();

builder.Services.AddMudServices();
builder.Services.AddScoped<CookieAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<CookieAuthenticationStateProvider>());
builder.Services.AddScoped<IUserPreferenceService, UserPreferenceService>();
builder.Services.AddScoped<Odyssey.Client.Services.IPageStateService, Odyssey.Client.Services.PageStateService>();
builder.Services.AddScoped<Odyssey.Client.Services.IClipboardService, Odyssey.Client.Services.ClipboardService>();
// Scoped is app-lifetime in WASM, which is the point: currencies, tags and contacts are fetched once
// per session instead of once per dialog open (issue #372).
builder.Services.AddScoped<Odyssey.Client.Services.IReferenceDataCache, Odyssey.Client.Services.ReferenceDataCache>();
builder.Services.AddScoped<Odyssey.Client.Services.IImportLimitsCache, Odyssey.Client.Services.ImportLimitsCache>();
builder.Services.AddScoped<Odyssey.Client.Services.IUploadLimitsCache, Odyssey.Client.Services.UploadLimitsCache>();
builder.Services.AddScoped<Odyssey.Client.Services.IAccountLimitsCache, Odyssey.Client.Services.AccountLimitsCache>();
builder.Services.AddScoped<Odyssey.Client.Services.IFileAnalysisDisclosureCache, Odyssey.Client.Services.FileAnalysisDisclosureCache>();

builder.Services.AddAuthorizationCore(options =>
{
    var permissionClaims = typeof(PermissionClaims).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string) && field.Name != nameof(PermissionClaims.Type))
        .Select(field => (string)field.GetRawConstantValue()!)
        .Distinct();

    foreach (var claimValue in permissionClaims)
    {
        options.AddPolicy(claimValue, policy =>
            policy.RequireClaim(PermissionClaims.Type, claimValue));
    }
});

await builder.Build().RunAsync();
