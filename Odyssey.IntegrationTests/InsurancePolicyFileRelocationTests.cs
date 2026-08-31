// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// formatted DateTime this test generated itself — there is no external input — and the target table has
// no entity type to write through, which is the change under test. Parameterising would not make the
// statements any safer, only harder to read against the migration SQL they mirror.
#pragma warning disable EF1002

using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The three migrations that make a renewal period the only home for an insurance document
/// (issue #26): the ledger, the relocation, and the drop.
///
/// <para>
/// Every assertion here has to run against real MariaDB. The relocation is hand-written SQL, its
/// assertion is a <c>CHECK</c> on a temporary table, its safety rests on which statements do and do not
/// implicitly commit, and its <c>Down</c> depends on foreign keys — none of which the EF InMemory
/// provider represents. The <c>migrate to N−1 → seed → migrate to head</c> seam these use is
/// <see cref="MigrationSeam"/>.
/// </para>
/// </summary>
[Collection(MariaDbCollection.Name)]
public class InsurancePolicyFileRelocationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_insurance_relocation";

    /// <summary>The migration immediately before the ledger — the last point at which
    /// <c>InsurancePolicyFiles</c> exists and holds the state to be relocated.</summary>
    private const string Baseline = "_AddEmailTransportSettings";

    private const string Ledger = "_AddInsurancePolicyFileRelocationLedger";
    private const string Relocation = "_MoveInsurancePolicyFilesToRenewals";
    private const string Drop = "_DropInsurancePolicyFiles";

    /// <summary>The scaffold-time literal the relocation pins its placeholder periods to. Asserted by
    /// value here, which is the whole reason it is a literal rather than an apply-time clock.</summary>
    private static readonly DateTime Pinned = new(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc);

    private const string User = "integration-user";

    // ── Relocation ──────────────────────────────────────────────────────────────

    /// <summary>
    /// §16.1 / §16.2: documents land on the period with the EARLIEST FromDate, field for field, and the
    /// original id survives in the ledger.
    /// </summary>
    [SkippableFact]
    public async Task Documents_move_to_the_earliest_period_preserving_every_field()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var policy = Guid.NewGuid();
        var earliest = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var latest = Guid.NewGuid();
        var fileA = Guid.NewGuid();
        var fileB = Guid.NewGuid();
        var sourceA = Guid.NewGuid();
        var sourceB = Guid.NewGuid();
        var effective = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedPrincipalsAsync(context, fileA, fileB);
                await SeedPolicyAsync(context, policy, "Home cover");

                // Deliberately inserted newest-first, so a relocation that took "the first row it saw"
                // rather than the earliest FromDate would pass by accident.
                await AddPeriodAsync(context, latest, policy, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                await AddPeriodAsync(context, middle, policy, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                await AddPeriodAsync(context, earliest, policy, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                // One row with a NULL EffectiveDate and a NULL attribution, one with both set: the two
                // columns the assertion has to compare NULL-safely.
                await AddPolicyFileAsync(context, sourceA, policy, fileA, fileType: 1, effectiveDate: null, attachedBy: null);
                await AddPolicyFileAsync(context, sourceB, policy, fileB, fileType: 3, effectiveDate: effective, attachedBy: User);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                var landed = await context.PolicyRenewalFiles
                    .Where(f => f.PolicyRenewalId == earliest)
                    .OrderBy(f => f.FileMetadataId)
                    .ToListAsync();
                Assert.Equal(2, landed.Count);
                Assert.Equal(0, await context.PolicyRenewalFiles.CountAsync(f => f.PolicyRenewalId == middle));
                Assert.Equal(0, await context.PolicyRenewalFiles.CountAsync(f => f.PolicyRenewalId == latest));

                var a = landed.Single(f => f.FileMetadataId == fileA);
                Assert.Equal(PolicyFileType.Invoice, a.FileType);
                Assert.Null(a.EffectiveDate);
                Assert.Null(a.AttachedByUserId);

                var b = landed.Single(f => f.FileMetadataId == fileB);
                Assert.Equal(PolicyFileType.PolicyDocument, b.FileType);
                Assert.Equal(effective, b.EffectiveDate);
                Assert.Equal(User, b.AttachedByUserId);

                // A fresh id on the destination row; the original is kept in the ledger.
                Assert.DoesNotContain(landed, f => f.Id == sourceA || f.Id == sourceB);

                var ledger = await MigrationSeam.RowAsync(context,
                    $"SELECT * FROM `_InsurancePolicyFileRelocation` WHERE SourceId = '{sourceB}'");
                Assert.NotNull(ledger);
                Assert.Equal("Relocated", ledger!["Outcome"]);
                Assert.Equal(b.Id, ToGuid(ledger["DestinationPolicyRenewalFileId"]));
                Assert.Equal(earliest, ToGuid(ledger["DestinationPolicyRenewalId"]));
                Assert.Equal(effective, (DateTime)ledger["EffectiveDate"]!);
                Assert.Equal(User, ledger["AttachedByUserId"]);
                Assert.False(Convert.ToBoolean(ledger["PlaceholderPeriodCreated"]));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// §16.3 / §16.4: a policy holding documents but no period gets exactly one placeholder, pinned to
    /// the scaffold literal; a policy with no periods and no documents gets nothing. The second half is
    /// what stops the migration from fabricating a period for every empty policy in the database.
    /// </summary>
    [SkippableFact]
    public async Task A_policy_with_documents_but_no_period_gets_one_placeholder_and_an_empty_one_gets_none()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var orphan = Guid.NewGuid();
        var barren = Guid.NewGuid();
        var fileA = Guid.NewGuid();
        var fileB = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedPrincipalsAsync(context, fileA, fileB);
                await SeedPolicyAsync(context, orphan, "Orphaned paperwork");
                await SeedPolicyAsync(context, barren, "Nothing at all");

                await AddPolicyFileAsync(context, Guid.NewGuid(), orphan, fileA);
                await AddPolicyFileAsync(context, Guid.NewGuid(), orphan, fileB);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                var placeholder = Assert.Single(await context.PolicyRenewals
                    .Where(r => r.InsurancePolicyId == orphan)
                    .ToListAsync());

                Assert.Equal(Pinned, placeholder.FromDate);
                Assert.Equal(Pinned, placeholder.ToDate);
                Assert.Equal(Pinned, placeholder.CreatedAtUtc);
                Assert.Equal(0m, placeholder.Premium);
                Assert.Equal(0m, placeholder.CoverageAmount);
                Assert.Equal("USD", placeholder.PremiumCurrencyCode);
                Assert.Equal("USD", placeholder.CoverageCurrencyCode);

                // The note is user-editable, so it is documentation rather than an identifier — but it
                // has to say where the period came from, and how many documents it exists to hold.
                Assert.Contains("Auto-created during migration", placeholder.Notes);
                Assert.Contains("preserve 2 document(s)", placeholder.Notes);

                Assert.Equal(2, await context.PolicyRenewalFiles
                    .CountAsync(f => f.PolicyRenewalId == placeholder.PolicyRenewalId));

                // Structural identity, which is what Down and these criteria key on.
                Assert.Equal(2, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `_InsurancePolicyFileRelocation` WHERE PlaceholderPeriodCreated = 1"));

                Assert.Equal(0, await context.PolicyRenewals.CountAsync(r => r.InsurancePolicyId == barren));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// §16.5: a file already attached to the destination period is skipped, not duplicated — the
    /// destination row the user chose deliberately keeps its own type, effective date and attribution,
    /// and its <c>AttachedAtUtc</c> is untouched.
    /// </summary>
    [SkippableFact]
    public async Task A_file_attached_to_both_the_policy_and_its_first_period_is_skipped()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var policy = Guid.NewGuid();
        var period = Guid.NewGuid();
        var file = Guid.NewGuid();
        var source = Guid.NewGuid();
        var chosen = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedPrincipalsAsync(context, file);
                await SeedPolicyAsync(context, policy, "Doubly filed");
                await AddPeriodAsync(context, period, policy, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                context.PolicyRenewalFiles.Add(new PolicyRenewalFile
                {
                    Id = Guid.NewGuid(),
                    PolicyRenewalId = period,
                    FileMetadataId = file,
                    FileType = PolicyFileType.Invoice,
                    AttachedByUserId = User,
                    AttachedAtUtc = chosen,
                });
                await context.SaveChangesAsync();

                await AddPolicyFileAsync(context, source, policy, file, fileType: 5);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                var surviving = Assert.Single(await context.PolicyRenewalFiles
                    .Where(f => f.PolicyRenewalId == period && f.FileMetadataId == file)
                    .ToListAsync());
                Assert.Equal(PolicyFileType.Invoice, surviving.FileType);
                Assert.Equal(chosen, surviving.AttachedAtUtc);

                var ledger = await MigrationSeam.RowAsync(context,
                    $"SELECT * FROM `_InsurancePolicyFileRelocation` WHERE SourceId = '{source}'");
                Assert.Equal("SkippedDuplicate", ledger!["Outcome"]);

                // Null, not the pre-existing row's id: Down must restore the source row without
                // deleting a destination row this migration did not create.
                Assert.Null(ledger["DestinationPolicyRenewalFileId"]);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// §16.7: the source table is gone after the drop, and the ledger holds exactly one row per
    /// pre-migration source row.
    /// </summary>
    [SkippableFact]
    public async Task The_source_table_is_dropped_and_the_ledger_holds_one_row_per_source_row()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var policy = Guid.NewGuid();
        var files = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedPrincipalsAsync(context, files);
                await SeedPolicyAsync(context, policy, "Counted");
                await AddPeriodAsync(context, Guid.NewGuid(), policy, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                foreach (var file in files)
                {
                    await AddPolicyFileAsync(context, Guid.NewGuid(), policy, file);
                }
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                Assert.False(await MigrationSeam.TableExistsAsync(context, "InsurancePolicyFiles"));
                Assert.Equal(files.Length, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `_InsurancePolicyFileRelocation`"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Safety: the controls, not the happy path ────────────────────────────────

    /// <summary>
    /// §16.6 / §16.8 / §16.9. The one criterion that matters most: the assertion is what stands between
    /// a mis-copy and an irreversible <c>DROP TABLE</c>.
    ///
    /// <para>
    /// The failure is induced by pre-seeding the ledger with a row whose <c>EffectiveDate</c> is
    /// <c>NULL</c> where the source's is set. That is reachable precisely because the ledger insert is
    /// guarded on <c>SourceId NOT EXISTS</c> — the replay guard — so the migration leaves the wrong row
    /// alone and the value check has something to catch. A <c>=</c> comparison on that nullable column
    /// would yield <c>NULL</c>, which is not <c>TRUE</c>, and the row would pass; this test failing is
    /// what proves <c>&lt;=&gt;</c> was used.
    /// </para>
    ///
    /// <para>
    /// It then asserts what the failure actually leaves behind, rather than assuming it. Migration B
    /// contains no DDL, so its writes are expected to roll back with the transaction: no placeholder,
    /// no relocated row, no ledger row of its own, the source table intact, and the drop unrun. An
    /// earlier draft of the spec asserted the opposite — a claim that had been true before the ledger
    /// moved into its own migration and was carried past the change that invalidated it.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_mis_copied_nullable_field_fails_the_assertion_and_rolls_the_relocation_back()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var policy = Guid.NewGuid();
        var period = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var file = Guid.NewGuid();
        var orphanFile = Guid.NewGuid();
        var source = Guid.NewGuid();
        var effective = new DateTime(2025, 2, 2, 0, 0, 0, DateTimeKind.Utc);

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedPrincipalsAsync(context, file, orphanFile);
                await SeedPolicyAsync(context, policy, "Mis-copied");
                await AddPeriodAsync(context, period, policy, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                await AddPolicyFileAsync(context, source, policy, file, effectiveDate: effective);

                // A second policy with no period, so the failure has placeholder work to roll back too.
                await SeedPolicyAsync(context, orphan, "Would gain a placeholder");
                await AddPolicyFileAsync(context, Guid.NewGuid(), orphan, orphanFile);

                // Ledger only — the relocation must be the thing that fails, not this.
                await MigrationSeam.MigrateToAsync(context, Ledger);
                await context.Database.ExecuteSqlRawAsync($"""
                    INSERT INTO `_InsurancePolicyFileRelocation`
                        (`SourceId`, `InsurancePolicyId`, `FileMetadataId`, `FileType`, `EffectiveDate`,
                         `AttachedByUserId`, `AttachedAtUtc`, `DestinationPolicyRenewalId`,
                         `DestinationPolicyRenewalFileId`, `Outcome`, `PlaceholderPeriodCreated`, `MigratedAtUtc`)
                    VALUES ('{source}', '{policy}', '{file}', 1, NULL, NULL, '2025-01-01 00:00:00',
                            '{period}', NULL, 'Relocated', 0, '2025-01-01 00:00:00');
                    """);
            }

            await using (var context = NewContext())
            {
                var failure = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.MigrateAsync());

                // Error 4025 (23000) — a CHECK constraint violation, read off the exception rather than
                // matched in its text. Asserted by code rather than by the unassertable claim that it
                // "is not classified transient": it is absent from Pomelo's transient allow-list, so
                // the execution strategy does not retry it.
                var mysql = Assert.IsAssignableFrom<MySqlException>(
                    Unwrap(failure).FirstOrDefault(e => e is MySqlException)
                    ?? throw new Xunit.Sdk.XunitException($"No MySqlException in: {Flatten(failure)}"));
                Assert.Equal(4025, mysql.Number);
                Assert.Contains("_assert", mysql.Message, StringComparison.Ordinal);
            }

            await using (var context = NewContext())
            {
                // The source is untouched: this is the whole point of asserting before the drop.
                Assert.True(await MigrationSeam.TableExistsAsync(context, "InsurancePolicyFiles"));
                Assert.Equal(2, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `InsurancePolicyFiles`"));

                Assert.False(await MigrationSeam.HasRunAsync(context, Relocation));
                Assert.False(await MigrationSeam.HasRunAsync(context, Drop));

                // Rolled back, not merely halted — the observed state, not the assumed one.
                Assert.Equal(0, await context.PolicyRenewals.CountAsync(r => r.InsurancePolicyId == orphan));
                Assert.Equal(0, await context.PolicyRenewalFiles.CountAsync(f => f.PolicyRenewalId == period));
                Assert.Equal(1, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `_InsurancePolicyFileRelocation`"));
            }

            // Corrected: remove the bad ledger row and re-run. It completes from the original state.
            await using (var context = NewContext())
            {
                await context.Database.ExecuteSqlRawAsync("DELETE FROM `_InsurancePolicyFileRelocation`");
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                Assert.False(await MigrationSeam.TableExistsAsync(context, "InsurancePolicyFiles"));
                Assert.Equal(1, await context.PolicyRenewalFiles.CountAsync(f => f.PolicyRenewalId == period));
                Assert.Equal(1, await context.PolicyRenewals.CountAsync(r => r.InsurancePolicyId == orphan));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// §16.10: replaying the relocation is a no-op. The execution strategy replays a migration in-process
    /// on a transient connection failure, so this is a real path, not a hypothetical one.
    ///
    /// <para>
    /// The <c>Outcome</c> assertion is the subtle half. Without the <c>SourceId NOT EXISTS</c> guard, a
    /// replay would see the destination rows its own first pass inserted, take the duplicate branch and
    /// rewrite a <c>Relocated</c> row as <c>SkippedDuplicate</c> — which would strand it past
    /// <c>Down</c>, since <c>Down</c> keys on that column.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Replaying_the_relocation_changes_nothing_and_never_reclassifies_an_outcome()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var policy = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var file = Guid.NewGuid();
        var orphanFile = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedPrincipalsAsync(context, file, orphanFile);
                await SeedPolicyAsync(context, policy, "Replayed");
                await AddPeriodAsync(context, Guid.NewGuid(), policy, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                await AddPolicyFileAsync(context, Guid.NewGuid(), policy, file);
                await SeedPolicyAsync(context, orphan, "Replayed orphan");
                await AddPolicyFileAsync(context, Guid.NewGuid(), orphan, orphanFile);

                // Stop at the relocation: the drop must not run, or the replay could not be attempted.
                await MigrationSeam.MigrateToAsync(context, Relocation);
            }

            (long Files, long Periods, long Ledger, long Relocated) before;
            await using (var context = NewContext())
            {
                before = await SnapshotAsync(context);
                await MigrationSeam.ForgetAsync(context, Relocation);
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Relocation);
                Assert.Equal(before, await SnapshotAsync(context));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// §16.11: replaying the drop after the table is gone is a no-op. This is why the drop is its own
    /// migration — the relocation's history row commits before it runs, so the relocation can never be
    /// replayed against a dropped table and needs no conditional guard of its own.
    /// </summary>
    [SkippableFact]
    public async Task Replaying_the_drop_against_an_already_dropped_table_is_a_no_op()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                Assert.False(await MigrationSeam.TableExistsAsync(context, "InsurancePolicyFiles"));
                await MigrationSeam.ForgetAsync(context, Drop);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                Assert.False(await MigrationSeam.TableExistsAsync(context, "InsurancePolicyFiles"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// §16.12: <c>Down</c> reconstructs the source table from the ledger and touches nothing it did not
    /// create.
    ///
    /// <para>
    /// The four things it must get right are all seeded here: a relocated row restores with every column
    /// equal to the original; a <c>SkippedDuplicate</c> restores too, and its pre-existing destination row
    /// survives; a document attached to the first period BEFORE the migration is left alone (an earlier
    /// design deleted it, because a heuristic <c>Down</c> keyed on the destination period plus
    /// <c>AttachedAtUtc</c> cannot tell it apart from a relocated row); and a same-file document
    /// re-attached AFTER the migration survives, which is what deleting by row id rather than by
    /// <c>(period, file)</c> buys.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Down_restores_the_source_rows_and_leaves_everything_else_alone()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var policy = Guid.NewGuid();
        var period = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var relocatedFile = Guid.NewGuid();
        var duplicateFile = Guid.NewGuid();
        var untouchedFile = Guid.NewGuid();
        var orphanFile = Guid.NewGuid();
        var relocatedSource = Guid.NewGuid();
        var duplicateSource = Guid.NewGuid();
        var effective = new DateTime(2025, 9, 9, 0, 0, 0, DateTimeKind.Utc);
        var attachedAt = new DateTime(2024, 5, 5, 6, 7, 8, DateTimeKind.Utc);
        var untouchedId = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedPrincipalsAsync(context, relocatedFile, duplicateFile, untouchedFile, orphanFile);
                await SeedPolicyAsync(context, policy, "Reversible");
                await AddPeriodAsync(context, period, policy, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                // Deliberately attached to the period before the migration ran, and to the SAME period
                // the relocation targets — the row a heuristic Down would have stolen.
                context.PolicyRenewalFiles.Add(new PolicyRenewalFile
                {
                    Id = untouchedId,
                    PolicyRenewalId = period,
                    FileMetadataId = untouchedFile,
                    FileType = PolicyFileType.Invoice,
                    AttachedByUserId = User,
                    AttachedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                // And the duplicate's own pre-existing destination row.
                context.PolicyRenewalFiles.Add(new PolicyRenewalFile
                {
                    Id = Guid.NewGuid(),
                    PolicyRenewalId = period,
                    FileMetadataId = duplicateFile,
                    FileType = PolicyFileType.Invoice,
                    AttachedByUserId = User,
                    AttachedAtUtc = new DateTime(2024, 2, 2, 0, 0, 0, DateTimeKind.Utc),
                });
                await context.SaveChangesAsync();

                await AddPolicyFileAsync(context, relocatedSource, policy, relocatedFile,
                    fileType: 3, effectiveDate: effective, attachedBy: User, attachedAt: attachedAt);
                await AddPolicyFileAsync(context, duplicateSource, policy, duplicateFile);

                await SeedPolicyAsync(context, orphan, "Reversible orphan");
                await AddPolicyFileAsync(context, Guid.NewGuid(), orphan, orphanFile);
            }

            Guid reattachedId;
            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                // A same-file re-attachment AFTER the migration, on a different period, so deleting by
                // (period, file) rather than by row id would be visible.
                var second = Guid.NewGuid();
                await AddPeriodAsync(context, second, policy, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                reattachedId = Guid.NewGuid();
                context.PolicyRenewalFiles.Add(new PolicyRenewalFile
                {
                    Id = reattachedId,
                    PolicyRenewalId = second,
                    FileMetadataId = relocatedFile,
                    FileType = PolicyFileType.ClaimDocument,
                    AttachedByUserId = User,
                    AttachedAtUtc = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);

                Assert.True(await MigrationSeam.TableExistsAsync(context, "InsurancePolicyFiles"));

                // Restored exactly, including the columns nothing but the ledger could have carried.
                var restored = await MigrationSeam.RowAsync(context,
                    $"SELECT * FROM `InsurancePolicyFiles` WHERE Id = '{relocatedSource}'");
                Assert.NotNull(restored);
                Assert.Equal(policy, ToGuid(restored!["InsurancePolicyId"]));
                Assert.Equal(relocatedFile, ToGuid(restored["FileMetadataId"]));
                Assert.Equal(3, Convert.ToInt32(restored["FileType"]));
                Assert.Equal(effective, (DateTime)restored["EffectiveDate"]!);
                Assert.Equal(User, restored["AttachedByUserId"]);
                Assert.Equal(attachedAt, (DateTime)restored["AttachedAtUtc"]!);

                // The skipped row is restored too — the one whose restoration nothing but the ledger
                // can verify, since it left no destination row of its own.
                Assert.Equal(1, await MigrationSeam.CountAsync(context,
                    $"SELECT COUNT(*) FROM `InsurancePolicyFiles` WHERE Id = '{duplicateSource}'"));

                // The relocated destination row is gone; everything else on that period survives.
                Assert.Equal(0, await context.PolicyRenewalFiles
                    .CountAsync(f => f.PolicyRenewalId == period && f.FileMetadataId == relocatedFile));
                Assert.True(await context.PolicyRenewalFiles.AnyAsync(f => f.Id == untouchedId));
                Assert.True(await context.PolicyRenewalFiles
                    .AnyAsync(f => f.PolicyRenewalId == period && f.FileMetadataId == duplicateFile));
                Assert.True(await context.PolicyRenewalFiles.AnyAsync(f => f.Id == reattachedId));

                // The placeholder period is gone with the documents it was created to hold.
                Assert.Equal(0, await context.PolicyRenewals.CountAsync(r => r.InsurancePolicyId == orphan));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// §16.12's last clause: <c>Down</c> completes without a foreign-key error when a parent named by a
    /// ledger row has since been deleted. The ledger's three keys are what make this work — a cascaded
    /// -away ledger row correctly restores nothing, rather than being reinserted into a table whose own
    /// keys would then reject it. A <c>Down</c> that throws lands on someone already recovering from
    /// something else.
    /// </summary>
    [SkippableFact]
    public async Task Down_survives_a_policy_deleted_between_up_and_down()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var keep = Guid.NewGuid();
        var doomed = Guid.NewGuid();
        var keepFile = Guid.NewGuid();
        var doomedFile = Guid.NewGuid();
        var keepSource = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedPrincipalsAsync(context, keepFile, doomedFile);
                await SeedPolicyAsync(context, keep, "Survivor");
                await SeedPolicyAsync(context, doomed, "Deleted later");
                await AddPeriodAsync(context, Guid.NewGuid(), keep, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                await AddPeriodAsync(context, Guid.NewGuid(), doomed, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                await AddPolicyFileAsync(context, keepSource, keep, keepFile);
                await AddPolicyFileAsync(context, Guid.NewGuid(), doomed, doomedFile);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                await context.InsurancePolicies.Where(p => p.InsurancePolicyId == doomed).ExecuteDeleteAsync();

                // The cascade took the ledger row with the policy, which is the behaviour the source
                // row's own key had.
                Assert.Equal(1, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `_InsurancePolicyFileRelocation`"));
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);

                Assert.Equal(1, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `InsurancePolicyFiles`"));
                Assert.Equal(1, await MigrationSeam.CountAsync(context,
                    $"SELECT COUNT(*) FROM `InsurancePolicyFiles` WHERE Id = '{keepSource}'"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// §16.36: deleting a user nulls their attribution on the ledger as well as on the relocated rows,
    /// so <c>users.delete</c> stays atomic and the ledger does not become a retention surface holding
    /// identifiers that outlive the account.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_user_nulls_their_attribution_on_the_ledger()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        const string departing = "departing-user";
        var policy = Guid.NewGuid();
        var file = Guid.NewGuid();
        var source = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await AttributionUsers.EnsureAsync(context, User, departing);
                await SeedFilesAsync(context, file);
                await SeedPolicyAsync(context, policy, "Attributed");
                await AddPeriodAsync(context, Guid.NewGuid(), policy, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                await AddPolicyFileAsync(context, source, policy, file, attachedBy: departing);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                await context.Users.Where(u => u.Id == departing).ExecuteDeleteAsync();

                Assert.Equal(1, await MigrationSeam.CountAsync(context,
                    $"SELECT COUNT(*) FROM `_InsurancePolicyFileRelocation` WHERE SourceId = '{source}'"));
                Assert.Null(await MigrationSeam.ScalarAsync(context,
                    $"SELECT AttachedByUserId FROM `_InsurancePolicyFileRelocation` WHERE SourceId = '{source}'"));
                Assert.True(await context.PolicyRenewalFiles.AnyAsync(f => f.AttachedByUserId == null));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// §16.13: the three migrations complete in under five seconds at 10,000 source rows. The bound is
    /// what rules out a cursor-per-row implementation; the statements here are all set-based.
    /// </summary>
    [SkippableFact]
    public async Task Ten_thousand_source_rows_migrate_in_under_five_seconds()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        const int rows = 10_000;
        var policy = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await AttributionUsers.EnsureAsync(context, User);
                await SeedPolicyAsync(context, policy, "Bulk");
                await AddPeriodAsync(context, Guid.NewGuid(), policy, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                // A digit cross-join rather than a recursive CTE: the server's default
                // max_recursive_iterations is 1000, and raising it per session would not survive EF
                // reopening the connection. FileMetadata.FileBlobId is unique, so the blobs are 1:1.
                const string digits = """
                    WITH digit (n) AS (
                        SELECT 0 UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL
                        SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL
                        SELECT 8 UNION ALL SELECT 9)
                    """;

                await context.Database.ExecuteSqlRawAsync($"""
                    INSERT INTO `FileBlob` (`Id`, `Content`)
                    {digits}
                    SELECT UUID(), 0x010203
                    FROM digit thousands, digit hundreds, digit tens, digit units;
                    """);

                await context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO `FileMetadata`
                        (`Id`, `UploadedByUserId`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`,
                         `FileBlobId`, `UploadedAtUtc`)
                    SELECT UUID(), NULL, CONCAT('bulk-', b.`Id`, '.pdf'), 'application/pdf', 3,
                           REPLACE(b.`Id`, '-', ''), b.`Id`, '2025-01-01 00:00:00'
                    FROM `FileBlob` b;
                    """);

                await context.Database.ExecuteSqlRawAsync($"""
                    INSERT INTO `InsurancePolicyFiles`
                        (`Id`, `InsurancePolicyId`, `FileMetadataId`, `FileType`, `EffectiveDate`,
                         `AttachedByUserId`, `AttachedAtUtc`)
                    SELECT UUID(), '{policy}', f.`Id`, 5, NULL, NULL, '2025-01-01 00:00:00'
                    FROM `FileMetadata` f;
                    """);
            }

            await using (var context = NewContext())
            {
                var stopwatch = Stopwatch.StartNew();
                await context.Database.MigrateAsync();
                stopwatch.Stop();

                Assert.Equal(rows, await context.PolicyRenewalFiles.CountAsync());
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                    $"The relocation took {stopwatch.Elapsed.TotalSeconds:F1}s at {rows} rows; the bound is 5s.");
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Seeding & plumbing ──────────────────────────────────────────────────────

    private async Task<(long Files, long Periods, long Ledger, long Relocated)> SnapshotAsync(OdysseyContext context) =>
    (
        await context.PolicyRenewalFiles.LongCountAsync(),
        await context.PolicyRenewals.LongCountAsync(),
        await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `_InsurancePolicyFileRelocation`"),
        await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM `_InsurancePolicyFileRelocation` WHERE Outcome = 'Relocated'")
    );

    private static async Task SeedPrincipalsAsync(OdysseyContext context, params Guid[] fileIds)
    {
        await AttributionUsers.EnsureAsync(context, User);
        await SeedFilesAsync(context, fileIds);
    }

    private static async Task SeedFilesAsync(OdysseyContext context, params Guid[] fileIds)
    {
        foreach (var fileId in fileIds)
        {
            var blob = Guid.NewGuid();
            context.FileBlob.Add(new FileBlob { Id = blob, Content = [1, 2, 3] });
            context.FileMetadata.Add(new FileMetadata
            {
                Id = fileId,
                FileName = $"{fileId:N}.pdf",
                ContentType = "application/pdf",
                SizeBytes = 3,
                Sha256Hash = Guid.NewGuid().ToString("N"),
                FileBlobId = blob,
                UploadedAtUtc = DateTime.UtcNow,
            });
        }

        await context.SaveChangesAsync();
    }

    /// <summary>A policy plus the insurer contact its required, RESTRICT-ed foreign key needs.</summary>
    private static async Task SeedPolicyAsync(OdysseyContext context, Guid policyId, string name)
    {
        var insurerId = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = insurerId,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            OrganizationDetails = new() { LegalName = name },
            NormalizedName = name.ToUpperInvariant(),
            Type = ContactType.Organization,
        });
        context.InsurancePolicies.Add(new InsurancePolicy
        {
            InsurancePolicyId = policyId,
            Name = name,
            Type = InsurancePolicyType.Home,
            InsurerId = insurerId,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private static async Task AddPeriodAsync(OdysseyContext context, Guid renewalId, Guid policyId, DateTime from)
    {
        context.PolicyRenewals.Add(new PolicyRenewal
        {
            PolicyRenewalId = renewalId,
            InsurancePolicyId = policyId,
            FromDate = from,
            ToDate = from.AddYears(1),
            Premium = 100m,
            PremiumCurrencyCode = "USD",
            CoverageAmount = 1000m,
            CoverageCurrencyCode = "USD",
            CreatedAtUtc = from,
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// A row in the table this change removes. It has no entity type any more — that is the point of the
    /// change — so the "before" state can only be written as SQL.
    /// </summary>
    private static Task AddPolicyFileAsync(
        OdysseyContext context, Guid id, Guid policyId, Guid fileId,
        int fileType = 5, DateTime? effectiveDate = null, string? attachedBy = User, DateTime? attachedAt = null)
    {
        var effective = effectiveDate is null
            ? "NULL"
            : $"'{effectiveDate.Value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}'";
        var user = attachedBy is null ? "NULL" : $"'{attachedBy}'";
        var attached = (attachedAt ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `InsurancePolicyFiles`
                (`Id`, `InsurancePolicyId`, `FileMetadataId`, `FileType`, `EffectiveDate`,
                 `AttachedByUserId`, `AttachedAtUtc`)
            VALUES ('{id}', '{policyId}', '{fileId}', {fileType}, {effective}, {user}, '{attached}');
            """);
    }

    private static Guid ToGuid(object? value) => value is Guid guid ? guid : Guid.Parse((string)value!);

    private static IEnumerable<Exception> Unwrap(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static string Flatten(Exception error) =>
        string.Join(" | ", Unwrap(error).Select(e => e.Message));

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    /// <summary>A connection to a database that always exists — the private one is being created or
    /// dropped, so it cannot be the one the statement connects through.</summary>
    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}

#pragma warning restore EF1002
