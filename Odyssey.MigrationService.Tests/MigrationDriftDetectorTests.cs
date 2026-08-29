using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The decision at the heart of the drift guard (issue #468), tested as the pure function it is.
/// </summary>
/// <remarks>
/// Kept separate from the relational plumbing on purpose. Gathering the inputs needs a real engine —
/// <c>information_schema</c> and an EF migrations assembly — but choosing between "this is an ordinary
/// upgrade" and "this database can never be migrated again" is arithmetic over two sets, and that is the
/// half a regression would most likely land in. The false-positive cases below matter as much as the
/// positive ones: a guard that fired on every ordinary upgrade would be worse than no guard at all.
/// </remarks>
public class MigrationDriftDetectorTests
{
    private static SchemaObject Table(string table) => new(SchemaObjectKind.Table, table, table);

    private static SchemaObject Column(string table, string column) =>
        new(SchemaObjectKind.Column, table, column);

    private static SchemaObject Index(string table, string index) =>
        new(SchemaObjectKind.Index, table, index);

    private static SchemaObject ForeignKey(string table, string name) =>
        new(SchemaObjectKind.ForeignKey, table, name);

    /// <summary>A database holding the given objects. A column implies its table, as it does in the
    /// real snapshot, where both are read from the same <c>information_schema.COLUMNS</c> rows.</summary>
    private static SchemaObjects Schema(params SchemaObject[] objects) =>
        new([.. objects.SelectMany(o => o.Kind == SchemaObjectKind.Column
            ? new[] { Table(o.Table), o }
            : [o])]);

    private static PendingMigrationObjects Creates(string migrationId, params SchemaObject[] objects) =>
        new(migrationId, objects);

    [Fact]
    public void NoPendingMigrations_IsNotDrift()
    {
        var drift = MigrationDriftDetector.Detect([], Schema(Column("Accounts", "AccountId")));

        Assert.Null(drift);
    }

    [Fact]
    public void AFreshDatabase_IsNotDrift()
    {
        var drift = MigrationDriftDetector.Detect(
            [Creates("20260828172122_InitialCreate", Table("Accounts"), Table("Transactions"))],
            SchemaObjects.Empty);

        Assert.Null(drift);
    }

    [Fact]
    public void APendingMigration_RecreatingAnExistingTable_IsDrift()
    {
        var drift = MigrationDriftDetector.Detect(
            [Creates("20260828172122_InitialCreate", Table("Accounts"), Table("Transactions"))],
            Schema(Column("Accounts", "AccountId")));

        Assert.NotNull(drift);
        Assert.Equal("20260828172122_InitialCreate", drift.MigrationId);
        Assert.Equal("table 'Accounts'", drift.ExistingObject);
    }

    [Fact]
    public void APendingMigration_ReaddingAnExistingColumn_IsDrift()
    {
        var drift = MigrationDriftDetector.Detect(
            [Creates("20260901120000_AddNickname", Column("Accounts", "Nickname"))],
            Schema(Column("Accounts", "AccountId"), Column("Accounts", "Nickname")));

        Assert.NotNull(drift);
        Assert.Equal("column 'Accounts.Nickname'", drift.ExistingObject);
    }

    /// <summary>
    /// The case that rules out the cheaper "pending migrations exist and so do some tables" test: this
    /// is what every ordinary upgrade looks like, and it must pass straight through.
    /// </summary>
    [Fact]
    public void AnOrdinaryUpgrade_AddingAColumnToALiveTable_IsNotDrift()
    {
        var drift = MigrationDriftDetector.Detect(
            [Creates("20260901120000_AddNickname", Column("Accounts", "Nickname"))],
            Schema(Column("Accounts", "AccountId"), Column("Accounts", "Name")));

        Assert.Null(drift);
    }

    [Fact]
    public void AnOrdinaryUpgrade_AddingANewTableBesideLiveOnes_IsNotDrift()
    {
        var drift = MigrationDriftDetector.Detect(
            [Creates("20260901120000_AddBudgets", Table("Budgets"))],
            Schema(Column("Accounts", "AccountId"), Column("Transactions", "TransactionId")));

        Assert.Null(drift);
    }

    /// <summary>
    /// MariaDB's identifier casing depends on <c>lower_case_table_names</c> and on the host filesystem,
    /// so a case-sensitive comparison would miss real drift on a server that folds names.
    /// </summary>
    [Fact]
    public void ObjectNames_AreComparedCaseInsensitively()
    {
        var drift = MigrationDriftDetector.Detect(
            [Creates("20260828172122_InitialCreate", Table("Accounts"))],
            Schema(Column("accounts", "accountid")));

        Assert.NotNull(drift);
    }

    /// <summary>
    /// The interrupted run leaves an arbitrary prefix applied, so the offending object is rarely the
    /// first one the migration names — the detector has to scan, not just peek.
    /// </summary>
    [Fact]
    public void DriftIsFound_EvenWhenTheExistingObject_IsNotTheFirstOneCreated()
    {
        var drift = MigrationDriftDetector.Detect(
            [Creates("20260828172122_InitialCreate", Table("Budgets"), Table("Tags"), Table("Accounts"))],
            Schema(Column("Accounts", "AccountId")));

        Assert.NotNull(drift);
        Assert.Equal("table 'Accounts'", drift.ExistingObject);
    }

    /// <summary>
    /// Every migration in the repository today bundles its indexes and foreign keys into
    /// <c>CreateTable</c>, so these two kinds have no live example — which is exactly why they are
    /// pinned. A later index-only or constraint-only migration drifts the same way, and nothing else
    /// would catch it going missing from the mapping.
    /// </summary>
    [Fact]
    public void APendingMigration_RecreatingAnExistingIndex_IsDrift()
    {
        var drift = MigrationDriftDetector.Detect(
            [Creates("20260901120000_AddAccountNameIndex", Index("Accounts", "IX_Accounts_Name"))],
            Schema(Column("Accounts", "Name"), Index("Accounts", "IX_Accounts_Name")));

        Assert.NotNull(drift);
        Assert.Equal("index 'IX_Accounts_Name' on table 'Accounts'", drift.ExistingObject);
    }

    [Fact]
    public void APendingMigration_ReaddingAnExistingForeignKey_IsDrift()
    {
        var drift = MigrationDriftDetector.Detect(
            [Creates("20260901120000_AddAccountFk", ForeignKey("Transactions", "FK_Transactions_Accounts"))],
            Schema(
                Column("Transactions", "AccountId"),
                ForeignKey("Transactions", "FK_Transactions_Accounts")));

        Assert.NotNull(drift);
        Assert.Equal("foreign key 'FK_Transactions_Accounts' on table 'Transactions'", drift.ExistingObject);
    }

    [Fact]
    public void AnOrdinaryUpgrade_AddingANewIndexToALiveTable_IsNotDrift()
    {
        var drift = MigrationDriftDetector.Detect(
            [Creates("20260901120000_AddAccountNameIndex", Index("Accounts", "IX_Accounts_Name"))],
            Schema(Column("Accounts", "Name"), Index("Accounts", "IX_Accounts_CurrencyCode")));

        Assert.Null(drift);
    }

    /// <summary>
    /// Kind is part of an object's identity. MySQL names a foreign key's backing index after the
    /// constraint, so the two share a name on the same table — collapsing them would make every
    /// foreign key look like a pre-existing index.
    /// </summary>
    [Fact]
    public void ObjectsOfDifferentKinds_SharingATableAndName_AreNotTheSameObject()
    {
        var drift = MigrationDriftDetector.Detect(
            [Creates("20260901120000_AddAccountFk", ForeignKey("Transactions", "FK_Transactions_Accounts"))],
            Schema(Index("Transactions", "FK_Transactions_Accounts")));

        Assert.Null(drift);
    }

    [Fact]
    public void DriftIsFound_InAnyPendingMigration_NotOnlyTheFirst()
    {
        var drift = MigrationDriftDetector.Detect(
            [
                Creates("20260901120000_AddBudgets", Table("Budgets")),
                Creates("20260902120000_AddTags", Table("Tags")),
            ],
            Schema(Column("Tags", "TagId")));

        Assert.NotNull(drift);
        Assert.Equal("20260902120000_AddTags", drift.MigrationId);
    }
}

/// <summary>
/// The message is the deliverable of this whole guard — it is what an operator sees instead of a bare
/// <c>Table 'Accounts' already exists</c> — so its load-bearing parts are pinned.
/// </summary>
public class MigrationDriftExceptionTests
{
    [Fact]
    public void TheMessage_NamesTheContextTheMigrationAndTheObject()
    {
        var error = new MigrationDriftException(
            "OdysseyContext", new MigrationDrift("20260828172122_InitialCreate", "table 'Accounts'"));

        Assert.Contains("OdysseyContext", error.Message);
        Assert.Contains("20260828172122_InitialCreate", error.Message);
        Assert.Contains("table 'Accounts'", error.Message);
    }

    /// <summary>
    /// Two different situations produce this exception and the guard cannot tell them apart, so the
    /// message has to name both. It used to assert an interrupted run as the cause, which sent anyone
    /// hitting the other one — a database built by a migration set that was later squashed — looking for
    /// an interruption that never happened.
    /// </summary>
    [Fact]
    public void TheMessage_NamesBothCauses_NotOnlyTheInterruptedRun()
    {
        var error = new MigrationDriftException(
            "OdysseyContext", new MigrationDrift("20260829005807_InitialCreate", "table 'Budgets'"));

        Assert.Contains("interrupted", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("superseded", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("squash", error.Message, StringComparison.OrdinalIgnoreCase);

        // And the tell that separates them, so the reader knows how to decide which one they have.
        Assert.Contains("__EFMigrationsHistory", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a repair pointer the message is just a better-worded dead end. The original occurrence
    /// cost a DCP-log dig precisely because nothing said what to do next.
    /// </summary>
    [Fact]
    public void TheMessage_PointsAtTheRepairDocumentation()
    {
        var error = new MigrationDriftException(
            "OdysseyContext", new MigrationDrift("20260828172122_InitialCreate", "table 'Accounts'"));

        Assert.Contains("docs/migration-history-drift.md", error.Message);
    }
}
