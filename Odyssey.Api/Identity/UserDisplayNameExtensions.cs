namespace Odyssey.Api.Identity;

/// <summary>
/// Label lookups over the map <see cref="IUserDisplayNameResolver.ResolveAsync(System.Security.Claims.ClaimsPrincipal, IEnumerable{string?}, CancellationToken)"/>
/// returns, for attribution columns that can be null.
/// </summary>
public static class UserDisplayNameExtensions
{
    /// <summary>
    /// The label for a column that always HAD an author, whether or not that account still exists.
    /// </summary>
    /// <remarks>
    /// <c>CreatedByUserId</c> and friends are nullable in the schema because the user-attribution
    /// foreign keys null them out when the account is deleted (see <c>OdysseyContext</c>), not because
    /// a row can be authorless. A null id therefore means exactly what an unresolvable id means — the
    /// author is gone — so both answer <see cref="UserDisplayNameResolver.UnknownUser"/>.
    ///
    /// <para>
    /// Contrast a genuinely OPTIONAL column such as <c>UpdatedByUserId</c>, where null is ambiguous:
    /// it means either "never updated" or "the updater's account is gone". Those keep answering
    /// <see langword="null"/> and leave the rendering to the client, which is the behaviour that
    /// shipped before the attribution keys existed.
    /// </para>
    /// </remarks>
    public static string NameForAuthor(this IReadOnlyDictionary<string, string> names, string? userId) =>
        userId is null
            ? UserDisplayNameResolver.UnknownUser
            : names.GetValueOrDefault(userId, UserDisplayNameResolver.UnknownUser);

    /// <summary>
    /// The label for an optional attribution column: <see langword="null"/> stays null, because the
    /// absence of a value is itself meaningful (see <see cref="NameForAuthor"/>).
    /// </summary>
    public static string? NameForOptional(this IReadOnlyDictionary<string, string> names, string? userId) =>
        userId is null ? null : names.GetValueOrDefault(userId);
}
