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
/// <see cref="DemoDataSet"/> and writes it once, skipping if it is already present.
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
        // Contacts first: they are FK principals for the finance references (transaction counterparty,
        // account custodian, policy insurer, contract party) as well as for PhotoPerson and
        // JournalEntryContact. Each step below is its own SaveChanges, so the order between them is the
        // ordering EF's FK graph cannot work out on our behalf.
        await SeedContactsAsync(context, data, cancellationToken);
        // Finance next: it owns the Files store, which the photo and attachment rows below reference.
        await SeedFinanceAsync(context, data, cancellationToken);
        // Photos before journal: a journal photo links a library Photo by PhotoId, a real FK.
        await SeedPhotosAsync(context, data, cancellationToken);
        await SeedJournalAsync(context, data, cancellationToken);
        await SeedCalendarAsync(context, data, cancellationToken);
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

    /// <summary>
    /// Demo data is seeded only in Development and Testing. Every other environment name —
    /// Production, Staging, or anything an operator invents — refuses, and
    /// <c>Seed:DemoData=true</c> cannot override that. Inside the two allowed environments the
    /// flag still wins, so a developer can turn seeding off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An allow-list, not a Production deny-list (issue #457, item B4). The previous shape refused
    /// Production and then let an explicit flag win <em>anywhere else</em>, so
    /// <c>ASPNETCORE_ENVIRONMENT=Staging</c> plus <c>Seed:DemoData=true</c> — both things a
    /// self-hoster types — seeded four accounts, one of them Admin with lockout disabled, whose
    /// shared password is published in this repository's README. A deny-list has to enumerate
    /// every environment that must not seed; an allow-list enumerates the two that may, so a name
    /// nobody anticipated fails closed.
    /// </para>
    /// <para>
    /// The refusal is not redundant with <c>docker-compose.prod.yml</c> pinning
    /// <c>Seed__DemoData: "false"</c>. The base compose file defaults the flag to <c>true</c>, so
    /// anyone who runs without the prod overlay — or deploys the image from their own manifest —
    /// would otherwise seed.
    /// </para>
    /// </remarks>
    private bool IsEnabled()
    {
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            // Warn rather than skip silently: an operator who set the flag deliberately is owed the
            // reason it did nothing, and the line doubles as evidence that the gate held.
            if (configuration.GetValue<bool?>("Seed:DemoData") == true)
            {
                logger.LogWarning(
                    "Seed:DemoData=true was ignored: demo data seeds only in Development or Testing, "
                    + "and this host is running as {Environment}. The demo accounts share a password "
                    + "published in the README, so they are never seeded outside those environments.",
                    environment.EnvironmentName);
            }

            return false;
        }

        return configuration.GetValue<bool?>("Seed:DemoData") ?? true;
    }

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
        await context.Accounts.AddRangeAsync(data.Accounts, cancellationToken);
        await context.AccountEstimates.AddRangeAsync(data.AccountEstimates, cancellationToken);
        await context.AccountTerms.AddRangeAsync(data.AccountTerms, cancellationToken);
        await context.Budgets.AddRangeAsync(data.Budgets, cancellationToken);
        await context.BudgetItems.AddRangeAsync(data.BudgetItems, cancellationToken);
        await context.Transactions.AddRangeAsync(data.Transactions, cancellationToken);
        await context.TransactionTagLinks.AddRangeAsync(data.TransactionTagLinks, cancellationToken);
        await context.InsurancePolicies.AddRangeAsync(data.InsurancePolicies, cancellationToken);
        await context.PolicyRenewals.AddRangeAsync(data.PolicyRenewals, cancellationToken);
        await context.InsurancePolicyInsurers.AddRangeAsync(data.InsurancePolicyInsurers, cancellationToken);
        await context.InsurancePolicyInsuredAccounts.AddRangeAsync(data.InsurancePolicyInsuredAccounts, cancellationToken);
        await context.InsurancePolicyInsuredContacts.AddRangeAsync(data.InsurancePolicyInsuredContacts, cancellationToken);
        await context.InsurancePolicyBeneficiaries.AddRangeAsync(data.InsurancePolicyBeneficiaries, cancellationToken);
        await context.Contracts.AddRangeAsync(data.Contracts, cancellationToken);
        await context.ContractParties.AddRangeAsync(data.ContractParties, cancellationToken);
        await context.TaxStatements.AddRangeAsync(data.TaxStatements, cancellationToken);
        await context.TaxStatementTags.AddRangeAsync(data.TaxStatementTags, cancellationToken);
        await context.FileBlob.AddRangeAsync(data.FileBlobs, cancellationToken);
        await context.FileMetadata.AddRangeAsync(data.FileMetadata, cancellationToken);
        await context.TaxStatementFiles.AddRangeAsync(data.TaxStatementFiles, cancellationToken);
        await context.Subscriptions.AddRangeAsync(data.Subscriptions, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded {Accounts} accounts, {Budgets} budgets, {Transactions} transactions, {Policies} insurance policies, {Contracts} contracts, {Subscriptions} subscriptions.",
            data.Accounts.Count, data.Budgets.Count, data.Transactions.Count, data.InsurancePolicies.Count, data.Contracts.Count, data.Subscriptions.Count);
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

    private async Task SeedPhotosAsync(OdysseyContext context, DemoDataSet data, CancellationToken cancellationToken)
    {
        if (data.Photos.Count == 0)
        {
            return;
        }

        // Record-level idempotent insert (NOT an all-or-nothing sentinel). On an existing deployment the
        // journal photo unification backfill (§15 Phase B) has already created library Photos for the
        // legacy journal files, so a blanket "skip if any photo exists" would starve the DB of the
        // standalone demo photos, tags and albums (the bug this replaces). Insert only what's missing,
        // keyed on the natural keys, so a fresh DB, an already-backfilled DB and a re-run all converge to
        // the same demo set without ever colliding on the unique indexes (FileId, Name, the link pairs).
        var existingPhotoIds = (await context.Photos.Select(p => p.PhotoId).ToListAsync(cancellationToken)).ToHashSet();
        var existingFileIds = (await context.Photos.Select(p => p.FileId).ToListAsync(cancellationToken)).ToHashSet();
        var existingTagNames = (await context.PhotoTags.Select(t => t.Name).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingTagIds = (await context.PhotoTags.Select(t => t.PhotoTagId).ToListAsync(cancellationToken)).ToHashSet();
        var existingAlbumIds = (await context.PhotoAlbums.Select(a => a.PhotoAlbumId).ToListAsync(cancellationToken)).ToHashSet();
        var existingLinks = (await context.PhotoTagLinks.Select(l => new { l.PhotoId, l.PhotoTagId }).ToListAsync(cancellationToken))
            .Select(x => (x.PhotoId, x.PhotoTagId)).ToHashSet();
        var existingPeople = (await context.PhotoPeople.Select(pp => new { pp.PhotoId, pp.ContactId }).ToListAsync(cancellationToken))
            .Select(x => (x.PhotoId, x.ContactId)).ToHashSet();
        var existingItems = (await context.PhotoAlbumItems.Select(i => new { i.PhotoAlbumId, i.PhotoId }).ToListAsync(cancellationToken))
            .Select(x => (x.PhotoAlbumId, x.PhotoId)).ToHashSet();

        // The natural-key sets above are not enough on their own. Every link row also carries a
        // deterministic surrogate primary key, and that — not the pair — is what the database enforces.
        // On a database seeded by an OLDER generator the two can disagree: the generator's Landlord
        // contact id changed, so such a database holds a PhotoPerson with today's PhotoPersonId but
        // yesterday's ContactId. Keyed only on the pair, that row reads as "new" and the insert dies on
        // a duplicate primary key, failing the whole migrations job. Skip anything whose surrogate key
        // is already taken, whatever it now points at.
        var existingLinkIds = (await context.PhotoTagLinks.Select(l => l.PhotoTagLinkId).ToListAsync(cancellationToken)).ToHashSet();
        var existingPersonIds = (await context.PhotoPeople.Select(pp => pp.PhotoPersonId).ToListAsync(cancellationToken)).ToHashSet();
        var existingItemIds = (await context.PhotoAlbumItems.Select(i => i.PhotoAlbumItemId).ToListAsync(cancellationToken)).ToHashSet();

        // A demo photo already backfilled under a DIFFERENT (random) id: match on its file, not its id.
        var newTags = data.PhotoTags.Where(t => !existingTagNames.Contains(t.Name)).ToList();
        var newPhotos = data.Photos.Where(p => !existingFileIds.Contains(p.FileId)).ToList();
        var newAlbums = data.PhotoAlbums.Where(a => !existingAlbumIds.Contains(a.PhotoAlbumId)).ToList();

        var photoIdsPresent = existingPhotoIds.Concat(newPhotos.Select(p => p.PhotoId)).ToHashSet();
        var tagIdsPresent = existingTagIds.Concat(newTags.Select(t => t.PhotoTagId)).ToHashSet();
        var albumIdsPresent = existingAlbumIds.Concat(newAlbums.Select(a => a.PhotoAlbumId)).ToHashSet();

        // A new album's cover must reference a photo that will exist; null it otherwise (defensive).
        foreach (var album in newAlbums.Where(a => a.CoverPhotoId is { } cover && !photoIdsPresent.Contains(cover)))
        {
            album.CoverPhotoId = null;
        }

        var newLinks = data.PhotoTagLinks
            .Where(l => photoIdsPresent.Contains(l.PhotoId) && tagIdsPresent.Contains(l.PhotoTagId)
                        && !existingLinks.Contains((l.PhotoId, l.PhotoTagId))
                        && !existingLinkIds.Contains(l.PhotoTagLinkId)).ToList();
        var newPeople = data.PhotoPeople
            .Where(pp => photoIdsPresent.Contains(pp.PhotoId)
                         && !existingPeople.Contains((pp.PhotoId, pp.ContactId))
                         && !existingPersonIds.Contains(pp.PhotoPersonId)).ToList();
        var newItems = data.PhotoAlbumItems
            .Where(i => albumIdsPresent.Contains(i.PhotoAlbumId) && photoIdsPresent.Contains(i.PhotoId)
                        && !existingItems.Contains((i.PhotoAlbumId, i.PhotoId))
                        && !existingItemIds.Contains(i.PhotoAlbumItemId)).ToList();

        if (newTags.Count == 0 && newPhotos.Count == 0 && newAlbums.Count == 0
            && newLinks.Count == 0 && newPeople.Count == 0 && newItems.Count == 0)
        {
            logger.LogInformation("Demo photo data already present; skipping photo seed.");
            return;
        }

        // Tags + photos first (principals for links / album items / the album cover FK), then the join
        // rows — one SaveChanges, EF orders inserts by the in-context FK graph.
        await context.PhotoTags.AddRangeAsync(newTags, cancellationToken);
        await context.Photos.AddRangeAsync(newPhotos, cancellationToken);
        await context.PhotoAlbums.AddRangeAsync(newAlbums, cancellationToken);
        await context.PhotoTagLinks.AddRangeAsync(newLinks, cancellationToken);
        await context.PhotoPeople.AddRangeAsync(newPeople, cancellationToken);
        await context.PhotoAlbumItems.AddRangeAsync(newItems, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded {Photos} library photos, {Tags} photo tags, {Albums} albums.",
            newPhotos.Count, newTags.Count, newAlbums.Count);
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
        // children — all in one SaveChanges so EF orders the inserts by the FK graph. The contact, photo
        // and file references are real FKs whose principals the earlier seed steps committed.
        await context.JournalTags.AddRangeAsync(data.JournalTags, cancellationToken);
        await context.JournalTaskTags.AddRangeAsync(data.JournalTaskTags, cancellationToken);
        await context.JournalEntries.AddRangeAsync(data.JournalEntries, cancellationToken);
        await context.JournalEntryTags.AddRangeAsync(data.JournalEntryTags, cancellationToken);
        await context.JournalEntryContacts.AddRangeAsync(data.JournalEntryContacts, cancellationToken);
        await context.JournalEntryPhotos.AddRangeAsync(data.JournalEntryPhotos, cancellationToken);
        await context.JournalEntryAttachments.AddRangeAsync(data.JournalEntryAttachments, cancellationToken);
        await context.JournalTasks.AddRangeAsync(data.JournalTasks, cancellationToken);
        await context.JournalTaskTagLinks.AddRangeAsync(data.JournalTaskTagLinks, cancellationToken);
        await context.JournalTaskAttachments.AddRangeAsync(data.JournalTaskAttachments, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded {Entries} journal entries, {Tasks} tasks, {JournalTags} journal tags, {JournalTaskTags} task tags.",
            data.JournalEntries.Count, data.JournalTasks.Count, data.JournalTags.Count, data.JournalTaskTags.Count);
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
