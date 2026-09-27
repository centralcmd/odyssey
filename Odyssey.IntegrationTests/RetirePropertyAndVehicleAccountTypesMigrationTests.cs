// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test wrote itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.MigrationService;
using Xunit;
using ContextAccountFileType = Odyssey.Context.AccountFileType;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextContractType = Odyssey.Context.ContractType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using DtoPropertyType = Odyssey.Dtos.Finance.PropertyType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// <c>RetirePropertyAndVehicleAccountTypes</c> (issue #218) against real MariaDB — AC 1–9, 11, 12, 16
/// and 17. None of it is observable on the fast tiers: the EF InMemory provider runs no migrations and
/// enforces no <c>CHECK</c>, no foreign key and no cascade.
/// </summary>
/// <remarks>
/// The seed is written through the entities rather than raw SQL, unlike the older migration tests:
/// this is the head migration, and at its baseline the model and the schema differ only by the
/// <c>CHECK</c> it adds, which an insert of type 6 or 7 is exactly what the baseline must still allow.
/// When a later migration changes one of the seeded tables, that seed moves to raw SQL — the reads
/// already are.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class RetirePropertyAndVehicleAccountTypesMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_retire_account_types";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_AddRealEstateHomeownerAssociation";

    private const string Subject = AccountTypeRetirementReport.MigrationName;

    private const string Check = "CK_Accounts_AccountTypeNotRetired";

    /// <summary>Every character a naive string composition would choke on (AC 16).</summary>
    private const string HostileNumber = "O'Brien; DROP TABLE `Accounts`; -- \\x";

    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime HouseOpened = new(2017, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime HouseClosed = new(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime HouseArchived = new(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CarOpened = new(2023, 2, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CarClosedBeforeOpened = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Seeded(
        Guid House, Guid Car, Guid Savings, Guid Custodian,
        Guid HouseEstimate, Guid HouseEstimateInEuro, Guid CarEstimate, Guid SavingsEstimate,
        Guid TagOne, Guid TagTwo,
        Guid Deed, Guid Letter, Guid Registration,
        Guid HouseParty, Guid CarParty);

    /// <summary>
    /// AC 1–8, 12, 16 — both types, one account deleted and one retained, every child kind moved, the
    /// hostile account number verbatim, and the counts reported before anything moved.
    /// </summary>
    [SkippableFact]
    public async Task Every_property_and_vehicle_account_becomes_a_property_under_the_same_guid()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            Seeded seed;
            AccountTypeRetirementReport? report;
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                seed = await SeedAsync(context);
                report = await AccountTypeRetirementReport.ReadIfPendingAsync(context, CancellationToken.None);
            }

            // AC 12 — the counts, taken before the move, and a log line carrying nothing but them.
            Assert.Equal(new AccountTypeRetirementReport(
                RealEstateProperties: 1, VehicleProperties: 1, RetainedAccounts: 1, Estimates: 3, SmartTags: 2,
                FileLinks: 3, ContractParties: 2, FileAnalysisJobsRemoved: 0, ClosedBeforeOpened: 1,
                CurrencyMismatchedEstimates: 1), report);
            var logger = new CapturingLogger();
            report!.Log(logger);
            var line = Assert.Single(logger.Lines);
            Assert.DoesNotContain("Residence", line, StringComparison.Ordinal);
            Assert.DoesNotContain("O'Brien", line, StringComparison.Ordinal);
            Assert.DoesNotContain("540000", line, StringComparison.Ordinal);

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);
                Assert.Null(await AccountTypeRetirementReport.ReadIfPendingAsync(context, CancellationToken.None));
            }

            await using (var context = NewContext())
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

                // AC 1.
                Assert.Equal(0, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `Accounts` WHERE `AccountType` IN (6, 7)"));

                // AC 2, 7, 8, 16 — the house: no transactions, so the account is gone; the property keeps
                // every field, the account number lands verbatim in Notes, and nothing was executed.
                Assert.False(await context.Accounts.AnyAsync(a => a.AccountId == seed.House));
                var house = await context.Properties.AsNoTracking()
                    .Include(p => p.RealEstateDetails).Include(p => p.VehicleDetails)
                    .SingleAsync(p => p.PropertyId == seed.House);
                Assert.Equal(
                    (DtoPropertyType.RealEstate, "Primary Residence", "Family home", "USD", (DateTime?)HouseOpened,
                        (DateTime?)HouseClosed, (DateTime?)HouseArchived),
                    (house.Type, house.Name, house.Description, house.CurrencyCode, house.AcquiredDate,
                        house.DisposedDate, house.Archived));
                Assert.Equal($"Migrated from account on {today}. Account number: {HostileNumber}.", house.Notes);
                Assert.NotNull(house.RealEstateDetails);
                Assert.Null(house.VehicleDetails);
                Assert.Equal(Odyssey.Dtos.Finance.RealEstateKind.Other, house.RealEstateDetails!.Kind);
                Assert.Null(house.RealEstateDetails.AddressLine);
                Assert.Null(house.RealEstateDetails.City);

                // AC 3, 6, 8 — the car: it has a transaction, so the account stays as an archived Other
                // asset with its custodian, transaction and smart tag; its reversed Closed goes to Notes.
                var carAccount = await context.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == seed.Car);
                Assert.Equal(ContextAccountType.OtherAsset, carAccount.AccountType);
                Assert.NotNull(carAccount.Archived);
                Assert.True(carAccount.Archived > Anchor);
                Assert.Equal(seed.Custodian, carAccount.CustodianId);
                Assert.Equal("CAR-1", carAccount.AccountNumber);
                Assert.Equal(1, await context.Transactions.CountAsync(t => t.AccountId == seed.Car));
                Assert.Equal([seed.TagTwo], await context.AccountSmartTags
                    .Where(s => s.AccountId == seed.Car).Select(s => s.TransactionTagId).ToListAsync());

                var car = await context.Properties.AsNoTracking()
                    .Include(p => p.VehicleDetails).SingleAsync(p => p.PropertyId == seed.Car);
                Assert.Equal(
                    (DtoPropertyType.Vehicle, "Family Car", (DateTime?)CarOpened, (DateTime?)null, (DateTime?)null),
                    (car.Type, car.Name, car.AcquiredDate, car.DisposedDate, car.Archived));
                Assert.Equal(
                    $"Migrated from account on {today}. Account number: CAR-1. Original closed date: 2020-01-01.",
                    car.Notes);
                Assert.Equal(Odyssey.Dtos.Finance.VehicleKind.Other, car.VehicleDetails!.Kind);
                Assert.Null(car.VehicleDetails.Vin);

                // AC 4 — estimates move with their ids and values; the mismatched currency is copied as is.
                var estimates = await context.PropertyEstimates.AsNoTracking()
                    .OrderBy(e => e.EffectiveFrom).ToListAsync();
                Assert.Equal(
                    [
                        (seed.HouseEstimate, seed.House, 540_000m, (string?)"USD", HouseOpened, (string?)"Purchase price."),
                        (seed.HouseEstimateInEuro, seed.House, 600_000m, "EUR", HouseOpened.AddYears(4), null),
                        (seed.CarEstimate, seed.Car, 48_000m, "USD", CarOpened, null),
                    ],
                    estimates.Select(e => (e.PropertyEstimateId, e.PropertyId, e.Value, e.CurrencyCode, e.EffectiveFrom, e.Note)));
                Assert.Equal([seed.SavingsEstimate], await context.AccountEstimates.Select(e => e.AccountEstimateId).ToListAsync());

                // AC 4 — smart tags, with their AddedAt.
                Assert.Equal(
                    [(seed.House, seed.TagOne, Anchor), (seed.Car, seed.TagTwo, Anchor.AddMinutes(1))],
                    (await context.PropertySmartTags.AsNoTracking().OrderBy(s => s.AddedAt).ToListAsync())
                        .Select(s => (s.PropertyId, s.TransactionTagId, s.AddedAt)));

                // AC 4 — file links, the type coarsened per the §4.1 table, attribution and validity kept.
                var files = await context.PropertyFiles.AsNoTracking().ToListAsync();
                Assert.Equal(3, files.Count);
                var deed = files.Single(f => f.FileMetadataId == seed.Deed);
                Assert.Equal((seed.House, PropertyFileType.PurchaseAgreement, Anchor, (DateTime?)Anchor.AddYears(1), (Guid?)seed.Custodian),
                    (deed.PropertyId, deed.FileType, deed.AttachedAtUtc, deed.ValidTo, deed.IssuedBy));
                Assert.Equal(PropertyFileType.Other, files.Single(f => f.FileMetadataId == seed.Letter).FileType);
                Assert.Equal((seed.Car, PropertyFileType.Registration),
                    files.Where(f => f.FileMetadataId == seed.Registration).Select(f => (f.PropertyId, f.FileType)).Single());
                // A retained account keeps its file link too: an analysis job may hang off it.
                Assert.Equal([seed.Registration], await context.AccountFiles
                    .Where(f => f.AccountId == seed.Car).Select(f => f.FileMetadataId).ToListAsync());

                // AC 5 — contract parties re-pointed in place: same row, same role, CHECK satisfied.
                var parties = await context.ContractParties.AsNoTracking()
                    .Where(p => p.ContractPartyId == seed.HouseParty || p.ContractPartyId == seed.CarParty)
                    .OrderBy(p => p.Role).ToListAsync();
                Assert.Equal(
                    [
                        (seed.HouseParty, (Guid?)null, (Guid?)seed.House, ContextContractPartyRole.Property),
                        (seed.CarParty, null, seed.Car, ContextContractPartyRole.Collateral),
                    ],
                    parties.Select(p => (p.ContractPartyId, p.AccountId, p.PropertyId, p.Role)));

                // An ordinary account is not touched.
                Assert.Equal(ContextAccountType.SavingsAccount,
                    (await context.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == seed.Savings)).AccountType);
                Assert.Equal(2, await context.Properties.CountAsync());

                // AC 9 — the CHECK refuses a retired ordinal written past the API.
                foreach (var retired in new[] { 6, 7 })
                {
                    var error = await Assert.ThrowsAnyAsync<MySqlException>(() => context.Database.ExecuteSqlRawAsync(
                        "INSERT INTO `Accounts` (`AccountId`, `Name`, `Description`, `Opened`, `AccountType`, `CurrencyCode`) " +
                        $"VALUES ('{Guid.NewGuid()}', 'Sneaky', '', '2026-01-01', {retired}, 'USD')"));
                    Assert.Contains(Check, error.Message, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 11 — both interruption windows. With the <c>CHECK</c> absent a replay changes no data and adds
    /// it; with the <c>CHECK</c> present the drift guard refuses, naming it, instead of a raw duplicate.
    /// </summary>
    [SkippableFact]
    public async Task A_replay_is_idempotent_before_the_check_and_reported_as_drift_after_it()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedAsync(context);
                await MigrationSeam.MigrateToAsync(context, Subject);
            }

            string before;
            await using (var context = NewContext())
            {
                before = await FingerprintAsync(context);

                // Window 1 — interrupted after the DML committed, before the CHECK.
                await context.Database.ExecuteSqlRawAsync($"ALTER TABLE `Accounts` DROP CONSTRAINT `{Check}`");
                await MigrationSeam.ForgetAsync(context, Subject);
            }

            await using (var context = NewContext())
            {
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
                Assert.True(await MigrationSeam.HasRunAsync(context, Subject));
                Assert.Equal(before, await FingerprintAsync(context));
                Assert.Equal(1, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() " +
                    $"AND TABLE_NAME = 'Accounts' AND CONSTRAINT_NAME = '{Check}' AND CONSTRAINT_TYPE = 'CHECK'"));

                // Window 2 — interrupted after the CHECK, before the history row.
                await MigrationSeam.ForgetAsync(context, Subject);
            }

            await using (var context = NewContext())
            {
                var drift = await Assert.ThrowsAsync<MigrationDriftException>(
                    () => MigrationRunner.MigrateAsync(context, CancellationToken.None));

                Assert.Equal(MigrationSeam.IdOf(context, Subject), drift.MigrationId);
                Assert.Contains(Check, drift.ExistingObject, StringComparison.Ordinal);
                Assert.Contains("docs/migration-history-drift.md", drift.Message, StringComparison.Ordinal);
                Assert.Equal(before, await FingerprintAsync(context));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 17 — a retained account and its property share one GUID, and each read path resolves it in its
    /// own table only: two different records, never one mistaken for the other.
    /// </summary>
    [SkippableFact]
    public async Task A_retained_account_and_its_property_are_two_records_under_one_guid()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            Seeded seed;
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                seed = await SeedAsync(context);
                await MigrationSeam.MigrateToAsync(context, Subject);
            }

            await using (var context = NewContext())
            {
                var account = await new AccountService(context, new ContactLookup(context)).Get(seed.Car);
                var property = await new PropertyService(context).Get(seed.Car);

                Assert.NotNull(account);
                Assert.NotNull(property);
                Assert.Equal(seed.Car, account!.AccountId);
                Assert.Equal(seed.Car, property!.PropertyId);
                Assert.Equal(DtoAccountType.OtherAsset, account.AccountType);
                Assert.Equal(DtoPropertyType.Vehicle, property.Type);
                Assert.Equal(1, account.TransactionCount);

                // The deleted house's id resolves as a property only.
                Assert.Null(await new AccountService(context, new ContactLookup(context)).Get(seed.House));
                Assert.NotNull(await new PropertyService(context).Get(seed.House));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 12 through the real <see cref="OdysseyMigrationService"/>: the report is read BEFORE the
    /// migration (afterwards the accounts it counts are gone) and logged once AFTER it; an empty
    /// database — where every migration is pending and there is no <c>Accounts</c> table to count —
    /// migrates without one; and a database already past the migration never logs one again.
    /// </summary>
    [SkippableFact]
    public async Task The_migrations_job_logs_the_counts_once_and_only_when_the_retirement_runs()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        // An empty database: the whole history is pending.
        await RecreateAsync();
        try
        {
            var fresh = await RunMigrationServiceAsync();
            Assert.DoesNotContain(fresh, line => line.Contains("Retired the Property and Vehicle", StringComparison.Ordinal));
            await using (var context = NewContext())
            {
                Assert.True(await MigrationSeam.HasRunAsync(context, Subject));
                Assert.Null(await AccountTypeRetirementReport.ReadIfPendingAsync(context, CancellationToken.None));
            }
        }
        finally
        {
            await DropAsync();
        }

        // A populated database at the baseline: counted before, logged after, exactly once.
        await RecreateAsync();
        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedAsync(context);
            }

            var line = Assert.Single(await RunMigrationServiceAsync(),
                l => l.Contains("Retired the Property and Vehicle", StringComparison.Ordinal));
            Assert.Contains("created 1 real-estate and 1 vehicle properties", line, StringComparison.Ordinal);
            Assert.Contains("kept 1 accounts", line, StringComparison.Ordinal);
            Assert.Contains("moved 3 estimates, 2 smart tags, 3 file links and 2 contract parties", line, StringComparison.Ordinal);

            await using (var context = NewContext())
            {
                Assert.Equal(0, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `Accounts` WHERE `AccountType` IN (6, 7)"));
            }

            // Already applied: the next run of the job reports nothing.
            Assert.DoesNotContain(await RunMigrationServiceAsync(),
                l => l.Contains("Retired the Property and Vehicle", StringComparison.Ordinal));
        }
        finally
        {
            await DropAsync();
        }
    }

    private async Task<List<string>> RunMigrationServiceAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<OdysseyContext>(options => options.UseMySql(
            fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString)));
        await using var provider = services.BuildServiceProvider();

        var logger = new CapturingLogger<OdysseyMigrationService>();
        await new OdysseyMigrationService(provider, logger).ExecuteAsync(CancellationToken.None);
        return logger.Lines;
    }

    // ── Seed ──────────────────────────────────────────────────────────────────

    private static async Task<Seeded> SeedAsync(OdysseyContext context)
    {
        var seed = new Seeded(
            House: Guid.NewGuid(), Car: Guid.NewGuid(), Savings: Guid.NewGuid(), Custodian: Guid.NewGuid(),
            HouseEstimate: Guid.NewGuid(), HouseEstimateInEuro: Guid.NewGuid(), CarEstimate: Guid.NewGuid(),
            SavingsEstimate: Guid.NewGuid(), TagOne: Guid.NewGuid(), TagTwo: Guid.NewGuid(),
            Deed: Guid.NewGuid(), Letter: Guid.NewGuid(), Registration: Guid.NewGuid(),
            HouseParty: Guid.NewGuid(), CarParty: Guid.NewGuid());

        context.Contacts.Add(new Contact
        {
            ContactId = seed.Custodian,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "NORDIC BANK",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Nordic Bank" },
        });
        context.TransactionTags.AddRange(
            new TransactionTag { TransactionTagId = seed.TagOne, Name = "Home" },
            new TransactionTag { TransactionTagId = seed.TagTwo, Name = "Fuel" });

        context.Accounts.AddRange(
            new Account
            {
                AccountId = seed.House, Name = "Primary Residence", Description = "Family home",
                AccountType = (ContextAccountType)6, CurrencyCode = "USD", Opened = HouseOpened,
                Closed = HouseClosed, Archived = HouseArchived, AccountNumber = HostileNumber,
                CustodianId = seed.Custodian,
            },
            new Account
            {
                AccountId = seed.Car, Name = "Family Car", Description = "", AccountType = (ContextAccountType)7,
                CurrencyCode = "USD", Opened = CarOpened, Closed = CarClosedBeforeOpened, AccountNumber = "CAR-1",
                CustodianId = seed.Custodian,
            },
            new Account
            {
                AccountId = seed.Savings, Name = "Savings", Description = "",
                AccountType = ContextAccountType.SavingsAccount, CurrencyCode = "USD", Opened = HouseOpened,
            });
        context.Transactions.Add(new Transaction
        {
            TransactionId = Guid.NewGuid(), AccountId = seed.Car, Description = "Opening value",
            Amount = 48_000m, TimeStamp = CarOpened,
        });

        context.AccountEstimates.AddRange(
            Estimate(seed.HouseEstimate, seed.House, 540_000m, "USD", HouseOpened, "Purchase price."),
            Estimate(seed.HouseEstimateInEuro, seed.House, 600_000m, "EUR", HouseOpened.AddYears(4), null),
            Estimate(seed.CarEstimate, seed.Car, 48_000m, "USD", CarOpened, null),
            Estimate(seed.SavingsEstimate, seed.Savings, 1m, "USD", HouseOpened, null));
        context.AccountSmartTags.AddRange(
            new AccountSmartTag { AccountId = seed.House, TransactionTagId = seed.TagOne, AddedAt = Anchor },
            new AccountSmartTag { AccountId = seed.Car, TransactionTagId = seed.TagTwo, AddedAt = Anchor.AddMinutes(1) });

        foreach (var file in new[] { seed.Deed, seed.Letter, seed.Registration })
        {
            var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
            context.FileBlob.Add(blob);
            context.FileMetadata.Add(new FileMetadata
            {
                Id = file, FileName = "doc.pdf", ContentType = "application/pdf", SizeBytes = 3,
                Sha256Hash = Guid.NewGuid().ToString("N"), FileBlobId = blob.Id, UploadedAtUtc = Anchor,
            });
        }

        context.AccountFiles.AddRange(
            new AccountFile
            {
                Id = Guid.NewGuid(), AccountId = seed.House, FileMetadataId = seed.Deed,
                FileType = ContextAccountFileType.PurchaseAgreement, AttachedAtUtc = Anchor,
                ValidFrom = Anchor, ValidTo = Anchor.AddYears(1), IssuedBy = seed.Custodian,
            },
            new AccountFile
            {
                Id = Guid.NewGuid(), AccountId = seed.House, FileMetadataId = seed.Letter,
                FileType = ContextAccountFileType.Message, AttachedAtUtc = Anchor,
            },
            new AccountFile
            {
                Id = Guid.NewGuid(), AccountId = seed.Car, FileMetadataId = seed.Registration,
                FileType = ContextAccountFileType.Registration, AttachedAtUtc = Anchor,
            });

        var purchase = Contract("House purchase", ContextContractType.Purchase);
        purchase.Parties.Add(Party(seed.HouseParty, purchase.ContractId, seed.House, ContextContractPartyRole.Property));
        var loan = Contract("Car loan", ContextContractType.Loan);
        loan.Parties.Add(Party(seed.CarParty, loan.ContractId, seed.Car, ContextContractPartyRole.Collateral));
        context.Contracts.AddRange(purchase, loan);

        await context.SaveChangesAsync();
        return seed;
    }

    private static AccountEstimate Estimate(
        Guid id, Guid account, decimal value, string currency, DateTime effectiveFrom, string? note) => new()
    {
        AccountEstimateId = id, AccountId = account, Value = value, CurrencyCode = currency,
        EffectiveFrom = effectiveFrom, Note = note, CreatedAtUtc = effectiveFrom,
    };

    private static Contract Contract(string name, ContextContractType type) => new()
    {
        ContractId = Guid.NewGuid(), Name = name, Type = type, StartDate = Anchor, CreatedAtUtc = Anchor,
    };

    private static ContractParty Party(Guid id, Guid contract, Guid account, ContextContractPartyRole role) => new()
    {
        ContractPartyId = id, ContractId = contract, AccountId = account, Role = role,
    };

    /// <summary>Every row the migration could touch, serialised — equal means no data changed.</summary>
    private static async Task<string> FingerprintAsync(OdysseyContext context)
    {
        var parts = new List<object?>
        {
            await MigrationSeam.ScalarAsync(context,
                "SELECT GROUP_CONCAT(CONCAT_WS('|', `AccountId`, `AccountType`, `Archived`) ORDER BY `AccountId`) FROM `Accounts`"),
            await MigrationSeam.ScalarAsync(context,
                "SELECT GROUP_CONCAT(CONCAT_WS('|', `PropertyId`, `Type`, `Notes`, `CreatedAt`) ORDER BY `PropertyId`) FROM `Properties`"),
            await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `RealEstateDetails`"),
            await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `VehicleDetails`"),
            await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `PropertyEstimates`"),
            await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `AccountEstimates`"),
            await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `PropertySmartTags`"),
            await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `AccountSmartTags`"),
            await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `PropertyFiles`"),
            await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `AccountFiles`"),
            await MigrationSeam.ScalarAsync(context,
                "SELECT GROUP_CONCAT(CONCAT_WS('|', `ContractPartyId`, `AccountId`, `PropertyId`) ORDER BY `ContractPartyId`) FROM `ContractParties`"),
        };
        return string.Join(" / ", parts);
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    private sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>;

    private class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

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

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}

#pragma warning restore EF1002
