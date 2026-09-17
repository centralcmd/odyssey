using System.Globalization;
using MudBlazor;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages;

/// <summary>
/// Presentation helpers shared by the <c>/users</c> table and the row components it expands into
/// (<see cref="UserDetailPanel"/>, <see cref="UserEditPanel"/>, <see cref="UserRolePill"/>). They
/// live here rather than on <see cref="Users"/> because more than one component renders the same
/// name, role glyph and date formats, and two copies of "how do we label a user" is exactly the
/// drift this split is meant to remove.
/// </summary>
internal static class UserDisplay
{
    /// <summary>
    /// The name column renders the server resolver's resolved label (issue #316): DisplayName ??
    /// FirstName ?? (admin caller: email). The username-derived heuristic is the fallback used only
    /// when the server sent no label (e.g. an unresolvable row), so the column is never blank.
    /// </summary>
    public static string DisplayName(ExistingUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            return user.DisplayName!;
        }

        var basis = !string.IsNullOrWhiteSpace(user.UserName) ? user.UserName! : (user.Email ?? string.Empty);
        var local = basis.Split('@')[0];
        var parts = local.Split('.', '_', '-', ' ').Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0)
            return basis;
        return string.Join(" ", parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    public static string Initials(ExistingUser user)
    {
        var name = DisplayName(user);
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "?";
        if (parts.Length == 1)
            return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    public static string CreatedText(ExistingUser user) =>
        user.CreatedAtUtc is { } created ? created.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) : "—";

    /// <summary>
    /// Structured "First Middle Last" from the profile (issue #316 follow-up), distinct from
    /// <see cref="DisplayName"/> (the resolver's attribution label — a nickname/override, not a legal
    /// name). Null when the user has no completed profile, so callers fall back to "—".
    /// </summary>
    public static string? FullName(ExistingUser user)
    {
        var parts = new[] { user.FirstName, user.MiddleName, user.LastName }.Where(p => !string.IsNullOrWhiteSpace(p));
        var joined = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    public static string? BirthDateText(ExistingUser user) =>
        user.BirthDate?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    public static string LockoutText(ExistingUser user)
    {
        if (user.LockoutEnd is not { } end || end <= DateTimeOffset.UtcNow)
            return "—";
        return end.Year >= 9999 ? "Indefinite" : end.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string RoleClass(string role) => role.ToLowerInvariant() switch
    {
        "owner" => "owner",
        "admin" => "admin",
        "user" => "user",
        _ => string.Empty,
    };

    public static string RoleIcon(string role) => role.ToLowerInvariant() switch
    {
        "owner" => Icons.Material.Filled.VerifiedUser,
        "admin" => Icons.Material.Filled.Shield,
        "user" => Icons.Material.Filled.HowToReg,
        "guest" => Icons.Material.Filled.Visibility,
        _ => Icons.Material.Filled.Person,
    };

    /// <summary>
    /// The role tint as a <c>{ bg, fg }</c> pair for <c>OdsAvatar</c> — <b>the only copy</b>, and the
    /// authoritative one (issue #94 §3).
    ///
    /// <para>
    /// <b>It has to be a value, not a class.</b> The <c>usr-av-*</c> rules these values came from were
    /// <b>scoped</b> to <c>Users.razor</c>, and scoped CSS does not cross a component boundary — a
    /// <c>Class</c> handed to a component would compile, render, and silently produce the default
    /// neutral tint on every row. Those rules, and the <c>AvatarClass</c> helper that named them, are
    /// <b>deleted</b> rather than left behind: the page no longer renders a plain
    /// <c>&lt;span class="avatar"&gt;</c> anywhere, so a surviving rule would be dead and unreachable
    /// while still reading as the source of truth.
    /// </para>
    /// </summary>
    public static string AvatarBg(string role) => role.ToLowerInvariant() switch
    {
        "owner" => "color-mix(in srgb, var(--mud-palette-primary) 14%, transparent)",
        "admin" => "color-mix(in srgb, var(--mud-palette-tertiary) 18%, transparent)",
        "user" => "color-mix(in srgb, var(--mud-palette-secondary) 16%, transparent)",
        _ => "var(--mud-palette-action-disabled-background)",
    };

    /// <inheritdoc cref="AvatarBg" />
    public static string AvatarFg(string role) => role.ToLowerInvariant() switch
    {
        "owner" => "var(--mud-palette-primary)",
        "admin" => "var(--mud-palette-tertiary)",
        "user" => "var(--mud-palette-secondary)",
        _ => "var(--mud-palette-text-secondary)",
    };
}
