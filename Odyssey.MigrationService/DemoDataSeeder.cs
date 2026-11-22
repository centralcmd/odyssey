using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Legal;
using Odyssey.Dtos.Application;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.TestData;

namespace Odyssey.MigrationService;

/// <summary>
/// Seeds the deterministic demo dataset (docs/test-environment-and-e2e-spec.md, step 2).
/// Gated to Development/Testing and idempotent: it builds the dataset from
/// <see cref="DemoDataSet"/> and writes it once, skipping if it is already present. Everything with bytes —
/// the files, the photos and every row that names one — is the worker's <c>DemoFileSeeder</c>'s instead:
/// this job has no file volumes, and only a host with them can put a file on the object store.
/// Reference data (currencies, roles, permission claims) is NOT seeded here — it already
/// exists via migrations; this only references it.
/// </summary>
public sealed class DemoDataSeeder(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<DemoDataSeeder> logger)
    : IDemoDataSeeder
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            logger.LogInformation("Demo data seeding is disabled for this environment; skipping.");
            return;
        }

        var data = DemoDataSet.Build();

        using var scope = serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        await SeedUsersAsync(userManager, context, data, cancellationToken);
        // After the users exist: the ToS version records its publishing admin, and every demo user needs
        // acceptance rows or each seeded login lands on the /accept-terms interstitial instead of the app.
        await SeedLegalAcceptanceAsync(context, data, cancellationToken);
        // Locations before anything that links one (issue #380): contact addresses, properties, journal
        // entries and events reference a location by a RESTRICT key. After the users: a location records
        // who created it.
        await SeedLocationsAsync(context, data, cancellationToken);
        // Contacts first: they are FK principals for the finance references (transaction counterparty,
        // account custodian, policy insurer, contract party) as well as for PhotoPerson and
        // JournalEntryContact. Each step below is its own SaveChanges, so the order between them is the
        // ordering EF's FK graph cannot work out on our behalf.
        await SeedContactsAsync(context, data, cancellationToken);
        await SeedFinanceAsync(context, data, cancellationToken);
        // After finance: a property smart tag names a TransactionTag, a RESTRICT principal it seeds.
        await SeedPropertiesAsync(context, data, cancellationToken);
        await SeedJournalAsync(context, data, cancellationToken);
        await SeedCalendarAsync(context, data, cancellationToken);
        // After the users: a recipe records who created and last edited it (SET NULL keys).
        await SeedRecipesAsync(context, data, cancellationToken);
        // Last, and not part of the demo DATASET: the one settings row a dev stack cannot function
        // without and cannot be seeded with a fixed value. See its own remarks.
        await SeedClientBaseUrlAsync(context, cancellationToken);

        logger.LogInformation("Demo data seeding complete.");
    }

    /// <summary>
    /// Points <c>EmailClientBaseUrl</c> at whatever address this stack actually serves the client on,
    /// so the dev and Aspire stacks keep producing working confirmation and reset links with no
    /// environment variable (issue #8).
    ///
    /// <para>
    /// <strong>This is the ONE place a configured value still reaches a settings row, and it is seed
    /// data, not the adoption mechanism issue #8 N1 rules out.</strong> The distinction is the gate it
    /// sits behind: <see cref="IsEnabled"/> confines this to Development and Testing, so no
    /// Production deployment can reach it. Production seeds the migration's empty value and the
    /// administrator sets the real one at <c>/settings</c>. If a general configuration-adoption step
    /// is ever needed again — see CLAUDE.md on what would retire that decision — it is a separate
    /// mechanism with its own ownership rule, not an un-gating of this one.
    /// </para>
    ///
    /// <para>
    /// <strong>It must not hardcode <c>http://localhost:5199</c>.</strong> Aspire assigns the client
    /// address from <c>Aspire:Client:Urls</c> and forwards it here as <c>Email__ClientBaseUrl</c>; a
    /// fixed literal would mail links to the wrong port whenever that value differs. The literal is
    /// the fallback for the Compose dev stack alone, whose port is pinned in <c>docker-compose.yml</c>.
    /// </para>
    ///
    /// <para>
    /// <strong>Idempotent on the same terms as the rest of the seeder, but keyed on ownership rather
    /// than on value.</strong> A row an administrator has already edited carries a non-null
    /// <c>UpdatedBy</c>, and this leaves it alone — otherwise every restart of a dev stack would
    /// stamp over a value someone deliberately set. Comparing values instead cannot tell "never
    /// touched" from "set back to the seeded value on purpose".
    /// </para>
    /// </summary>
    private async Task SeedClientBaseUrlAsync(OdysseyContext context, CancellationToken cancellationToken)
    {
        var configured = configuration["Email:ClientBaseUrl"];
        var value = string.IsNullOrWhiteSpace(configured) ? DevelopmentClientBaseUrl : configured.Trim();

        // Validated against the same rule the PUT path applies, so the seeder cannot write a row the
        // send path would then refuse. A misconfigured Aspire value is logged and skipped rather than
        // stored: an unusable row fails every send closed, which is a worse dev experience than the
        // empty row it would replace.
        // Fully qualified rather than imported: a `using Odyssey.Dtos;` here would make `Sex`
        // ambiguous against Odyssey.Dtos.Application.Sex, the one allowed shadow in the merged
        // projects (issue #316 §6) and one this file already names.
        if (Odyssey.Dtos.EmailClientBaseUrlRule.Canonicalize(value) is not { } canonical)
        {
            logger.LogWarning(
                "Skipping the demo EmailClientBaseUrl seed: the configured client base URL is not a "
                + "usable public origin. Set it at /settings instead.");
            return;
        }

        var row = await context.SystemSettings
            .FirstOrDefaultAsync(setting => setting.Key == SystemSettingsKeys.EmailClientBaseUrl, cancellationToken);

        if (row is null)
        {
            // Should not happen post-migration; created rather than skipped so a database built by
            // EnsureCreated (the fast test tiers) gets the row too.
            row = new SystemSetting { Key = SystemSettingsKeys.EmailClientBaseUrl, Value = canonical };
            context.SystemSettings.Add(row);
        }
        else if (row.UpdatedBy is not null)
        {
            return;
        }
        else
        {
            row.Value = canonical;
        }

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded the demo client base URL for confirmation and reset links.");
    }

    /// <summary>
    /// The Compose dev stack's client address. A literal only because that stack's port is fixed in
    /// <c>docker-compose.yml</c>; Aspire supplies its own and overrides this.
    /// </summary>
    private const string DevelopmentClientBaseUrl = "http://localhost:5199";

    /// <summary>The shared allow-list gate (<see cref="DemoDataGate"/>), which the worker's demo-file seeder reads too.</summary>
    private bool IsEnabled() => DemoDataGate.IsEnabled(environment, configuration, logger);

    private async Task SeedUsersAsync(
        UserManager<ApplicationUser> userManager,
        OdysseyContext context,
        DemoDataSet data,
        CancellationToken cancellationToken)
    {
        foreach (var demoUser in data.Users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var user = await userManager.FindByEmailAsync(demoUser.Email);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    Id = demoUser.Id,
                    UserName = demoUser.Email,
                    Email = demoUser.Email,
                    EmailConfirmed = true,
                };

                // Hashed directly rather than through the password-validating CreateAsync overload: the
                // shared demo password is deliberately short and human-typeable (it is documented in the
                // README and typed by hand in the E2E suites), so it does not satisfy the production
                // password policy this job applies to the bootstrap administrator (issue #290). Demo
                // credentials are a fixture, not an account anyone is trusted with.
                user.PasswordHash = userManager.PasswordHasher.HashPassword(user, demoUser.Password);

                var created = await userManager.CreateAsync(user);
                if (!created.Succeeded)
                {
                    var errors = string.Join("; ", created.Errors.Select(error => $"{error.Code}: {error.Description}"));
                    throw new InvalidOperationException($"Failed to create demo user '{demoUser.Email}': {errors}");
                }

                logger.LogInformation("Created demo user {Email} ({Role}).", demoUser.Email, demoUser.Role);
            }

            // Ensure every demo user can log in deterministically: confirmed, never locked out
            // (require-admin-approval disables every newly added user by default), 2FA off, and never
            // behind the forced-password-change gate — that flag belongs to the seeded bootstrap
            // administrator alone (issue #290), and setting it here would break every E2E login.
            user.EmailConfirmed = true;
            user.LockoutEnabled = false;
            user.LockoutEnd = null;
            user.TwoFactorEnabled = false;
            user.MustChangePassword = false;
            await userManager.UpdateAsync(user);

            // Assign the role by inserting the join row directly. The seeded roles store their
            // NormalizedName lower-cased, which the default upper-invariant normalizer behind
            // UserManager.AddToRoleAsync cannot match — so we link by the known role id instead.
            var roleId = RoleIdFor(demoUser.Role);
            var alreadyAssigned = await context.UserRoles
                .AnyAsync(userRole => userRole.UserId == user.Id && userRole.RoleId == roleId, cancellationToken);
            if (!alreadyAssigned)
            {
                context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = roleId });
                await context.SaveChangesAsync(cancellationToken);
            }

            // A complete profile (issue #316) so the seeded login skips the first-login onboarding gate.
            var hasProfile = await context.UserProfiles
                .AnyAsync(profile => profile.UserId == user.Id, cancellationToken);
            if (!hasProfile)
            {
                context.UserProfiles.Add(new UserProfile
                {
                    UserId = user.Id,
                    FirstName = demoUser.FirstName,
                    LastName = demoUser.LastName,
                    DisplayName = demoUser.DisplayName,
                    BirthDate = demoUser.BirthDate,
                    Sex = (Sex)demoUser.Sex,
                });
                await context.SaveChangesAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Seed one published Terms of Service version plus an acceptance of it, and of the current
    /// <c>LICENSE</c>, for every demo user (issue #354 §13, AC 15).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the browser and API E2E suites would break on their very first step: the demo logins
    /// are the whole premise of those tiers, and a fresh database makes every one of them non-compliant.
    /// The License digest is computed with the same <see cref="LicenseDocumentProvider"/> the API uses,
    /// not a local copy, so seeded rows can't hash to something the API doesn't recognise.
    /// </para>
    /// <para>
    /// Idempotency is per user <em>and per artefact</em>, matching how the compliance rule itself is
    /// scoped, rather than "a ToS version exists ⇒ nothing to do". A single all-or-nothing guard silently
    /// skips the users that need rows most: renaming a demo login changes its email-derived id, so the
    /// next run creates a brand-new user that the guard then denies acceptances to, and every seeded
    /// login 451s (this is exactly what #417's demo-email rename did to already-seeded databases). The
    /// same applies whenever the <c>LICENSE</c> text changes, which re-gates everyone by design.
    /// </para>
    /// <para>
    /// The check is "has this user responded to this artefact at all", not "did they accept it", so a
    /// deliberate decline made through the UI is left standing. Seeding a fresh acceptance over it would
    /// also never work: <see cref="DemoDataDefaults.LegalRespondedAt"/> is a fixed past timestamp, so the
    /// live decline would stay the most recent response and every restart would append another dead row.
    /// </para>
    /// </remarks>
    private async Task SeedLegalAcceptanceAsync(
        OdysseyContext context,
        DemoDataSet data,
        CancellationToken cancellationToken)
    {
        var respondedAt = DemoDataDefaults.LegalRespondedAt;

        var version = await CurrentTermsOfServiceVersionAsync(context, cancellationToken);
        if (version is null)
        {
            var publishingAdmin = data.Users.FirstOrDefault(user => user.Role == RoleDefinitions.Admin);

            version = new TermsOfServiceVersion
            {
                Content = DemoDataDefaults.TermsOfServiceContent,
                PublishedAt = respondedAt,
                PublishedByUserId = publishingAdmin?.Id,
            };
            context.TermsOfServiceVersions.Add(version);
            await context.SaveChangesAsync(cancellationToken);
        }

        var licenseHash = new LicenseDocumentProvider(AppContext.BaseDirectory).Get().Sha256;
        var demoUserIds = data.Users.Select(demoUser => demoUser.Id).ToList();

        var haveLicenseResponse = await context.LicenseAcceptances
            .Where(row => demoUserIds.Contains(row.UserId) && row.LicenseHash == licenseHash)
            .Select(row => row.UserId)
            .Distinct()
            .ToHashSetAsync(cancellationToken);

        var haveTermsResponse = await context.TermsOfServiceAcceptances
            .Where(row => demoUserIds.Contains(row.UserId) && row.TermsOfServiceVersionId == version.Id)
            .Select(row => row.UserId)
            .Distinct()
            .ToHashSetAsync(cancellationToken);

        var seeded = 0;
        foreach (var demoUser in data.Users)
        {
            if (!haveLicenseResponse.Contains(demoUser.Id))
            {
                context.LicenseAcceptances.Add(new LicenseAcceptance
                {
                    UserId = demoUser.Id,
                    LicenseHash = licenseHash,
                    Accepted = true,
                    RespondedAt = respondedAt,
                });
                seeded++;
            }

            if (!haveTermsResponse.Contains(demoUser.Id))
            {
                context.TermsOfServiceAcceptances.Add(new TermsOfServiceAcceptance
                {
                    UserId = demoUser.Id,
                    TermsOfServiceVersionId = version.Id,
                    Accepted = true,
                    RespondedAt = respondedAt,
                });
                seeded++;
            }
        }

        if (seeded == 0)
        {
            logger.LogInformation("Demo legal acceptances already present; skipping legal seed.");
            return;
        }

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} demo legal acceptance rows against ToS version {VersionId}.", seeded, version.Id);
    }

    /// <summary>"Current" matches <c>LegalComplianceService</c>: highest PublishedAt, ties to highest Id.</summary>
    private static Task<TermsOfServiceVersion?> CurrentTermsOfServiceVersionAsync(
        OdysseyContext context, CancellationToken cancellationToken) =>
        context.TermsOfServiceVersions
            .OrderByDescending(version => version.PublishedAt)
            .ThenByDescending(version => version.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static string RoleIdFor(string role) => role switch
    {
        RoleDefinitions.Admin => RoleDefinitions.AdminId,
        RoleDefinitions.Owner => RoleDefinitions.OwnerId,
        RoleDefinitions.User => RoleDefinitions.UserId,
        RoleDefinitions.Guest => RoleDefinitions.GuestId,
        _ => throw new InvalidOperationException($"Unknown demo role '{role}'."),
    };

    private async Task SeedFinanceAsync(OdysseyContext context, DemoDataSet data, CancellationToken cancellationToken)
    {
        var sentinelId = data.Accounts[0].AccountId;
        if (await context.Accounts.AnyAsync(account => account.AccountId == sentinelId, cancellationToken))
        {
            logger.LogInformation("Demo finance data already present; skipping finance seed.");
            return;
        }

        // Insert order is handled by EF's FK graph within one SaveChanges; all referenced principals
        // (accounts, tags, budgets) are added in the same unit. The contact references here (transaction
        // counterparty, account custodian, policy insurer, contract party) are real FKs whose principals
        // were committed by SeedContactsAsync, which is why the caller runs it first.
        await context.TransactionTags.AddRangeAsync(data.Tags, cancellationToken);
        await context.ExchangeRates.AddRangeAsync(data.ExchangeRates, cancellationToken);
        await context.Instruments.AddRangeAsync(data.Instruments, cancellationToken);
        await context.InstrumentPrices.AddRangeAsync(data.InstrumentPrices, cancellationToken);
        await context.Accounts.AddRangeAsync(data.Accounts, cancellationToken);
        await context.AccountEstimates.AddRangeAsync(data.AccountEstimates, cancellationToken);
        await context.Terms.AddRangeAsync(data.Terms, cancellationToken);
        await context.Budgets.AddRangeAsync(data.Budgets, cancellationToken);
        await context.BudgetItems.AddRangeAsync(data.BudgetItems, cancellationToken);
        await context.Transactions.AddRangeAsync(data.Transactions, cancellationToken);
        await context.TransactionTagLinks.AddRangeAsync(data.TransactionTagLinks, cancellationToken);
        await context.Contracts.AddRangeAsync(data.Contracts, cancellationToken);
        // Parties naming a property wait for SeedPropertiesAsync: the property is their FK principal,
        // and it cannot be seeded before this step because its smart tags name the tags seeded here.
        await context.ContractParties.AddRangeAsync(
            data.ContractParties.Where(party => party.PropertyId is null), cancellationToken);
        await context.ContractEvents.AddRangeAsync(data.ContractEvents, cancellationToken);
        await context.TaxStatements.AddRangeAsync(data.TaxStatements, cancellationToken);
        await context.TaxStatementTags.AddRangeAsync(data.TaxStatementTags, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded {Accounts} accounts, {Budgets} budgets, {Transactions} transactions, {Contracts} contracts.",
            data.Accounts.Count, data.Budgets.Count, data.Transactions.Count, data.Contracts.Count);
    }

    /// <summary>
    /// The demo properties (issue #167). Its own all-or-nothing sentinel rather than a share of
    /// <see cref="SeedFinanceAsync"/>'s, so a dev database finance-seeded before properties existed
    /// still gains them on the next run — and a second run inserts nothing.
    /// </summary>
    private async Task SeedPropertiesAsync(OdysseyContext context, DemoDataSet data, CancellationToken cancellationToken)
    {
        var sentinelId = data.Properties[0].PropertyId;
        if (await context.Properties.AnyAsync(property => property.PropertyId == sentinelId, cancellationToken))
        {
            logger.LogInformation("Demo properties already present; skipping property seed.");
        }
        else
        {
            // The detail rows travel on the Property navigations.
            await context.Properties.AddRangeAsync(data.Properties, cancellationToken);
            await context.PropertyEstimates.AddRangeAsync(data.PropertyEstimates, cancellationToken);
            await context.PropertySmartTags.AddRangeAsync(data.PropertySmartTags, cancellationToken);
            // Their contracts were seeded by SeedFinanceAsync, which leaves these out (issue #218).
            await context.ContractParties.AddRangeAsync(
                data.ContractParties.Where(party => party.PropertyId is not null), cancellationToken);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Seeded {Properties} properties.", data.Properties.Count);
        }

        await SeedPropertyEventsAsync(context, data, cancellationToken);
    }

    /// <summary>
    /// The property event logs (issue #209), sentinel-gated on their own so a dev database seeded with
    /// properties before events existed still gains them on the next run — and a second run inserts
    /// nothing.
    /// </summary>
    private async Task SeedPropertyEventsAsync(OdysseyContext context, DemoDataSet data, CancellationToken cancellationToken)
    {
        if (data.PropertyEvents.Count == 0)
        {
            return;
        }

        var sentinelId = data.PropertyEvents[0].EventId;
        if (await context.PropertyEvents.AnyAsync(e => e.EventId == sentinelId, cancellationToken))
        {
            logger.LogInformation("Demo property events already present; skipping property event seed.");
            return;
        }

        await context.PropertyEvents.AddRangeAsync(data.PropertyEvents, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded {Events} property events.", data.PropertyEvents.Count);
    }

    private async Task SeedContactsAsync(OdysseyContext context, DemoDataSet data, CancellationToken cancellationToken)
    {
        if (data.Contacts.Count == 0)
        {
            return;
        }

        // All-or-nothing sentinel on the first contact id (mirrors SeedFinanceAsync). The Person/Org
        // details and address/email/phone children hang off each Contact as navigation graphs, so a single
        // AddRange + SaveChanges inserts the whole aggregate via EF's FK ordering.
        var sentinelId = data.Contacts[0].ContactId;
        if (await context.Contacts.AnyAsync(c => c.ContactId == sentinelId, cancellationToken))
        {
            logger.LogInformation("Demo contact data already present; skipping contact seed.");
            return;
        }

        await context.Contacts.AddRangeAsync(data.Contacts, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedJournalAsync(OdysseyContext context, DemoDataSet data, CancellationToken cancellationToken)
    {
        if (data.JournalEntries.Count == 0)
        {
            return;
        }

        var sentinelId = data.JournalEntries[0].JournalEntryId;
        if (await context.JournalEntries.AnyAsync(entry => entry.JournalEntryId == sentinelId, cancellationToken))
        {
            logger.LogInformation("Demo journal data already present; skipping journal seed.");
            return;
        }

        // Tags first (principals for the join rows), then the aggregate roots and their owned/link
        // children — all in one SaveChanges so EF orders the inserts by the FK graph. The contact
        // references are real FKs whose principals the earlier seed steps committed. The photo and
        // attachment links are the worker's: they name files only it can write.
        await context.JournalTags.AddRangeAsync(data.JournalTags, cancellationToken);
        await context.JournalTaskTags.AddRangeAsync(data.JournalTaskTags, cancellationToken);
        await context.JournalEntries.AddRangeAsync(data.JournalEntries, cancellationToken);
        await context.JournalEntryTags.AddRangeAsync(data.JournalEntryTags, cancellationToken);
        await context.JournalEntryContacts.AddRangeAsync(data.JournalEntryContacts, cancellationToken);
        await context.JournalTasks.AddRangeAsync(data.JournalTasks, cancellationToken);
        await context.JournalTaskTagLinks.AddRangeAsync(data.JournalTaskTagLinks, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded {Entries} journal entries, {Tasks} tasks, {JournalTags} journal tags, {JournalTaskTags} task tags.",
            data.JournalEntries.Count, data.JournalTasks.Count, data.JournalTags.Count, data.JournalTaskTags.Count);
    }

    private async Task SeedRecipesAsync(OdysseyContext context, DemoDataSet data, CancellationToken cancellationToken)
    {
        if (data.Recipes.Count == 0)
        {
            return;
        }

        var sentinelId = data.Recipes[0].RecipeId;
        if (await context.Recipes.AnyAsync(recipe => recipe.RecipeId == sentinelId, cancellationToken))
        {
            logger.LogInformation("Demo recipes already present; skipping recipe seed.");
            return;
        }

        // Tags first (principals for the links), then the recipes and their children, in one SaveChanges so EF orders
        // the inserts by the FK graph. The photo and file links are the worker's: they name files only it can write.
        await context.RecipeTags.AddRangeAsync(data.RecipeTags, cancellationToken);
        await context.Recipes.AddRangeAsync(data.Recipes, cancellationToken);
        await context.RecipeIngredients.AddRangeAsync(data.RecipeIngredients, cancellationToken);
        await context.RecipeSteps.AddRangeAsync(data.RecipeSteps, cancellationToken);
        await context.RecipeTagLinks.AddRangeAsync(data.RecipeTagLinks, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded {Recipes} recipes and {RecipeTags} recipe tags.", data.Recipes.Count, data.RecipeTags.Count);
    }

    private async Task SeedLocationsAsync(OdysseyContext context, DemoDataSet data, CancellationToken cancellationToken)
    {
        if (data.Locations.Count == 0)
        {
            return;
        }

        // Record-level rather than one sentinel: a dev database seeded before issue #380 holds the five
        // household places but none of the address-backed ones the contacts and properties now link.
        // Matching on id alone is not enough on an upgraded database: the issue #380 conversion turns the
        // already-seeded contact and property addresses into places under fresh ids, so the demo place for
        // the same address would collide on the unique Name or AddressKey. Such a converted row stands in
        // for the demo one — the records that link it were seeded before the conversion and are skipped
        // below. The folding mirrors the indexes' case-insensitive collation.
        var existing = await context.Locations
            .Select(location => new { location.LocationId, location.Name, location.AddressKey })
            .ToListAsync(cancellationToken);
        var presentIds = existing.Select(location => location.LocationId).ToHashSet();
        var takenNames = existing.Select(location => location.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var takenKeys = existing.Where(location => location.AddressKey is not null)
            .Select(location => location.AddressKey!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = data.Locations
            .Where(location => !presentIds.Contains(location.LocationId)
                               && !takenNames.Contains(location.Name)
                               && (location.AddressKey is null || !takenKeys.Contains(location.AddressKey)))
            .ToList();
        if (missing.Count == 0)
        {
            logger.LogInformation("Demo locations already present; skipping location seed.");
            return;
        }

        await context.Locations.AddRangeAsync(missing, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded {Locations} locations.", missing.Count);
    }

    private async Task SeedCalendarAsync(OdysseyContext context, DemoDataSet data, CancellationToken cancellationToken)
    {
        if (data.Calendars.Count == 0)
        {
            return;
        }

        var sentinelId = data.Calendars[0].CalendarId;
        if (await context.Calendars.AnyAsync(calendar => calendar.CalendarId == sentinelId, cancellationToken))
        {
            logger.LogInformation("Demo calendar data already present; skipping calendar seed.");
            return;
        }

        await context.Calendars.AddRangeAsync(data.Calendars, cancellationToken);
        await context.RecurrencePatterns.AddRangeAsync(data.RecurrencePatterns, cancellationToken);
        await context.CalendarEvents.AddRangeAsync(data.CalendarEvents, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded {Calendars} calendars, {Events} calendar events, {Patterns} recurrence patterns.",
            data.Calendars.Count, data.CalendarEvents.Count, data.RecurrencePatterns.Count);
    }
}
