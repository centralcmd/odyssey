using Microsoft.AspNetCore.Components;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages;

public partial class UserEditPanel
{
    /// <summary>The row being edited. Changing it reseeds the working copy.</summary>
    [Parameter, EditorRequired] public ExistingUser User { get; set; } = default!;

    /// <summary>The roles offered by the role picker.</summary>
    [Parameter] public IReadOnlyList<ExistingRole> Roles { get; set; } = [];

    /// <summary>Whether the page's save is in flight — disables the whole panel's actions.</summary>
    [Parameter] public bool Saving { get; set; }

    /// <summary>The failure message from the last save attempt, if any.</summary>
    [Parameter] public string? Error { get; set; }

    /// <summary>Raised when the reviewer abandons the edit.</summary>
    [Parameter] public EventCallback OnCancel { get; set; }

    /// <summary>Raised with the finished working copy. The page owns the API calls.</summary>
    [Parameter] public EventCallback<Draft> OnSave { get; set; }

    /// <summary>The editable fields, as staged in this panel before they are sent.</summary>
    public sealed record Draft(bool EmailConfirmed, bool Enabled, string Role);

    // ── Working copy ──
    // Seeded from User and reseeded whenever the panel is pointed at a different row, so reopening
    // an edit never shows the previous row's staged values.
    private string? _seededId;
    private bool _emailConfirmed;
    private bool _enabled;
    private string _role = string.Empty;

    protected override void OnParametersSet()
    {
        if (_seededId == User.Id)
            return;

        _seededId = User.Id;
        _emailConfirmed = User.EmailConfirmed;
        _enabled = User.Enabled;
        _role = User.Role;
    }

    private bool HasChanges =>
        _emailConfirmed != User.EmailConfirmed
        || _enabled != User.Enabled
        || !string.Equals(_role, User.Role, StringComparison.Ordinal);

    /// <summary>
    /// A change is "disruptive" if it locks the user out and ends their sessions: disabling an enabled
    /// account. The guard surfaces these before they're applied.
    /// </summary>
    /// <remarks>
    /// The wording is deliberately bounded rather than immediate. Disabling rotates the account's security
    /// stamp (issue #442), and <c>SecurityStampValidator</c> re-checks live cookies on a one-minute
    /// interval — so an existing session ends within the minute, not on its next request.
    /// </remarks>
    private IReadOnlyList<string> DisruptiveChanges =>
        User.Enabled && !_enabled
            ? ["Disable this account — the user is locked out, and their active sessions end within a minute."]
            : [];

    private Task SubmitAsync() => OnSave.InvokeAsync(new Draft(_emailConfirmed, _enabled, _role));
}
