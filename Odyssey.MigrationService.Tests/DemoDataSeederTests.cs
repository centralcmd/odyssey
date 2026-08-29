using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Legal;
using Odyssey.Dtos.Application;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.MigrationService.Tests;

public class DemoDataSeederTests
{
    [Fact]
    public async Task Seeds_users_with_roles_and_full_finance_dataset()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var finance = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var expected = DemoDataSet.Build();

        foreach (var demoUser in DemoUsers.All)
        {
            var user = await users.FindByEmailAsync(demoUser.Email);
            Assert.NotNull(user);
            Assert.True(user!.EmailConfirmed);
            Assert.Null(user.LockoutEnd);
            Assert.False(user.TwoFactorEnabled);
            // Never flagged (issue #290): the forced-change gate belongs to the seeded bootstrap
            // administrator alone, and flagging a demo user would break every E2E login.
            Assert.False(user.MustChangePassword);
            Assert.Contains(demoUser.Role, await users.GetRolesAsync(user));

            // The demo password actually authenticates. Not redundant with "a user row exists": the
            // seeder writes PasswordHash itself (via PasswordHasher) rather than through the
            // password-validating CreateAsync overload, because the documented demo password is shorter
            // than the policy the job applies to the bootstrap administrator (issue #290). That hand-off
            // is the one step where a hash could be written that nothing can verify — and the only other
            // tier that would notice is the browser E2E suite, which needs a running stack.
            Assert.True(
                await users.CheckPasswordAsync(user, demoUser.Password),
                $"The seeded demo password must sign {demoUser.Email} in.");
        }

        Assert.Equal(expected.Accounts.Count, await finance.Accounts.CountAsync());
        Assert.Equal(expected.AccountEstimates.Count, await finance.AccountEstimates.CountAsync());
        Assert.Equal(expected.AccountTerms.Count, await finance.AccountTerms.CountAsync());
        Assert.Equal(expected.Budgets.Count, await finance.Budgets.CountAsync());
        Assert.Equal(expected.Transactions.Count, await finance.Transactions.CountAsync());
        Assert.Equal(expected.TransactionTagLinks.Count, await finance.TransactionTagLinks.CountAsync());
        Assert.Equal(expected.InsurancePolicies.Count, await finance.InsurancePolicies.CountAsync());
        Assert.Equal(expected.PolicyRenewals.Count, await finance.PolicyRenewals.CountAsync());
        Assert.Equal(expected.TaxStatements.Count, await finance.TaxStatements.CountAsync());
        Assert.Equal(expected.TaxStatementTags.Count, await finance.TaxStatementTags.CountAsync());
        Assert.Equal(expected.TaxStatementFiles.Count, await finance.TaxStatementFiles.CountAsync());
        Assert.Equal(expected.FileMetadata.Count, await finance.FileMetadata.CountAsync());
        Assert.Equal(expected.FileBlobs.Count, await finance.FileBlob.CountAsync());

        // Every seeded contact has a non-null, unique ExternalUid (issue #338 §6, AC #9).
        // Contacts moved to OdysseyContext (issue #325 follow-up).
        var journalForContacts = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var externalUids = await journalForContacts.Contacts.Select(c => c.ExternalUid).ToListAsync();
        Assert.All(externalUids, uid => Assert.False(string.IsNullOrWhiteSpace(uid)));
        Assert.Equal(externalUids.Count, externalUids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Seeds_journal_module_dataset()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var expected = DemoDataSet.Build();

        Assert.Equal(expected.JournalTags.Count, await journal.JournalTags.CountAsync());
        Assert.Equal(expected.JournalTaskTags.Count, await journal.JournalTaskTags.CountAsync());
        Assert.Equal(expected.JournalEntries.Count, await journal.JournalEntries.CountAsync());
        Assert.Equal(expected.JournalEntryTags.Count, await journal.JournalEntryTags.CountAsync());
        Assert.Equal(expected.JournalEntryContacts.Count, await journal.JournalEntryContacts.CountAsync());
        Assert.Equal(expected.JournalEntryPhotos.Count, await journal.JournalEntryPhotos.CountAsync());
        Assert.Equal(expected.JournalEntryAttachments.Count, await journal.JournalEntryAttachments.CountAsync());
        Assert.Equal(expected.JournalTasks.Count, await journal.JournalTasks.CountAsync());
        Assert.Equal(expected.JournalTaskTagLinks.Count, await journal.JournalTaskTagLinks.CountAsync());
        Assert.Equal(expected.JournalTaskAttachments.Count, await journal.JournalTaskAttachments.CountAsync());

        // A journal entry with at least one contact, photo and attachment exists (rich example).
        Assert.True(await journal.JournalEntries.AnyAsync(), "expected seeded journal entries");
        Assert.NotEmpty(expected.JournalEntryPhotos);
        Assert.NotEmpty(expected.JournalEntryContacts);

        // Every seeded task carries a non-null, unique ExternalUid (issue #337 §6 / AC #18) so the
        // VTODO export has a stable UID for every row and idempotent re-import matches by it.
        var taskUids = await journal.JournalTasks.Select(t => t.ExternalUid).ToListAsync();
        Assert.All(taskUids, uid => Assert.False(string.IsNullOrWhiteSpace(uid)));
        Assert.Equal(taskUids.Count, taskUids.Distinct().Count());

        // Likewise, every seeded journal entry has a non-null, unique, per-row ExternalUid (issue #339
        // §6 / AC #9) — the anchor the VJOURNAL export/import round-trips on.
        var entryUids = await journal.JournalEntries.Select(e => e.ExternalUid).ToListAsync();
        Assert.All(entryUids, uid => Assert.False(string.IsNullOrWhiteSpace(uid)));
        Assert.Equal(entryUids.Count, entryUids.Distinct().Count());
    }

    [Fact]
    public async Task Seeds_photos_module_dataset()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var photos = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var expected = DemoDataSet.Build();

        Assert.Equal(expected.Photos.Count, await photos.Photos.CountAsync());
        Assert.Equal(expected.PhotoTags.Count, await photos.PhotoTags.CountAsync());
        Assert.Equal(expected.PhotoTagLinks.Count, await photos.PhotoTagLinks.CountAsync());
        Assert.Equal(expected.PhotoPeople.Count, await photos.PhotoPeople.CountAsync());
        Assert.Equal(expected.PhotoAlbums.Count, await photos.PhotoAlbums.CountAsync());
        Assert.Equal(expected.PhotoAlbumItems.Count, await photos.PhotoAlbumItems.CountAsync());
    }

    /// <summary>
    /// A legacy database whose photo link rows were written by an <em>older</em> generator: the
    /// deterministic surrogate key is the same, but the natural key it points at has since changed.
    /// </summary>
    /// <remarks>
    /// Found in the wild. The demo generator's Landlord contact id changed, so a database seeded before
    /// that carries a <c>PhotoPerson</c> with today's <c>PhotoPersonId</c> but yesterday's
    /// <c>ContactId</c>. The seed's guards were keyed on the natural pair while the table's uniqueness
    /// is on the surrogate key, so the row read as "new" and the insert died on a duplicate primary key
    /// — taking the whole migrations job down with it. The same shape applies to
    /// <c>PhotoTagLinks</c> and <c>PhotoAlbumItems</c>, which is why all three are asserted here.
    /// </remarks>
    [Fact]
    public async Task Reseeds_over_link_rows_whose_surrogate_key_survived_a_changed_natural_key()
    {
        await using var provider = BuildProvider(out var seeder);
        await seeder.ExecuteAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var journal = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

            // Repoint each link row's natural key while keeping its primary key — exactly what an
            // older generation of the generator leaves behind.
            foreach (var person in journal.PhotoPeople)
            {
                person.ContactId = Guid.NewGuid();
            }

            foreach (var link in journal.PhotoTagLinks)
            {
                link.PhotoTagId = Guid.NewGuid();
            }

            foreach (var item in journal.PhotoAlbumItems)
            {
                item.PhotoId = Guid.NewGuid();
            }

            await journal.SaveChangesAsync();
        }

        var expected = DemoDataSet.Build();

        // The whole assertion is "this does not throw": a duplicate-key insert here fails the
        // migrations job, and since issue #290 that keeps the API down behind it.
        await seeder.ExecuteAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var journal = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

            // No row is duplicated: the surrogate keys were already taken, so nothing is re-inserted.
            Assert.Equal(expected.PhotoPeople.Count, await journal.PhotoPeople.CountAsync());
            Assert.Equal(expected.PhotoTagLinks.Count, await journal.PhotoTagLinks.CountAsync());
            Assert.Equal(expected.PhotoAlbumItems.Count, await journal.PhotoAlbumItems.CountAsync());
        }
    }

    // Reproduces the existing-deployment case: the journal unification backfill has created library
    // Photos for the legacy journal files, so only those exist. The record-level idempotent seed must
    // still add the standalone demo photos/tags/albums (not skip the whole seed).
    [Fact]
    public async Task Reseeds_standalone_photo_data_when_only_journal_photos_exist()
    {
        await using var provider = BuildProvider(out var seeder);
        await seeder.ExecuteAsync(CancellationToken.None);
        var expected = DemoDataSet.Build();

        using (var scope = provider.CreateScope())
        {
            var photos = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var journal = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var journalPhotoIds = await journal.JournalEntryPhotos.Select(p => p.PhotoId).ToListAsync();

            // Drop everything except the journal-linked photos (simulating a backfill-only library).
            photos.PhotoAlbumItems.RemoveRange(photos.PhotoAlbumItems);
            photos.PhotoTagLinks.RemoveRange(photos.PhotoTagLinks);
            photos.PhotoPeople.RemoveRange(photos.PhotoPeople);
            photos.PhotoAlbums.RemoveRange(photos.PhotoAlbums);
            photos.PhotoTags.RemoveRange(photos.PhotoTags);
            photos.Photos.RemoveRange(photos.Photos.Where(p => !journalPhotoIds.Contains(p.PhotoId)));
            await photos.SaveChangesAsync();
            Assert.Equal(journalPhotoIds.Distinct().Count(), await photos.Photos.CountAsync());
            Assert.Equal(0, await photos.PhotoTags.CountAsync());
        }

        await seeder.ExecuteAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var photos = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            Assert.Equal(expected.Photos.Count, await photos.Photos.CountAsync());
            Assert.Equal(expected.PhotoTags.Count, await photos.PhotoTags.CountAsync());
            Assert.Equal(expected.PhotoAlbums.Count, await photos.PhotoAlbums.CountAsync());
        }
    }

    [Fact]
    public async Task Seeds_complete_profile_for_each_demo_user()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var app = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        Assert.Equal(DemoUsers.All.Count, await app.UserProfiles.CountAsync());

        foreach (var demoUser in DemoUsers.All)
        {
            var user = await users.FindByEmailAsync(demoUser.Email);
            Assert.NotNull(user);
            var profile = await app.UserProfiles.SingleAsync(p => p.UserId == user!.Id);

            Assert.Equal(demoUser.FirstName, profile.FirstName);
            Assert.Equal(demoUser.LastName, profile.LastName);
            Assert.Equal(demoUser.DisplayName, profile.DisplayName);
            Assert.Equal(demoUser.BirthDate, profile.BirthDate);
            Assert.Equal((Sex)demoUser.Sex, profile.Sex);

            // A seeded profile is complete, so the seeded login skips the onboarding gate.
            Assert.False(string.IsNullOrWhiteSpace(profile.FirstName));
            Assert.False(string.IsNullOrWhiteSpace(profile.LastName));
            Assert.NotNull(profile.BirthDate);
            Assert.NotNull(profile.Sex);
        }
    }

    [Fact]
    public async Task Is_idempotent_across_repeated_runs()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);
        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var finance = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var app = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        Assert.Equal(DemoUsers.All.Count, await users.Users.CountAsync());
        // The profile seed is guarded (one per user), so a second run adds no duplicate rows.
        Assert.Equal(DemoUsers.All.Count, await app.UserProfiles.CountAsync());
        // Likewise the legal seed: one ToS version, and one response per user per artefact.
        Assert.Equal(1, await app.TermsOfServiceVersions.CountAsync());
        Assert.Equal(DemoUsers.All.Count, await app.LicenseAcceptances.CountAsync());
        Assert.Equal(DemoUsers.All.Count, await app.TermsOfServiceAcceptances.CountAsync());
        Assert.Equal(DemoDataSet.Build().Transactions.Count, await finance.Transactions.CountAsync());
    }

    [Fact]
    public async Task Seeds_legal_acceptances_that_satisfy_the_compliance_rule_for_every_demo_user()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var app = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var version = await app.TermsOfServiceVersions.SingleAsync();
        var licenseHash = new LicenseDocumentProvider(AppContext.BaseDirectory).Get().Sha256;

        foreach (var demoUser in DemoUsers.All)
        {
            Assert.True(
                await app.LicenseAcceptances.AnyAsync(row =>
                    row.UserId == demoUser.Id && row.LicenseHash == licenseHash && row.Accepted),
                $"{demoUser.Email} must have accepted the current LICENSE, or the gate 451s every call.");

            Assert.True(
                await app.TermsOfServiceAcceptances.AnyAsync(row =>
                    row.UserId == demoUser.Id && row.TermsOfServiceVersionId == version.Id && row.Accepted),
                $"{demoUser.Email} must have accepted the current ToS version.");
        }
    }

    /// <summary>
    /// The regression behind the #417 demo-email rename: renaming a login changes its email-derived id,
    /// so a re-seed of an existing database creates a new user. The legal seed used to guard on "a ToS
    /// version exists" and skip wholesale, leaving that user with no acceptance rows — and every
    /// authenticated call from the seeded login answered with a 451 by the compliance gate.
    /// </summary>
    [Fact]
    public async Task Seeds_legal_acceptances_for_a_user_added_after_the_terms_were_already_published()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            // Stand in for the renamed demo logins: drop the acceptances the first run wrote, keeping
            // the published ToS version — exactly the state an already-seeded database was left in.
            var app = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            app.LicenseAcceptances.RemoveRange(app.LicenseAcceptances);
            app.TermsOfServiceAcceptances.RemoveRange(app.TermsOfServiceAcceptances);
            await app.SaveChangesAsync();
        }

        await seeder.ExecuteAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var app = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

            // Still one version — republishing would re-gate everyone rather than repair them.
            Assert.Equal(1, await app.TermsOfServiceVersions.CountAsync());
            Assert.Equal(DemoUsers.All.Count, await app.LicenseAcceptances.CountAsync());
            Assert.Equal(DemoUsers.All.Count, await app.TermsOfServiceAcceptances.CountAsync());
        }
    }

    /// <summary>
    /// A decline made through the UI must survive re-seeding. It is also the case a naive "top up any
    /// non-compliant user" repair would get wrong forever: the seeded timestamp is a fixed past date, so
    /// an acceptance written over a live decline never becomes the most recent response, and every
    /// restart would append another dead row.
    /// </summary>
    [Fact]
    public async Task Leaves_a_users_own_decline_standing_and_adds_no_row_for_it()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);

        var decliner = DemoUsers.All[0];

        using (var scope = provider.CreateScope())
        {
            var app = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var version = await app.TermsOfServiceVersions.SingleAsync();
            app.TermsOfServiceAcceptances.Add(new TermsOfServiceAcceptance
            {
                UserId = decliner.Id,
                TermsOfServiceVersionId = version.Id,
                Accepted = false,
                RespondedAt = DateTime.UtcNow,
            });
            await app.SaveChangesAsync();
        }

        await seeder.ExecuteAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var app = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

            // The seed's own row plus the decline, and nothing appended on top of them.
            Assert.Equal(
                DemoUsers.All.Count + 1,
                await app.TermsOfServiceAcceptances.CountAsync());
            Assert.False(
                await app.TermsOfServiceAcceptances
                    .Where(row => row.UserId == decliner.Id)
                    .OrderByDescending(row => row.RespondedAt)
                    .ThenByDescending(row => row.Id)
                    .Select(row => row.Accepted)
                    .FirstAsync());
        }
    }

    [Fact]
    public async Task Does_not_seed_when_disabled()
    {
        await using var provider = BuildProvider(out var seeder, seedEnabled: false);

        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var finance = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        Assert.Equal(0, await users.Users.CountAsync());
        Assert.Equal(0, await finance.Accounts.CountAsync());
    }

    // The environment gate (issue #457, item B4). Demo data seeds only in Development and Testing;
    // the four demo accounts include an Admin with lockout disabled whose shared password is
    // published in the README, so an explicit Seed:DemoData=true must not be able to reach any
    // other environment. The "Staging" case is the one the audit named: a plausible thing for a
    // self-hoster to type, which under the previous Production-only deny-list did seed.
    [Theory]
    [InlineData("Development", null, true)]
    [InlineData("Testing", null, true)]
    [InlineData("Development", "true", true)]
    [InlineData("Testing", "true", true)]
    [InlineData("Development", "false", false)]
    [InlineData("Testing", "false", false)]
    [InlineData("Staging", "true", false)]
    [InlineData("Staging", null, false)]
    [InlineData("Production", "true", false)]
    [InlineData("Production", null, false)]
    [InlineData("QA", "true", false)]
    [InlineData("", "true", false)]
    public async Task Seeds_only_in_development_or_testing(string environmentName, string? flag, bool expectSeeded)
    {
        await using var provider = BuildProvider(out var seeder, flag, environmentName);

        await seeder.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var seeded = await users.Users.CountAsync() > 0;
        Assert.Equal(expectSeeded, seeded);
    }

    // The warning fires on exactly one condition: an explicit Seed:DemoData=true that the environment
    // gate then refused. An operator who set the flag deliberately should not be left wondering why
    // nothing happened, and the line is the record that the gate held.
    //
    // Both halves are asserted, because the negative half is where the interesting mutants live. A
    // condition of `!= false` rather than `== true` passes every outcome assertion above, and would
    // announce "Seed:DemoData=true was ignored" on any host that simply never set the flag — which is
    // most of them, and is the one message guaranteed to send an operator hunting for a setting they
    // do not have. The allowed-environment rows cover the other direction: choosing not to seed in
    // Development is a normal choice, not a refused override, and must stay silent.
    [Theory]
    // Refused override — the only case that warns.
    [InlineData("Staging", "true", true)]
    [InlineData("Production", "true", true)]
    [InlineData("QA", "true", true)]
    [InlineData("", "true", true)]
    // Disallowed environment, but nothing was overridden: the flag is absent or already false.
    [InlineData("Staging", null, false)]
    [InlineData("Staging", "false", false)]
    [InlineData("Production", null, false)]
    [InlineData("QA", "false", false)]
    // Allowed environments never warn, whichever way the flag is set.
    [InlineData("Development", null, false)]
    [InlineData("Development", "true", false)]
    [InlineData("Development", "false", false)]
    [InlineData("Testing", null, false)]
    [InlineData("Testing", "true", false)]
    [InlineData("Testing", "false", false)]
    public async Task Warns_only_when_an_explicit_flag_was_refused_by_the_environment_gate(
        string environmentName, string? flag, bool expectWarning)
    {
        var messages = new List<string>();
        await using var provider = BuildProvider(
            out var seeder, flag, environmentName, capturedWarnings: messages);

        await seeder.ExecuteAsync(CancellationToken.None);

        if (!expectWarning)
        {
            Assert.Empty(messages);
            return;
        }

        var warning = Assert.Single(messages);
        Assert.Contains("Seed:DemoData=true was ignored", warning);
        // The environment name is what tells the operator which host refused; "" has none to name.
        if (environmentName.Length > 0)
        {
            Assert.Contains(environmentName, warning);
        }
    }

    private static ServiceProvider BuildProvider(out DemoDataSeeder seeder, bool seedEnabled = true)
    {
        var provider = MigrationServiceTestHost.Build();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Seed:DemoData"] = seedEnabled ? "true" : "false" })
            .Build();

        var logger = provider.GetRequiredService<ILogger<DemoDataSeeder>>();
        seeder = new DemoDataSeeder(provider, configuration, new TestHostEnvironment(), logger);
        return provider;
    }

    private static ServiceProvider BuildProvider(
        out DemoDataSeeder seeder,
        string? flag,
        string environmentName,
        List<string>? capturedWarnings = null)
    {
        var provider = MigrationServiceTestHost.Build();

        // A null flag means the key is absent, which is not the same as "false": inside an allowed
        // environment the default is on.
        var settings = new Dictionary<string, string?>();
        if (flag is not null)
        {
            settings["Seed:DemoData"] = flag;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ILogger<DemoDataSeeder> logger = capturedWarnings is null
            ? provider.GetRequiredService<ILogger<DemoDataSeeder>>()
            : new WarningCapturingLogger(capturedWarnings);

        seeder = new DemoDataSeeder(
            provider, configuration, new TestHostEnvironment { EnvironmentName = environmentName }, logger);
        return provider;
    }

    private sealed class WarningCapturingLogger(List<string> warnings) : ILogger<DemoDataSeeder>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Odyssey.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
