using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The fast-tier half of <see cref="AccountTypeRetirementReport"/> (issue #218 §9). The branches that
/// need a schema — migration pending, already applied, an empty database — run against real MariaDB in
/// <c>Odyssey.IntegrationTests</c>' <c>RetirePropertyAndVehicleAccountTypesMigrationTests</c>.
/// </summary>
public class AccountTypeRetirementReportTests
{
    [Fact]
    public async Task A_non_relational_context_has_nothing_to_report()
    {
        await using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase($"retirement-report-{Guid.NewGuid()}")
            .Options);

        Assert.Null(await AccountTypeRetirementReport.ReadIfPendingAsync(context, CancellationToken.None));
    }
}
