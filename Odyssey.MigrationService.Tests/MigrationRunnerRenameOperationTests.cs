using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The drift guard's operation → schema-object mapping for the three <c>Rename*</c> operations
/// (issue #120, AC 45 and 47).
/// </summary>
/// <remarks>
/// Before this, every <c>Rename*Operation</c> fell through <c>CreatedBy</c>'s <c>_ => []</c> default
/// and was invisible to the pre-flight check: an interruption part-way through a renaming migration
/// surfaced as a raw <c>Table 'Terms' already exists</c> with no guidance, since MariaDB commits DDL
/// implicitly and leaves the renamed object behind with no history row.
/// </remarks>
public class MigrationRunnerRenameOperationTests
{
    [Fact]
    public void RenameTable_CreatesTheTableUnderItsNewName()
    {
        var operation = new RenameTableOperation { Name = "AccountTerms", NewName = "Terms" };

        var created = Assert.Single(MigrationRunner.CreatedBy(operation));

        Assert.Equal(new SchemaObject(SchemaObjectKind.Table, "Terms", "Terms"), created);
    }

    [Fact]
    public void RenameColumn_CreatesTheColumnUnderItsNewNameOnItsTable()
    {
        var operation = new RenameColumnOperation { Table = "Terms", Name = "BillingPeriod", NewName = "Interval" };

        var created = Assert.Single(MigrationRunner.CreatedBy(operation));

        Assert.Equal(new SchemaObject(SchemaObjectKind.Column, "Terms", "Interval"), created);
    }

    [Fact]
    public void RenameIndex_CreatesTheIndexUnderItsNewNameOnItsTable()
    {
        var operation = new RenameIndexOperation
        {
            Table = "Terms",
            Name = "IX_AccountTerms_AccountId_TermKind_LabelKey_EffectiveFrom",
            NewName = "IX_Terms_AccountId_TermKind_LabelKey_EffectiveFrom",
        };

        var created = Assert.Single(MigrationRunner.CreatedBy(operation));

        Assert.Equal(
            new SchemaObject(SchemaObjectKind.Index, "Terms", "IX_Terms_AccountId_TermKind_LabelKey_EffectiveFrom"),
            created);
    }

    /// <summary>
    /// AC 47 — the extension stays NARROW. A rename's own drop half creates nothing, so replaying it
    /// cannot collide with an object already present, which is the only failure this guard is about.
    /// A table rename with no new name (a schema-only move) likewise has nothing to report.
    /// </summary>
    [Theory]
    [MemberData(nameof(OperationsThatCreateNothing))]
    public void AnOperationThatCreatesNothing_MapsToNoSchemaObject(MigrationOperation operation)
    {
        Assert.Empty(MigrationRunner.CreatedBy(operation));
    }

    public static TheoryData<MigrationOperation> OperationsThatCreateNothing() =>
    [
        new DropTableOperation { Name = "AccountTerms" },
        new DropColumnOperation { Table = "Terms", Name = "BillingPeriod" },
        new DropIndexOperation { Table = "Terms", Name = "IX_AccountTerms_AccountId" },
        new DropForeignKeyOperation { Table = "Terms", Name = "FK_AccountTerms_Accounts_AccountId" },
        new AlterColumnOperation { Table = "Terms", Name = "Interval" },
        new RenameTableOperation { Name = "Terms", NewName = null },
        new SqlOperation { Sql = "UPDATE `Terms` SET `Interval` = 3 WHERE `Interval` = 4;" },
    ];
}
