using Odyssey.Context;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Pins the require-admin-approval registration gate enforced by <see cref="OdysseyContext"/>:
/// new accounts are created disabled (permanent lockout) until an admin enables them, and the gate can
/// be turned off via the live <see cref="SystemSettingsKeys.RegistrationRequireAdminApproval"/> setting
/// (issue #349 — moved off static config). Since issue #290 the gate has no exemption at all — the
/// first-ever user is no longer special; see <see cref="RegistrationGrantsNoPrivilegeTests"/>.
/// </summary>
public class NewUserApprovalTests
{
    [Fact]
    public void SubsequentUser_IsDisabled_WhenApprovalRequired()
    {
        using var context = CreateContext(requireApproval: true);

        AddUser(context, "first@example.com");
        var second = AddUser(context, "second@example.com");

        Assert.Equal(AccountLockout.DisabledLockoutEnd, second.LockoutEnd);
        Assert.True(second.LockoutEnabled);
    }

    [Fact]
    public void SubsequentUser_IsEnabled_WhenApprovalNotRequired()
    {
        using var context = CreateContext(requireApproval: false);

        AddUser(context, "first@example.com");
        var second = AddUser(context, "second@example.com");

        Assert.Null(second.LockoutEnd);
    }

    [Fact]
    public void FirstUser_IsDisabledToo_WhenApprovalRequired()
    {
        using var context = CreateContext(requireApproval: true);

        var first = AddUser(context, "first@example.com");

        Assert.Equal(AccountLockout.DisabledLockoutEnd, first.LockoutEnd);
        Assert.False(first.EmailConfirmed);
    }

    private static OdysseyContext CreateContext(bool requireApproval)
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase($"NewUserApprovalTests_{Guid.NewGuid()}")
            .Options;

        var context = new OdysseyContext(options);
        // EnsureCreated seeds the migration defaults (RegistrationRequireAdminApproval="true");
        // override the row directly to exercise the "not required" branch instead of constructing
        // options that no longer exist.
        context.Database.EnsureCreated();
        if (!requireApproval)
        {
            var setting = context.SystemSettings.Single(s => s.Key == SystemSettingsKeys.RegistrationRequireAdminApproval);
            setting.Value = "false";
            context.SaveChanges();
        }

        return context;
    }

    private static ApplicationUser AddUser(OdysseyContext context, string email)
    {
        var normalized = email.ToUpperInvariant();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = normalized,
            Email = email,
            NormalizedEmail = normalized,
        };

        context.Users.Add(user);
        context.SaveChanges();
        return user;
    }
}
