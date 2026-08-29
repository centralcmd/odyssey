using Odyssey.Api;
using Odyssey.Api.DataExport;
using Odyssey.Api.Email;
using Odyssey.Api.FileExport;
using Odyssey.Api.Identity;
using Odyssey.Api.Legal;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Api.UserAdministration;
using Odyssey.Core.Finance;
using Odyssey.Core.Configuration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

// Before anything reads a secret (issue #451 §1.4). The compose `${VAR:?...}` guards only catch unset
// or empty, and every placeholder in .env.prod.example is a valid non-empty value that is also public
// in this repository — so an operator who edited only ODYSSEY_DOMAIN and GHCR_OWNER would otherwise
// boot with a database password published in this repository. It covers configuration only: the five
// application credentials are read from the encrypted secret store (issue #445), never from here.
// The Production check lives inside the guard, not in an `if` here — a gate at the call site is a
// branch no test can reach.
builder.Configuration.ThrowIfPlaceholderValues(builder.Environment.EnvironmentName);

builder.AddDatabases();

var emailSection = builder.Configuration.GetSection(EmailOptions.SectionName);

builder.Services.AddIdentityApiEndpoints<ApplicationUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<OdysseyContext>();

var identityBuilder = builder.Services.AddIdentityCore<ApplicationUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<OdysseyContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    // Password reset gets a provider of its own so its lifespan can be cut to an hour without
    // touching the shared "Default" provider that also issues email-confirmation tokens — those keep
    // the 1-day default that onboarding depends on (issue #405).
    .AddTokenProvider<PasswordResetTokenProvider<ApplicationUser>>(
        PasswordResetTokenProviderOptions.ProviderName)
    .AddApiEndpoints();

// Clears MustChangePassword on a completed reset or change (issue #406 §5.3). AddUserManager performs a
// plain AddScoped for UserManager<ApplicationUser>, so the last registration wins — which is why this must
// come AFTER both Identity builder chains above, or AddIdentityApiEndpoints' own registration would
// overwrite it. MapIdentityApi resolves UserManager<ApplicationUser>, not the derived type, so what
// matters is that *that* service type resolves to OdysseyUserManager; AdminPasswordResetApiTests pins it.
identityBuilder.AddUserManager<OdysseyUserManager>();

// A rotated security stamp otherwise leaves other sessions alive for up to Identity's default 30 minutes,
// which is far too long when the rotation exists to revoke a possibly-compromised session (issue #406 §5.4
// and #405). One minute costs at most one indexed user lookup per active session per minute, on a request
// that was already hitting the database. It is a security property of the system, so it is set in code
// rather than exposed as configuration.
builder.Services.Configure<SecurityStampValidatorOptions>(
    options => options.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.AddOptions<IdentityOptions>()
    .Configure(identity =>
        identity.Tokens.PasswordResetTokenProvider = PasswordResetTokenProviderOptions.ProviderName);

// Legal acceptance (issue #354 §5). This Replace MUST come after BOTH Identity builder calls above.
// Both of them register an IUserClaimsPrincipalFactory, so a custom factory handed to whichever call
// runs first is silently discarded by the other: the app builds, boots and logs in, the
// pending-acceptance claim is simply never added, and the entire feature enforces nothing with no error
// anywhere. Replacing after both is order-independent and therefore correct regardless of which
// registration would otherwise win — LegalClaimsFactoryTests resolves the service from a real container
// and asserts the custom type, so a future reordering or a third Identity registration can't quietly
// reintroduce that failure mode.
builder.Services.Replace(
    ServiceDescriptor.Scoped<IUserClaimsPrincipalFactory<ApplicationUser>, LegalComplianceClaimsPrincipalFactory>());

// SecurityStampValidator's revalidation (30-minute ValidationInterval, unmodified) is what re-runs the
// claims factory for an already-active session — the mechanism that bounds how long a session can
// outlive a newly published ToS. Wrapping the configured handler rather than replacing it keeps that
// behaviour intact and just records the principal being revalidated, which is how the factory tells an
// unattended revalidation from an interactive sign-in when compliance computation fails (§10.11).
// PostConfigure, not Configure, so this runs after every Identity registration regardless of order.
builder.Services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options =>
{
    var configuredValidate = options.Events.OnValidatePrincipal;
    options.Events.OnValidatePrincipal = async context =>
    {
        context.HttpContext.RequestServices.GetRequiredService<LegalRevalidationState>()
            .ExistingPrincipal = context.Principal;
        await configuredValidate(context);
    };
});

// The Legal options binding is GONE (issue #445 Wave 4). Its one member — the pseudonymization
// secret — moved to the encrypted secret store, so there is no section left to bind and no
// startup-time value left to validate.
//
// That removes the Production startup failure an unset secret used to cause, deliberately and with a
// cost: a Production deployment that upgrades without entering the credential now starts cleanly and
// fails at the first account deletion instead. A startup gate is not available any more — the value
// lives in a database this process has not migrated yet at the point options are validated, and a
// credential an administrator is expected to enter through the UI cannot be a precondition for the UI
// coming up. LegalPseudonymizer throws with the remedy in the message instead, inside the deletion's
// own transaction, so the acceptance rows stay intact and attributable.

// Singleton with an eagerly-bound content root: the LICENSE is read from disk once and cached for the
// process lifetime, so the per-request compliance computation never touches the filesystem.
builder.Services.AddSingleton<Odyssey.Context.Legal.ILicenseDocumentProvider>(
    _ => new Odyssey.Context.Legal.LicenseDocumentProvider(builder.Environment.ContentRootPath));
builder.Services.AddSingleton<ILegalPseudonymizer, LegalPseudonymizer>();
builder.Services.AddScoped<LegalComplianceService>();
builder.Services.AddScoped<LegalRevalidationState>();

// Pin the Identity application auth cookie's security flags explicitly rather than inheriting the
// framework defaults (plus ForwardedHeaders), so the production posture is unambiguous:
//   - HttpOnly: never expose the cookie to JavaScript (mitigates XSS cookie theft).
//   - SameSite=Strict: the browser never attaches it to cross-site requests — defense-in-depth
//     against CSRF on top of the per-request antiforgery token, which now covers the Identity
//     endpoints too (see MapIdentityApi/MapControllers below). The client and API are same-site in
//     every deployment (same origin via the nginx /api proxy in the container stack; same-site
//     localhost across ports in dev), so Strict costs nothing here.
//   - SecurePolicy=Always in Production (HTTPS-only behind the proxy, independent of ForwardedHeaders);
//     SameAsRequest elsewhere so the plain-HTTP dev/Compose stack and the real-HTTP API tests still
//     receive and return the cookie.
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsProduction()
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
});

// RequireConfirmedAccount is pinned true unconditionally (issue #349) rather than bound from config:
// SignInManager<TUser> takes IOptions<IdentityOptions> (not IOptionsMonitor) and caches
// optionsAccessor.Value for the process lifetime, so this flag can never itself be live-toggled here.
// It gates whether SignInManager consults IUserConfirmation<ApplicationUser> at all — pinning it true
// makes that seam (SystemSettingsUserConfirmation, registered below) the single live decision point
// for EmailRequireConfirmation, in both directions. Leaving this false or config-bound would silently
// close the gate that seam depends on regardless of the stored DB value.
builder.Services.AddOptions<IdentityOptions>()
    .Configure(identity =>
    {
        identity.SignIn.RequireConfirmedAccount = true;

        // Password policy for a financial-PII app: at least 16 characters spanning all four classes.
        // Shared with Odyssey.MigrationService, which applies the same rules to the configured bootstrap
        // administrator (issue #290); this server-side policy is the authoritative gate (enforced by
        // MapIdentityApi's /register and /manage/info), and the client register page only mirrors it.
        PasswordPolicy.Apply(identity.Password);
    });

// Email confirmation / password reset delivery. MapIdentityApi resolves the generic
// IEmailSender<ApplicationUser>; without it Identity uses a no-op sender that silently
// drops the confirmation link.
builder.Services.AddOptions<EmailOptions>().Bind(emailSection).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddTransient<IEmailSender<ApplicationUser>, SmtpEmailSender>();

// The same sender behind a narrower, outcome-returning seam for the admin-initiated reset (issue #406
// §5.1) — one composition and one send path, so an admin-issued link is byte-identical to a self-service
// one. Deliberately not a second implementation: AdminPasswordResetApiTests asserts the reuse.
builder.Services.AddTransient<IPasswordResetLinkSender, SmtpEmailSender>();

// An unset SmtpHost degrades to "log the action link, send nothing" — a reasonable local-dev
// convenience, and an unacceptable Production posture now that password-reset tokens travel this path
// (issue #405). SmtpHost carries no [Required] because the degraded mode is the *intended* behaviour
// outside Production, so the requirement is expressed here rather than as an annotation.
if (builder.Environment.IsProduction())
{
    builder.Services.AddOptions<EmailOptions>()
        .Validate(email => !string.IsNullOrWhiteSpace(email.SmtpHost),
            "Email:SmtpHost must be configured in Production.")
        .ValidateOnStart();
}

// Singleton: MapIdentityApi resolves the sender once from the root provider, so the throttle's
// counters must outlive any scope (issue #393).
builder.Services.AddSingleton<IEmailSendThrottle, EmailSendThrottle>();
// Singleton because it owns the PER-PROCESS fallback key (issue #445 Wave 3): one generated per call
// would make every digest unique and destroy the correlation the digests exist for.
builder.Services.AddSingleton<IEmailRecipientHashKey, EmailRecipientHashKey>();

// The sign-in-side half of EmailRequireConfirmation (issue #349) — see SystemSettingsUserConfirmation.
// AddScoped (not TryAdd) so this overrides Identity's own DefaultUserConfirmation<TUser> registered
// above by AddIdentityApiEndpoints/AddIdentityCore, mirroring the IEmailSender override immediately above.
builder.Services.AddScoped<IUserConfirmation<ApplicationUser>, Odyssey.Api.Identity.SystemSettingsUserConfirmation>();

builder.Services.AddAuthorization(options =>
{
    foreach (var claimValue in RolePermissions.AllClaims)
    {
        options.AddPolicy(claimValue, policy =>
            policy.RequireClaim(PermissionClaims.Type, claimValue));
    }

    // Fail closed by default. Without this, an endpoint that is simply missing its
    // [Authorize] attribute is reachable anonymously — which is exactly how
    // GET /api/budgets/{id}/transactions once served financial records to the public
    // internet. MapControllers().RequireAuthorization() below covers controllers, but
    // not minimal-API endpoints; this covers both. Genuinely public endpoints
    // (login, register, password reset, /healthz) carry [AllowAnonymous], which wins.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var allowedCorsOrigins = CorsConfiguration.GetAllowedOrigins(builder.Configuration);

builder.Services.AddCors(options =>
{
    options.AddPolicy("ClientDevelopment", policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy
                // Not `_ => true`: reflection is limited to loopback and Codespaces hosts, since this
                // branch pairs it with AllowCredentials(). See CorsConfiguration.
                .SetIsOriginAllowed(CorsConfiguration.IsDevelopmentOriginAllowed)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                // Let the WASM client read the download filename on cross-origin file downloads
                // (dev serves the client on a different port than the API). Without this the browser
                // hides Content-Disposition and downloads fall back to a generic name.
                // X-Odyssey-Export-Rows (issue #343 §5/§11) rides alongside Content-Disposition for the
                // same reason: WASM's browser-hosted HttpClient strips any response header not on this
                // list, so a header the client must read needs the same treatment here regardless of
                // what the server actually sends.
                .WithExposedHeaders("Content-Disposition", "X-Odyssey-Export-Rows");
        }
        else
        {
            policy
                .WithOrigins(allowedCorsOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                // X-Odyssey-Export-Rows (issue #343 §5/§11) rides alongside Content-Disposition for the
                // same reason: WASM's browser-hosted HttpClient strips any response header not on this
                // list, so a header the client must read needs the same treatment here regardless of
                // what the server actually sends.
                .WithExposedHeaders("Content-Disposition", "X-Odyssey-Export-Rows");
        }
    });
});

// CSRF defense-in-depth for the cookie-authenticated SPA. The request token is echoed by the
// client in the X-XSRF-TOKEN header (see /api/antiforgery/token below). Validation is performed by
// the UseAntiforgery() middleware against the RequireAntiforgeryToken metadata attached to both the
// controllers and the Identity minimal-API endpoints (see MapIdentityApi/MapControllers below) — a
// metadata-only mechanism that, unlike the MVC AutoValidateAntiforgeryToken filter, doesn't depend
// on view-features services that AddControllers (Web API, no views) leaves unregistered.
// SuppressReadingTokenFromFormBody (issue #343 §5 design item 1): this client always sends the
// antiforgery token as the X-XSRF-TOKEN header (AntiforgeryHandler), never as a form field, so no
// protection is lost — and it stops antiforgery from parsing multipart/form-data request bodies at
// all, which is what makes ImportSizeLimitMiddleware's per-request transport limit (below) safe to
// apply regardless of how DefaultAntiforgeryTokenStore's form/header precedence actually resolves.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.SuppressReadingTokenFromFormBody = true;
});

builder.Services.AddIdentityRateLimiter(builder.Configuration);
builder.Services.AddImportExportRateLimiter();
builder.Services.AddAdminActionRateLimiter(builder.Configuration);

// The instance-wide half of the export concurrency control (issue #343 §5) — a singleton so every
// request's ExportConcurrencyFilter instance (one per request, via [TypeFilter]) shares the same
// underlying ConcurrencyLimiter.
builder.Services.AddSingleton<Odyssey.Api.GlobalExportConcurrencyLimiter>();

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.AddOpenApiExplorer();

builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<AccountTermService>();
builder.Services.AddScoped<AccountEstimateService>();
builder.Services.AddScoped<AccountSmartTagService>();
builder.Services.AddScoped<BudgetItemService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<TransactionTagService>();
builder.Services.AddScoped<Odyssey.Core.Journal.ContactService>();
builder.Services.AddScoped<Odyssey.Core.Journal.ContactVCardService>();
builder.Services.AddScoped<CurrencyService>();
builder.Services.AddScoped<ExchangeRateService>();
builder.Services.AddScoped<CurrencyConversionService>();
builder.Services.AddScoped<AccountTotalsService>();
builder.Services.AddScoped<FileService>();

// File upload size cap lives in one place (the "FileStorage" config section) and is applied at
// every layer that can reject an oversized request: the Kestrel request-body limit and the
// multipart form-length limit (both raised above their stock 30 MB / 128 MB defaults to match),
// and the application-level FileValidationService that produces the clean validation error. The
// reverse-proxy limits (nginx/Caddy) are static config kept at or above this value.
builder.Services.Configure<FileStorageOptions>(
    builder.Configuration.GetSection(FileStorageOptions.SectionName));
var fileStorageOptions = builder.Configuration.GetSection(FileStorageOptions.SectionName)
    .Get<FileStorageOptions>() ?? new FileStorageOptions();
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = fileStorageOptions.MaxRequestBodyBytes);
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = fileStorageOptions.MaxRequestBodyBytes);
// The cap on file CONTENT is now the admin-editable setting, not the configured transport ceiling
// (issue #421 Wave 4). FileStorage:MaxFileSizeBytes keeps its startup role above — it sizes Kestrel and
// the multipart limit, and bounds how far the setting can be raised — but the number a user is actually
// validated against is read live, so lowering the cap takes effect without a redeploy.
builder.Services.AddScoped<FileValidationService>(sp =>
    new FileValidationService(sp.GetRequiredService<IUploadLimitsLookup>()));

builder.Services.AddScoped<UserAdministrationService>();
builder.Services.AddScoped<Odyssey.Api.Identity.IUserDisplayNameResolver, Odyssey.Api.Identity.UserDisplayNameResolver>();
builder.Services.AddScoped<Odyssey.Api.Profiles.ProfileService>();
builder.Services.AddScoped<Odyssey.Api.Preferences.UserPreferencesService>();

builder.Services.AddScoped<TaxStatementService>();

builder.Services.AddScoped<InsuranceService>();

// Admin-configurable runtime settings store (issue #349). IMemoryCache isn't registered anywhere
// else in the solution — it backs SystemSettingsLookup's 30s TTL over the two Insurance fields above,
// the only cached (non-perimeter) reads this feature introduces.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<Odyssey.Api.SystemSettings.SystemSettingsService>();
builder.Services.AddScoped<Odyssey.Core.Finance.ISystemSettingsLookup, Odyssey.Api.SystemSettings.SystemSettingsLookup>();

// The sixteen import/export volume caps (issue #343 and a follow-up), same 30s-cached-lookup shape
// as the Insurance settings above, just owned by Odyssey.Core.Journal (the four import/export services'
// home) instead.
builder.Services.AddScoped<Odyssey.Core.Journal.IImportExportLimitsLookup, Odyssey.Api.SystemSettings.ImportExportLimitsLookup>();

// The six AI file-analysis settings (issue #421 Wave 1), same 30s-cached-lookup shape. Its own cache
// key rather than a method on ISystemSettingsLookup: that one's entry is shared with the Insurance
// eviction path, so folding these in would make an insurance save evict the analysis settings.
builder.Services.AddScoped<Odyssey.Core.Finance.IFileAnalysisSettingsLookup, Odyssey.Api.SystemSettings.FileAnalysisSettingsLookup>();

// The photo/journal per-request caps (issue #421 Wave 3). A second lookup rather than a method on
// ISystemSettingsLookup: the nine Wave 3 caps span two domain projects, and a lookup interface lives
// in the project that consumes it so that project's tests can fake it.
builder.Services.AddScoped<Odyssey.Core.Journal.IJournalLimitsLookup, Odyssey.Api.SystemSettings.JournalLimitsLookup>();

// The upload cap (issue #421 Wave 4) and the startup ceiling that bounds it. The ceilings object is a
// singleton because the configuration behind it is fixed at startup.
builder.Services.AddScoped<Odyssey.Core.Finance.IUploadLimitsLookup, Odyssey.Api.SystemSettings.UploadLimitsLookup>();
// The per-account limits (issue #434 key 15). Its own cache key and its own eviction, for the same
// reason every other lookup here has one: sharing an entry would make an unrelated save evict it.
builder.Services.AddScoped<Odyssey.Core.Finance.IAccountLimitsLookup, Odyssey.Api.SystemSettings.AccountLimitsLookup>();
builder.Services.AddSingleton<Odyssey.Api.SystemSettings.RequestCapCeilings>();

// Encrypted secret settings (issue #444). The registry is a singleton because its only input is the
// host environment; the protector is a singleton over IDataProtectionProvider; the durability check
// reads KeyManagementOptions, which is fixed once the container is built. Everything that touches a
// DbContext is scoped.
builder.Services.AddSingleton<Odyssey.Api.SystemSettings.SecretSettingsRegistry>();
builder.Services.AddSingleton<Odyssey.Api.SystemSettings.IKeyRingDurability,
    Odyssey.Api.SystemSettings.KeyRingDurability>();
builder.Services.AddSingleton<Odyssey.Context.Secrets.ISecretProtector,
    Odyssey.Context.Secrets.SecretProtector>();
builder.Services.AddScoped<Odyssey.Api.SystemSettings.SecretSettingsService>();
// The consumption seam. No consumers yet — each queued credential gets its own follow-up issue, which
// decides its own fallback policy on Unreadable and its own config-retirement question.
builder.Services.AddScoped<Odyssey.Context.Secrets.ISecretSettingsReader,
    Odyssey.Api.SystemSettings.SecretSettingsReader>();

builder.Services.AddScoped<ContractService>();

builder.Services.AddScoped<SubscriptionService>();

// Journal module (issue #311). The lookups keep the module boundary one-directional in code even though
// finance and journal now share one DbContext. No feature toggle — capability is gated by claims only.
builder.Services.AddScoped<Odyssey.Core.Finance.IContactLookup, Odyssey.Core.Journal.ContactLookup>();
builder.Services.AddScoped<Odyssey.Core.Finance.IContactReferenceGuard, Odyssey.Core.Finance.ContactReferenceGuard>();
builder.Services.AddScoped<Odyssey.Core.Finance.IContactMutationLock, Odyssey.Core.Finance.ContactMutationLock>();
builder.Services.AddScoped<Odyssey.Core.Finance.IFileLookup, Odyssey.Core.Finance.FileLookup>();
builder.Services.AddScoped<Odyssey.Core.Finance.IFileReferenceGuard, Odyssey.Core.Finance.FileReferenceGuard>();
builder.Services.AddScoped<Odyssey.Core.Journal.JournalEntryService>();
builder.Services.AddScoped<Odyssey.Core.Journal.JournalEntryIcsService>();
builder.Services.AddScoped<Odyssey.Core.Journal.JournalTagService>();
builder.Services.AddScoped<Odyssey.Core.Journal.JournalTaskService>();
builder.Services.AddScoped<Odyssey.Core.Journal.JournalTaskTagService>();
builder.Services.AddScoped<Odyssey.Core.Journal.TaskIcsService>();

// Photos module (issue #321), now part of the merged OdysseyContext; consumes the Finance lookups above and
// the bounded image-content reader for in-process EXIF/IPTC/XMP extraction. IPhotoLookup is the narrow
// surface the Journal module uses to link/find-or-create library photos. Gated by claims only.
builder.Services.AddScoped<Odyssey.Core.Finance.IImageContentReader, Odyssey.Core.Finance.ImageContentReader>();
builder.Services.AddSingleton<Odyssey.Core.Journal.IPhotoMetadataExtractor, Odyssey.Core.Journal.PhotoMetadataExtractor>();
builder.Services.AddScoped<Odyssey.Core.Journal.PhotoMetadataService>();
builder.Services.AddScoped<Odyssey.Core.Journal.PhotoService>();
builder.Services.AddScoped<Odyssey.Core.Journal.PhotoTagService>();
builder.Services.AddScoped<Odyssey.Core.Journal.PhotoAlbumService>();
builder.Services.AddScoped<Odyssey.Core.Journal.IPhotoLookup, Odyssey.Core.Journal.PhotoLookup>();

// Calendar module (issue #323). No feature toggle — capability is gated by claims only.
builder.Services.AddScoped<Odyssey.Core.Journal.CalendarService>();
builder.Services.AddScoped<Odyssey.Core.Journal.CalendarEventService>();
builder.Services.AddScoped<Odyssey.Core.Journal.RecurrencePatternService>();
builder.Services.AddScoped<Odyssey.Core.Journal.CalendarIcsService>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DataExportService>();

builder.Services.AddScoped<AdminFileExportService>();

builder.Services.Configure<FileAnalysisOptions>(
    builder.Configuration.GetSection(FileAnalysisOptions.SectionName));
// Transient, like every DelegatingHandler: IHttpClientFactory owns its lifetime and pools it with the
// handler chain, so it must resolve nothing scoped in its constructor.
builder.Services.AddTransient<Odyssey.Api.SystemSettings.FileAnalysisApiKeyHandler>();
builder.Services.AddScoped<FileAnalysisService>();
builder.Services.AddHttpClient<IFileAnalysisProvider, ClaudeFileAnalysisProvider>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<FileAnalysisOptions>>().Value;

    // No BaseAddress (issue #439): the destination is admin-editable now, so it is resolved per run
    // from the settings snapshot and passed to the provider as part of FileAnalysisTarget. A
    // registration-time BaseAddress would be fixed at startup and could not follow a repoint — and,
    // worse, would be the value stamped on jobs while requests went somewhere else.
    //
    // The API key is NO LONGER set here (issue #445 Wave 1). It moved to the encrypted secret store,
    // and this callback is synchronous — it cannot await a scoped OdysseyContext — while a
    // DefaultRequestHeaders entry is evaluated once at client construction and so could never follow a
    // rotation anyway. FileAnalysisApiKeyHandler attaches it per request instead. The accepted
    // consequence is unchanged and still stated on the settings row: repointing the base URL sends the
    // stored key to the new host.
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

    // The resilience handler below owns every timeout: HttpClient.Timeout wraps the whole pipeline,
    // so leaving it at the configured value would cut the retries off mid-flight. The per-call budget
    // lives on AttemptTimeout instead, which is where it belongs once a call can be retried.
    client.Timeout = Timeout.InfiniteTimeSpan;
})
    // Redirects are NOT followed (issue #439 §5.3a). Without this the client runs on the default
    // handler with AllowAutoRedirect = true, and .NET strips only Authorization across origins — a
    // custom x-api-key header survives, and a 307/308 preserves method and body, so the key and the
    // whole document would be re-POSTed to whatever host the configured one names. That would make the
    // bound this feature rests on ("the key goes to the host that is set") untrue, and would make
    // FileAnalysisJob.AnalyzerBaseUrlHost record the configured host rather than the one the data
    // reached. A 3xx becomes a provider error instead; see ClaudeFileAnalysisProvider.SendAsync.
    //
    // This is a fix to CURRENT behaviour, not only to what #439 adds: today's deploy-configured base
    // URL can already redirect the API key onward.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    // OUTSIDE the resilience pipeline, deliberately (issue #445 Wave 1): handlers registered earlier
    // sit further out, so an unreadable credential throws once here rather than being retried twice
    // and counted against the circuit breaker. A credential fault is not transient.
    .AddHttpMessageHandler<Odyssey.Api.SystemSettings.FileAnalysisApiKeyHandler>()
    // The slowest, most third-party-dependent path in the app: a transient 429/503 from the provider
    // used to surface as a hard user-facing failure (issue #382). Retries are safe here because both
    // call sites treat a failure as a recorded job/match failure, not a partial write.
    .AddStandardResilienceHandler()
    .Configure((resilience, sp) =>
    {
        var opts = sp.GetRequiredService<IOptions<FileAnalysisOptions>>().Value;
        var attempt = TimeSpan.FromSeconds(opts.TimeoutSeconds);

        // Keep the configured budget as the per-attempt one, so a single analysis call still gets its
        // full 120s. Two attempts' worth caps the total, so a timed-out attempt can be retried exactly
        // once rather than keeping the caller waiting for the full retry ladder. The sampling duration
        // must be at least double the attempt timeout or the options validator rejects the pipeline.
        resilience.AttemptTimeout.Timeout = attempt;
        resilience.TotalRequestTimeout.Timeout = attempt * 2;
        resilience.CircuitBreaker.SamplingDuration = attempt * 2;
        resilience.Retry.MaxRetryAttempts = 2;
    });

// Data Protection, registered UNCONDITIONALLY (issue #444 §10). Before this it was registered only
// when a keys path was configured, leaving the framework's implicit registration — an ephemeral or
// profile-local key ring — everywhere else. The shared application name keeps keys interchangeable
// across rebuilt containers and, now, across the API and the migrations job.
//
// Persisting to a mounted volume is what stops every redeploy rotating the ring and logging every
// user out; with credentials stored under it, it is also what stops them being lost. That volume is
// therefore SECRET-BEARING — see docs/deployment.md for the backup and incident-response guidance.
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Odyssey");
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    // A CONFIGURED but unwritable directory fails startup, deliberately. Data Protection's own
    // behaviour there is to fall back to an in-memory ring with a log warning, which is the silent
    // ephemeral failure this feature cannot tolerate. Note the posture split: an UNCONFIGURED path
    // only warns (a bare `dotnet run` and every CI host sit there, and the write-path 503 covers
    // them), while a configured-but-unwritable one is always a misconfiguration whose only silent
    // outcome is data loss.
    DataProtectionKeyDirectory.EnsureWritable(dataProtectionKeysPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

// Behind the reverse proxy (Caddy terminates TLS → client nginx → api over internal HTTP), honor
// X-Forwarded-* so the API sees the original https scheme. Required for Secure auth cookies and
// correct absolute links. The proxy IPs are dynamic within the Docker network, so we can't pin an
// exact address — but they are always private, so trust only the private (RFC 1918 + loopback)
// ranges rather than every source. That keeps a directly-connecting external client from spoofing
// X-Forwarded-For (which would otherwise poison the client IP seen by logging/audit). A deployment
// with a known proxy subnet can override this via ForwardedHeaders:KnownNetworks (CIDR list).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();

    var configuredNetworks = builder.Configuration
        .GetSection("ForwardedHeaders:KnownNetworks")
        .Get<string[]>();

    var networks = configuredNetworks is { Length: > 0 }
        ? configuredNetworks
        : ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    foreach (var cidr in networks)
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
});

var app = builder.Build();

// "Testing" is an in-process test-host name, not a deployment environment (issue #451 Phase 3): four
// places key off it to weaken the app, antiforgery enforcement included. Refuse to serve traffic under
// it rather than letting a self-hoster who typed it meaning "staging" find out later. See
// TestingEnvironmentGuard for why the signal is the server type.
TestingEnvironmentGuard.Validate(
    app.Environment.EnvironmentName,
    app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()?.GetType().FullName);

// Key-ring diagnostics (issue #444 §10). The DETECTED REPOSITORY TYPE is logged, not the config key:
// the config key is the cause, the repository type is the actual condition, and the durability check
// allow-lists durable types — so an operator diagnosing an unexpected 503 on a secret write needs to
// see what was classified, especially on a deployment persisting keys somewhere the allow-list does
// not yet name.
//
// A secondary signal only. An unconfigured path deliberately does NOT fail startup: a bare
// `dotnet run`, every CI host and any stack that has not adopted the keys volume all sit there, and
// the write-path refusal is what covers them. Failing startup instead would turn a recoverable
// misconfiguration into an outage for every deployment, including ones storing no secrets at all.
{
    var keyRing = app.Services.GetRequiredService<Odyssey.Api.SystemSettings.IKeyRingDurability>();
    if (keyRing.IsDurable)
    {
        app.Logger.LogInformation(
            "Data Protection key ring is persisted by {Repository}.", keyRing.RepositoryTypeName);
    }
    else
    {
        app.Logger.LogWarning(
            "Data Protection has no explicitly configured durable key repository (detected: "
            + "{Repository}). Auth cookies and antiforgery tokens will not survive a restart, and "
            + "writes to encrypted secret settings will be refused with 503. Set "
            + "DataProtection:KeysPath to a durable, writable directory.",
            keyRing.RepositoryTypeName);
    }
}

app.UseForwardedHeaders();

// Liveness probe + running version. The version is stamped from <Version> in
// Directory.Build.props (kept in sync by release-please) into the assembly's
// informational version; we trim the "+<git-sha>" build metadata the SDK appends.
app.MapGet("/healthz", () =>
{
    var informationalVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    var version = informationalVersion.Split('+', 2)[0];
    return Results.Ok(new { status = "ok", version });
}).AllowAnonymous();

// The whole Identity group is limited, not just the anonymous four: /manage/* is authenticated but
// still cheap to hammer, and one policy over the group leaves no endpoint uncovered as MapIdentityApi
// grows.
var identityApi = app.MapIdentityApi<ApplicationUser>()
    .RequireRateLimiting(IdentityRateLimiting.PolicyName);

// The global FallbackPolicy (see AddAuthorization above) requires an authenticated user on any
// endpoint that declares no authorization metadata of its own — which is every endpoint
// MapIdentityApi maps outside /manage. Without this, /login itself would demand a login.
//
// Scoped by route rather than applied to the whole group on purpose: MapIdentityApi puts
// /manage/* behind its own RequireAuthorization, and AllowAnonymous beats it wherever both are
// present. A blanket .AllowAnonymous() here would silently expose /manage/info, /manage/2fa and
// the rest to unauthenticated callers.
identityApi.Add(endpoint =>
{
    if (endpoint is RouteEndpointBuilder route &&
        route.RoutePattern.RawText?.Contains("/manage", StringComparison.OrdinalIgnoreCase) != true)
    {
        endpoint.Metadata.Add(new AllowAnonymousAttribute());
    }
});

// ...and a far tighter one over just /forgotPassword and /resendConfirmationEmail, which put a
// message on the wire on every call. Applied after the group policy on purpose — see the method's
// remarks for why the ordering is what makes it take effect.
identityApi.RequireMailEndpointRateLimiting(
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IdentityRateLimiting)));

// One structured line per completed password reset (issue #405) — attached to /resetPassword alone,
// not to the group, for the reason spelled out in the method's remarks.
identityApi.LogPasswordResetCompletion(
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PasswordResetLogging)));

// Swagger is on by default in Development, and elsewhere only when Swagger:Enabled says so — but
// NEVER in Production (issue #451 §1.3). The config half used to be the whole story, and it defaults
// to true in the container stack, so any deployment that did not explicitly pass the variable served
// the API's full surface at /api/swagger through the client's nginx proxy. The environment check is
// the half that cannot be forgotten in an env file.
var enableSwagger = app.Environment.IsDevelopment()
    || (app.Configuration.GetValue<bool>("Swagger:Enabled") && !app.Environment.IsProduction());
if (enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    ExceptionHandler = GlobalExceptionHandler.HandleAsync
});

// Enable CORS for the configured client origins in all environments. This allows running the
// frontend and backend from Codespaces preview URLs and other dev tooling.
app.UseCors("ClientDevelopment");

if (app.Environment.IsProduction())
{
    // In production, we want to redirect to HTTPS when certificates are configured.
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

// The admin-initiated-reset gate (issue #406 §5.6): while a user's MustChangePassword is set, every
// authenticated endpoint is refused except the five that let them out of it. Placed immediately after
// UseAuthorization so the principal exists, and BEFORE the legal gate on purpose: a forced credential
// change outranks acceptance, and a user with both pending must be able to reach
// POST /api/account/password (which the legal allowlist admits for exactly this reason) rather than being
// bounced between two gates neither of which it can satisfy. Unconditional, including in Testing — no user
// has the flag set by default, so it is inert until an admin triggers a reset.
app.UseMiddleware<PasswordChangeRequiredMiddleware>();

// The legal-acceptance gate (issue #354 §5). Positioned here on purpose: after UseAuthentication so the
// principal (and therefore the pending-acceptance claim) exists, and before UseAntiforgery so a gated
// write is answered with the 451 the client's LegalComplianceHandler keys off rather than an antiforgery
// 400 that tells it nothing. Unconditional, including in Testing — the TestAuthHandler principal never
// carries the claim, so it is a no-op there and the gate's real logic is covered by its own tests.
app.UseMiddleware<LegalComplianceMiddleware>();

// Runs after UseForwardedHeaders so the limiter partitions on the real client IP rather than the
// reverse proxy's, and after routing has selected the endpoint so the policy and mail-endpoint
// metadata are visible. Only the Identity group is limited: everything else carries no policy, and
// the global limiter that adds the mail window resolves to a no-op partition for it.
app.UseRateLimiter();

// Applies the configured, admin-editable per-surface byte cap to the request transport before
// anything (including antiforgery, next) can read the body (issue #343 §5). Unconditional — including
// in Testing — so every test tier exercises the same code path; a no-op for any endpoint not carrying
// ImportSizeLimitMetadata.
app.UseMiddleware<ImportSizeLimitMiddleware>();

app.UseAntiforgery();

// UseAntiforgery defers enforcement: on a missing/invalid token it sets a failed
// IAntiforgeryValidationFeature and continues, expecting the endpoint to react. MVC controllers
// reading a JSON body have nothing that inspects it (the MVC filter that would relies on
// view-features services a Web API doesn't register), so translate a failed validation into a 400
// here before the controller runs.
app.Use(async (context, next) =>
{
    if (context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false })
    {
        // Emit the same RFC 7807 application/problem+json shape as every other error path
        // (GlobalExceptionHandler / ControllerProblemExtensions) so clients parse failures uniformly.
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = ReasonPhrases.GetReasonPhrase(StatusCodes.Status400BadRequest),
            Detail = "Invalid or missing antiforgery token.",
        };
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
        return;
    }

    await next(context);
});

// Issues an antiforgery request token (and sets the paired secret cookie) for the SPA to echo in
// the X-XSRF-TOKEN header on writes. GET is a safe method, so the endpoint isn't itself validated.
// Reachable at /api/antiforgery/token in dev and (via the nginx /api/ proxy) in the container stack.
app.MapGet("/api/antiforgery/token", (IAntiforgery antiforgery, HttpContext context) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { token = tokens.RequestToken ?? string.Empty });
}).AllowAnonymous();

// The antiforgery middleware validates only unsafe (POST/PUT/PATCH/DELETE) requests to endpoints
// carrying RequireAntiforgeryToken metadata; safe methods (incl. GETs, so /confirmEmail) pass
// through untouched. Both the MVC controllers and the state-changing Identity minimal-API endpoints
// (/login, /register, /manage/*, ...) are tagged, so the whole write surface is guarded uniformly
// rather than leaving the Identity flows on SameSite-cookie protection alone. The Testing
// environment is exempt so the in-memory API tests, which inject claims and carry no token, keep
// passing — and TestingEnvironmentGuard, above, is what stops that exemption reaching a deployment:
// only an in-process test host is allowed to run under the name.
// Fail closed: require an authenticated user on every controller endpoint by default, so a
// controller action that forgets its [Authorize] attribute can't leak data. Per-action
// [Authorize(Policy = ...)] claim checks still apply on top (policies combine); the Identity
// minimal-API (/login, /register, ...), /healthz and the antiforgery-token endpoint are not
// controllers and are deliberately left anonymous.
if (!app.Environment.IsEnvironment("Testing"))
{
    identityApi.WithMetadata(new RequireAntiforgeryTokenAttribute());
    app.MapControllers()
        .WithMetadata(new RequireAntiforgeryTokenAttribute())
        .RequireAuthorization();
}
else
{
    app.MapControllers().RequireAuthorization();
}

// Fail fast rather than boot into an unrecoverable lockout (issue #406 §5.6): a removed
// [PasswordChangeExempt], or a renamed route on one of the five endpoints that let a gated user out of
// the state, is not a hole — it is a user who can never change their password or sign out. Last, so
// every Map* call and convention above has been applied.
PasswordChangeExemptRoutes.ValidateExemptEndpoints(
    ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));

app.Run();
