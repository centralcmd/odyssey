using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The drift guard's handling of <c>CHECK</c> constraints (issue #218 AC 11). The retirement migration's
/// only DDL is one, so without an arm an interruption after it replayed as a raw duplicate-constraint
/// error instead of the guard's repair message.
/// </summary>
/// <remarks>
/// Recognising check constraints also means recognising that EF scaffolds every change to a check's
/// expression as a drop and a re-add of the same name — which a replay can never collide on. Several
/// shipped migrations do exactly that, so the discount is what keeps an ordinary upgrade across them
/// from being refused.
/// </remarks>
public class MigrationRunnerCheckConstraintTests
{
    private static readonly SchemaObject RetiredCheck =
        new(SchemaObjectKind.CheckConstraint, "Accounts", "CK_Accounts_AccountTypeNotRetired");

    [Fact]
    public void AddCheckConstraint_CreatesTheConstraintOnItsTable()
    {
        var operation = new AddCheckConstraintOperation
        {
            Table = "Accounts",
            Name = "CK_Accounts_AccountTypeNotRetired",
            Sql = "`AccountType` NOT IN (6, 7)",
        };

        Assert.Equal(RetiredCheck, Assert.Single(MigrationRunner.CreatedBy(operation)));
    }

    [Fact]
    public void AnAddedCheck_ThatAlreadyExists_IsDrift_NamingTheConstraint()
    {
        var pending = new PendingMigrationObjects(
            "20260927004808_RetirePropertyAndVehicleAccountTypes",
            MigrationRunner.CreatedAfterDrops(
                [new AddCheckConstraintOperation { Table = "Accounts", Name = RetiredCheck.Name, Sql = "1" }],
                new HashSet<SchemaObject>(SchemaObjectComparer.Instance)));

        // Case differs on purpose: the engine's identifier casing is not something to rely on.
        var drift = MigrationDriftDetector.Detect(
            [pending],
            new SchemaObjects([new SchemaObject(SchemaObjectKind.CheckConstraint, "accounts", "ck_accounts_accounttypenotretired")]));

        Assert.NotNull(drift);
        Assert.Equal("check constraint 'CK_Accounts_AccountTypeNotRetired' on table 'Accounts'", drift!.ExistingObject);
    }

    [Fact]
    public void ACheckDroppedThenReAdded_IsNotACreationTheReplayCouldCollideOn()
    {
        var created = MigrationRunner.CreatedAfterDrops(
            [
                new DropCheckConstraintOperation { Table = "ContractParties", Name = "CK_ContractParties_ExactlyOneTarget" },
                new AddColumnOperation { Table = "ContractParties", Name = "PropertyId", ClrType = typeof(Guid) },
                new AddCheckConstraintOperation
                {
                    Table = "ContractParties", Name = "CK_ContractParties_ExactlyOneTarget", Sql = "1",
                },
            ],
            new HashSet<SchemaObject>(SchemaObjectComparer.Instance));

        Assert.Equal([new SchemaObject(SchemaObjectKind.Column, "ContractParties", "PropertyId")], created);
    }

    [Fact]
    public void ADropInAnEarlierPendingMigration_DiscountsTheReAddInALaterOne()
    {
        var dropped = new HashSet<SchemaObject>(SchemaObjectComparer.Instance);

        Assert.Empty(MigrationRunner.CreatedAfterDrops(
            [new DropCheckConstraintOperation { Table = "Accounts", Name = RetiredCheck.Name }], dropped));
        Assert.Empty(MigrationRunner.CreatedAfterDrops(
            [new AddCheckConstraintOperation { Table = "Accounts", Name = RetiredCheck.Name, Sql = "1" }], dropped));
    }

    [Fact]
    public void AReAddBeforeItsDrop_StillCounts()
    {
        // Order matters: a create that runs before the drop can collide with what is already there.
        var created = MigrationRunner.CreatedAfterDrops(
            [
                new AddCheckConstraintOperation { Table = "Accounts", Name = RetiredCheck.Name, Sql = "1" },
                new DropCheckConstraintOperation { Table = "Accounts", Name = RetiredCheck.Name },
            ],
            new HashSet<SchemaObject>(SchemaObjectComparer.Instance));

        Assert.Equal([RetiredCheck], created);
    }

    [Fact]
    public void ADropOnAnotherTable_DoesNotDiscountASameNamedCheck()
    {
        var created = MigrationRunner.CreatedAfterDrops(
            [
                new DropCheckConstraintOperation { Table = "Properties", Name = RetiredCheck.Name },
                new AddCheckConstraintOperation { Table = "Accounts", Name = RetiredCheck.Name, Sql = "1" },
            ],
            new HashSet<SchemaObject>(SchemaObjectComparer.Instance));

        Assert.Equal([RetiredCheck], created);
    }

    [Fact]
    public void DroppedBy_CoversEveryDropAndTheOldNameOfEveryRename()
    {
        Assert.Equal(
            [
                new SchemaObject(SchemaObjectKind.Table, "T", "T"),
                new SchemaObject(SchemaObjectKind.Column, "T", "C"),
                new SchemaObject(SchemaObjectKind.Index, "T", "IX"),
                new SchemaObject(SchemaObjectKind.ForeignKey, "T", "FK"),
                new SchemaObject(SchemaObjectKind.CheckConstraint, "T", "CK"),
                new SchemaObject(SchemaObjectKind.Table, "Old", "Old"),
                new SchemaObject(SchemaObjectKind.Column, "T", "OldColumn"),
                new SchemaObject(SchemaObjectKind.Index, "T", "OldIndex"),
            ],
            new MigrationOperation[]
            {
                new DropTableOperation { Name = "T" },
                new DropColumnOperation { Table = "T", Name = "C" },
                new DropIndexOperation { Table = "T", Name = "IX" },
                new DropForeignKeyOperation { Table = "T", Name = "FK" },
                new DropCheckConstraintOperation { Table = "T", Name = "CK" },
                new RenameTableOperation { Name = "Old", NewName = "New" },
                new RenameColumnOperation { Table = "T", Name = "OldColumn", NewName = "NewColumn" },
                new RenameIndexOperation { Table = "T", Name = "OldIndex", NewName = "NewIndex" },
            }.SelectMany(MigrationRunner.DroppedBy));
    }
}
