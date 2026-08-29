using Odyssey.Context;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Replaces <c>FirstUserRoleAssignmentTests</c>, which pinned the opposite behaviour (issue #290).
/// Registration order confers no privilege: the first account added to an empty database is created
/// exactly like the second — no role row, unconfirmed, and disabled while require-admin-approval is on.
/// The initial administrator comes from <c>BootstrapAdminSeeder</c> instead.
/// </summary>
public class RegistrationGrantsNoPrivilegeTests
{
    [Fact]
    public void TheFirstUserOnAnEmptyDatabase_GetsNoRole_NoConfirmation_AndIsDisabled()
    {
        using var context = CreateContext();

        var first = AddUser(context, "first@example.com");

        Assert.Empty(context.UserRoles.Where(userRole => userRole.UserId == first.Id));
        Assert.False(first.EmailConfirmed);
        Assert.Equal(AccountLockout.DisabledLockoutEnd, first.LockoutEnd);
        Assert.True(first.LockoutEnabled);
    }

    [Fact]
    public void TheSecondUser_IsTreatedIdenticallyToTheFirst()
    {
        using var context = CreateContext();

        AddUser(context, "first@example.com");
        var second = AddUser(context, "second@example.com");

        Assert.Empty(context.UserRoles.Where(userRole => userRole.UserId == second.Id));
        Assert.False(second.EmailConfirmed);
        Assert.Equal(AccountLockout.DisabledLockoutEnd, second.LockoutEnd);
    }

    /// <summary>
    /// The old branch keyed off <c>Users.Count() == 0</c> and promoted <c>newUsers[0]</c>, so a batch
    /// insert into an empty database was the sharpest expression of it. Nothing is promoted now.
    /// </summary>
    [Fact]
    public void MultipleUsersInOneSaveChanges_ProduceNoRoleRowsAtAll()
    {
        using var context = CreateContext();

        var first = NewUser("first@example.com");
        var second = NewUser("second@example.com");
        context.Users.AddRange(first, second);
        context.SaveChanges();

        Assert.Empty(context.UserRoles);
        Assert.Equal(AccountLockout.DisabledLockoutEnd, first.LockoutEnd);
        Assert.Equal(AccountLockout.DisabledLockoutEnd, second.LockoutEnd);
    }

    [Fact]
    public void WithApprovalOff_TheFirstUserIsEnabled_ButStillUnprivileged()
    {
        using var context = CreateContext(requireApproval: false);

        var first = AddUser(context, "first@example.com");

        Assert.Null(first.LockoutEnd);
        Assert.Empty(context.UserRoles);
        Assert.False(first.EmailConfirmed);
    }

    private static OdysseyContext CreateContext(bool requireApproval = true)
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase($"RegistrationGrantsNoPrivilegeTests_{Guid.NewGuid()}")
            .Options;

        var context = new OdysseyContext(options);
        // EnsureCreated applies the HasData seeds, including the roles the deleted branch used to link
        // to and RegistrationRequireAdminApproval="true".
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
        var user = NewUser(email);
        context.Users.Add(user);
        context.SaveChanges();
        return user;
    }

    private static ApplicationUser NewUser(string email)
    {
        var normalized = email.ToUpperInvariant();

        return new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = normalized,
            Email = email,
            NormalizedEmail = normalized,
        };
    }
}
