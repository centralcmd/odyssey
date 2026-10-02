using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>The contact audit line never renders an empty actor (issue #287 M2/M4).</summary>
public class ContactAuditLogTests
{
    [Fact]
    public void APrincipalWithoutAUserId_IsLoggedAsNoActor_NeverAsAnEmptySlot()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var audit = new ContactAuditLog(factory.CreateLogger<ContactAuditLog>());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        audit.ContactChanged(anonymous, Guid.NewGuid(), "alias.created");
        audit.VCardExported(anonymous, rowCount: 3, filtered: false);

        var lines = logs.ForCategory("ContactAuditLog").Select(entry => entry.Message).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.EndsWith($"by {ContactAuditLog.NoActor}.", line, StringComparison.Ordinal));
    }
}
