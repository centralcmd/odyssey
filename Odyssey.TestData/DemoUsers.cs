namespace Odyssey.TestData;

/// <summary>
/// A demo login user. Plain data only — actual Identity user creation (UserManager,
/// password hashing, role assignment) happens in the seeder (step 2), so this project
/// stays free of any Identity / Application.Context dependency.
/// </summary>
/// <param name="Email">Login email; also used as the user name.</param>
/// <param name="Role">Canonical role name from RoleDefinitions (Admin/Owner/User/Guest).</param>
/// <param name="FirstName">Profile first name (issue #316).</param>
/// <param name="LastName">Profile last name.</param>
/// <param name="DisplayName">Profile display-name override shown in attribution.</param>
/// <param name="BirthDate">Profile date of birth.</param>
/// <param name="Sex">Profile sex as the Odyssey.Dtos.Application.Sex ordinal (1 = Male, 2 = Female);
/// kept as a primitive so this project takes no Identity/Application.Context dependency.</param>
public sealed record DemoUser(
    string Email,
    string Role,
    string FirstName,
    string LastName,
    string DisplayName,
    DateOnly BirthDate,
    int Sex)
{
    /// <summary>Shared password for all demo users.</summary>
    public string Password => DemoDataDefaults.UserPassword;

    /// <summary>Stable id derived from the email, so re-seeding reuses the same user id.</summary>
    public string Id => DeterministicGuid.From($"user::{Email}").ToString();
}

public static class DemoUsers
{
    // Finance data is NOT user-scoped, so these users differ only by role/permission;
    // they all see the same shared portfolio (spec §3.5). Each carries a complete profile
    // (issue #316) so seeded logins skip the first-login onboarding gate.
    public static readonly IReadOnlyList<DemoUser> All =
    [
        new("admin@demo.example.com", "Admin", "Ada", "Lindqvist", "Ada L.", new DateOnly(1985, 3, 12), 2),
        new("owner@demo.example.com", "Owner", "Olav", "Berg", "Olav Berg", new DateOnly(1979, 11, 2), 1),
        new("user@demo.example.com", "User", "Sofia", "Haugen", "Sofia H.", new DateOnly(1992, 6, 24), 2),
        new("guest@demo.example.com", "Guest", "Gustav", "Moen", "Gustav", new DateOnly(1968, 1, 30), 1),
    ];
}
