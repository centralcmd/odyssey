using Odyssey.Dtos.Authorization;

// The claim constants are referenced bare below to keep the role lists readable.
using static Odyssey.Dtos.Authorization.PermissionClaims;

namespace Odyssey.Context.Authorization;

/// <summary>
/// Which <see cref="PermissionClaims"/> each role holds. A server-side policy decision — deliberately
/// not in <c>Odyssey.Dtos</c> alongside the claim vocabulary, so the browser client never ships
/// (or reasons about) the role-to-claim mapping.
/// </summary>
/// <remarks>
/// Seeded into <c>AspNetRoleClaims</c> by <c>OdysseyContext</c> and by the claim migrations. The
/// permission claims baked into a user's auth cookie at login come from these, so a change here only
/// reaches existing sessions after a sign-out/sign-in.
/// </remarks>
public static class RolePermissions
{
    public static readonly string[] CalendarModuleClaims =
    [
        CalendarCreate,
        CalendarRead,
        CalendarUpdate,
        CalendarDelete,
    ];

    public static readonly string[] PhotosModuleClaims =
    [
        PhotosCreate,
        PhotosRead,
        PhotosUpdate,
        PhotosDelete,
        PhotoTagsCreate,
        PhotoTagsRead,
        PhotoTagsUpdate,
        PhotoTagsDelete,
        PhotoAlbumsCreate,
        PhotoAlbumsRead,
        PhotoAlbumsUpdate,
        PhotoAlbumsDelete,
    ];

    public static readonly string[] JournalModuleClaims =
    [
        JournalCreate,
        JournalRead,
        JournalUpdate,
        JournalDelete,
        JournalTagsCreate,
        JournalTagsRead,
        JournalTagsUpdate,
        JournalTagsDelete,
        TasksCreate,
        TasksRead,
        TasksUpdate,
        TasksDelete,
        TaskTagsCreate,
        TaskTagsRead,
        TaskTagsUpdate,
        TaskTagsDelete,
    ];

    public static readonly string[] AllClaims =
    [
        AccountsCreate,
        AccountsRead,
        AccountsUpdate,
        AccountsDelete,
        BudgetsCreate,
        BudgetsRead,
        BudgetsUpdate,
        BudgetsDelete,
        TransactionsCreate,
        TransactionsRead,
        TransactionsUpdate,
        TransactionsDelete,
        TransactionTagsCreate,
        TransactionTagsRead,
        TransactionTagsUpdate,
        TransactionTagsDelete,
        ContactsCreate,
        ContactsRead,
        ContactsUpdate,
        ContactsDelete,
        CurrenciesCreate,
        CurrenciesRead,
        CurrenciesUpdate,
        CurrenciesDelete,
        ExchangeRatesCreate,
        ExchangeRatesRead,
        ExchangeRatesUpdate,
        ExchangeRatesDelete,
        UserPreferencesCreate,
        UserPreferencesRead,
        UserPreferencesUpdate,
        UserPreferencesDelete,
        FilesCreate,
        FilesRead,
        FilesUpdate,
        FilesDelete,
        UsersManage,
        FileAnalysisCreate,
        FileAnalysisRead,
        FileAnalysisImport,
        UsersRead,
        UsersUpdate,
        DataExport,
        FilesExportAll,
        UsersDelete,
        AccountsTermsRead,
        AccountsTermsWrite,
        TaxesCreate,
        TaxesRead,
        TaxesUpdate,
        TaxesDelete,
        AccountsEstimatesRead,
        AccountsEstimatesWrite,
        ContractsCreate,
        ContractsRead,
        ContractsUpdate,
        ContractsDelete,
        FileAnalysisAudit,
        ..JournalModuleClaims,
        ..PhotosModuleClaims,
        ..CalendarModuleClaims,
        ProfileImagesRead,
        SystemSettingsRead,
        SystemSettingsUpdate,
        SystemSettingsSecurityUpdate,
    ];

    public static readonly string[] AdminClaims =
    [
        ..AllClaims,
    ];
    
    public static readonly string[] OwnerClaims =
    [
        AccountsCreate,
        AccountsRead,
        AccountsUpdate,
        AccountsDelete,
        BudgetsCreate,
        BudgetsRead,
        BudgetsUpdate,
        BudgetsDelete,
        TransactionsCreate,
        TransactionsRead,
        TransactionsUpdate,
        TransactionsDelete,
        TransactionTagsCreate,
        TransactionTagsRead,
        TransactionTagsUpdate,
        TransactionTagsDelete,
        ContactsCreate,
        ContactsRead,
        ContactsUpdate,
        ContactsDelete,
        CurrenciesCreate,
        CurrenciesRead,
        CurrenciesUpdate,
        CurrenciesDelete,
        ExchangeRatesCreate,
        ExchangeRatesRead,
        ExchangeRatesUpdate,
        ExchangeRatesDelete,
        UserPreferencesCreate,
        UserPreferencesRead,
        UserPreferencesUpdate,
        UserPreferencesDelete,
        FilesCreate,
        FilesRead,
        FilesUpdate,
        FilesDelete,
        FileAnalysisCreate,
        FileAnalysisRead,
        FileAnalysisImport,
        AccountsTermsRead,
        AccountsTermsWrite,
        TaxesCreate,
        TaxesRead,
        TaxesUpdate,
        TaxesDelete,
        AccountsEstimatesRead,
        AccountsEstimatesWrite,
        ContractsCreate,
        ContractsRead,
        ContractsUpdate,
        ContractsDelete,
        ..JournalModuleClaims,
        ..PhotosModuleClaims,
        ..CalendarModuleClaims,
        ProfileImagesRead,
    ];

    public static readonly string[] UserClaims =
    [
        AccountsRead,
        BudgetsRead,
        TransactionsCreate,
        TransactionsRead,
        TransactionsUpdate,
        TransactionsDelete,
        TransactionTagsRead,
        ContactsRead,
        CurrenciesRead,
        ExchangeRatesRead,
        UserPreferencesRead,
        UserPreferencesCreate,
        UserPreferencesUpdate,
        UserPreferencesDelete,
        FilesRead,
        FilesCreate,
        FilesUpdate,
        FilesDelete,
        FileAnalysisCreate,
        FileAnalysisRead,
        FileAnalysisImport,
        AccountsTermsRead,
        TaxesRead,
        AccountsEstimatesRead,
        ContractsRead,
        ..JournalModuleClaims,
        ..PhotosModuleClaims,
        ..CalendarModuleClaims,
        ProfileImagesRead,
    ];

    public static readonly string[] GuestClaims =
    [
        AccountsRead,
        AccountsTermsRead,
        BudgetsRead,
        TransactionsRead,
        TransactionTagsRead,
        ContactsRead,
        CurrenciesRead,
        ExchangeRatesRead,
        UserPreferencesRead,
        UserPreferencesCreate,
        UserPreferencesUpdate,
        UserPreferencesDelete,
        FilesRead,
        TaxesRead,
        AccountsEstimatesRead,
        // Granted to Guest too (issue #94 §10.5). The picture identifies a person the caller already
        // meets by name on shared records, and the point of the claim is that it is REVOCABLE per role
        // without a code change — not that it narrows anything today.
        ProfileImagesRead,
    ];

    /// <summary>
    /// The single enumeration of every role and the claims it holds (issue #90 G9).
    ///
    /// <para>
    /// Before this existed, the four pairings were written out by hand in <c>RoleClaimSeeder</c> and
    /// again at four separate sites in <c>AuthorizationPolicyTests</c>. A guard written in that house
    /// style cannot see a fifth role: someone adding a narrow "Viewer" would touch this class and the
    /// seeder, have no reason to touch a test, and the guards would keep passing vacuously over the
    /// four they name while the new role made their conclusion void. Issue #90 §10.3 accepts a
    /// disclosure property <b>on the strength of one of those guards</b>, so a guard that can fail
    /// open is not a tidiness problem.
    /// </para>
    ///
    /// <para>
    /// <see cref="AllClaims"/> is deliberately <b>not</b> a member: it is the vocabulary-completeness
    /// list, not a role. It is held by no principal, and including it would make every "does any role
    /// do X" guard trivially true.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<(string RoleId, string RoleName, string[] Claims)> RoleClaimMap =
    [
        (RoleDefinitions.AdminId, RoleDefinitions.Admin, AdminClaims),
        (RoleDefinitions.OwnerId, RoleDefinitions.Owner, OwnerClaims),
        (RoleDefinitions.UserId, RoleDefinitions.User, UserClaims),
        (RoleDefinitions.GuestId, RoleDefinitions.Guest, GuestClaims),
    ];
}
