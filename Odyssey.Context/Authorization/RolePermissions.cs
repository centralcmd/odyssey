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

    /// <summary>
    /// The Recipes module (issue #525). Every role holding a record claim also holds <c>recipes.tags.read</c>,
    /// which the tag-icon pin in <c>AuthorizationPolicyTests</c> relies on (issue #345).
    /// </summary>
    public static readonly string[] RecipesModuleClaims =
    [
        RecipesCreate,
        RecipesRead,
        RecipesUpdate,
        RecipesDelete,
        RecipeTagsCreate,
        RecipeTagsRead,
        RecipeTagsUpdate,
        RecipeTagsDelete,
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
        LocationsCreate,
        LocationsRead,
        LocationsUpdate,
        LocationsDelete,
    ];

    /// <summary>
    /// The weather claims every role holds (issue #348 §7.2). Configuring weather and purging its history
    /// are Admin-only and reach Admin through <see cref="AllClaims"/> alone.
    /// </summary>
    public static readonly string[] WeatherReaderClaims =
    [
        WeatherRead,
        WeatherPreferencesUpdate,
    ];

    /// <summary>
    /// The news reader's claims (issue #470 §7.2), all of which Admin and Owner hold. User keeps the household's read
    /// state and article tags (<see cref="NewsReaderClaims"/>); Guest only reads.
    /// </summary>
    public static readonly string[] NewsModuleClaims =
    [
        NewsRead,
        NewsUpdate,
        NewsFeedsCreate,
        NewsFeedsUpdate,
        NewsFeedsDelete,
        NewsTagsCreate,
        NewsTagsUpdate,
        NewsTagsDelete,
    ];

    /// <summary>What User holds of <see cref="NewsModuleClaims"/>: reading, read state and article tags.</summary>
    public static readonly string[] NewsReaderClaims =
    [
        NewsRead,
        NewsUpdate,
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
        InstrumentsCreate,
        InstrumentsRead,
        InstrumentsUpdate,
        InstrumentsDelete,
        InstrumentPricesCreate,
        InstrumentPricesRead,
        InstrumentPricesUpdate,
        InstrumentPricesDelete,
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
        FilesExportAll,
        UsersDelete,
        TaxesCreate,
        TaxesRead,
        TaxesUpdate,
        TaxesDelete,
        AccountsEstimatesRead,
        AccountsEstimatesWrite,
        PropertiesCreate,
        PropertiesRead,
        PropertiesUpdate,
        PropertiesDelete,
        PropertiesEstimatesRead,
        PropertiesEstimatesWrite,
        ContractsCreate,
        ContractsRead,
        ContractsUpdate,
        ContractsDelete,
        FileAnalysisAudit,
        ..JournalModuleClaims,
        ..PhotosModuleClaims,
        ..RecipesModuleClaims,
        ..CalendarModuleClaims,
        ..WeatherReaderClaims,
        WeatherPointsRead,
        WeatherPointsUpdate,
        WeatherPointsDelete,
        ProfileImagesRead,
        SystemSettingsRead,
        SystemSettingsUpdate,
        SystemSettingsSecurityUpdate,
        FileStorageRead,
        ..NewsModuleClaims,
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
        InstrumentsCreate,
        InstrumentsRead,
        InstrumentsUpdate,
        InstrumentsDelete,
        InstrumentPricesCreate,
        InstrumentPricesRead,
        InstrumentPricesUpdate,
        InstrumentPricesDelete,
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
        TaxesCreate,
        TaxesRead,
        TaxesUpdate,
        TaxesDelete,
        AccountsEstimatesRead,
        AccountsEstimatesWrite,
        PropertiesCreate,
        PropertiesRead,
        PropertiesUpdate,
        PropertiesDelete,
        PropertiesEstimatesRead,
        PropertiesEstimatesWrite,
        ContractsCreate,
        ContractsRead,
        ContractsUpdate,
        ContractsDelete,
        ..JournalModuleClaims,
        ..PhotosModuleClaims,
        ..RecipesModuleClaims,
        ..CalendarModuleClaims,
        ..WeatherReaderClaims,
        ProfileImagesRead,
        ..NewsModuleClaims,
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
        InstrumentsRead,
        InstrumentPricesRead,
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
        TaxesRead,
        AccountsEstimatesRead,
        PropertiesRead,
        PropertiesEstimatesRead,
        ContractsRead,
        ..JournalModuleClaims,
        ..PhotosModuleClaims,
        ..RecipesModuleClaims,
        ..CalendarModuleClaims,
        ..WeatherReaderClaims,
        ProfileImagesRead,
        ..NewsReaderClaims,
    ];

    public static readonly string[] GuestClaims =
    [
        AccountsRead,
        BudgetsRead,
        TransactionsRead,
        TransactionTagsRead,
        ContactsRead,
        CurrenciesRead,
        ExchangeRatesRead,
        InstrumentsRead,
        InstrumentPricesRead,
        UserPreferencesRead,
        UserPreferencesCreate,
        UserPreferencesUpdate,
        UserPreferencesDelete,
        FilesRead,
        TaxesRead,
        AccountsEstimatesRead,
        PropertiesRead,
        PropertiesEstimatesRead,
        // Household locations (issue #350 §7.3): Guest is an invited member of the household and the list
        // is the household's own, so Guest reads it — addresses and descriptions included. Narrowing that
        // later is a change here, not in code.
        LocationsRead,
        // Weather (issue #348 §7.2): reading it and choosing one's own weather location. weather.read
        // implies locations.read on every role (AuthorizationPolicyTests pins that), which Guest holds.
        ..WeatherReaderClaims,
        // Granted to Guest too (issue #94 §10.5). The picture identifies a person the caller already
        // meets by name on shared records, and the point of the claim is that it is REVOCABLE per role
        // without a code change — not that it narrows anything today.
        ProfileImagesRead,
        // News (issue #470 §7.2): the household's shared feeds, read only.
        NewsRead,
        // Recipes (issue #525 §7.2): the household's recipes and their tags, read only. Photos stay withheld,
        // since Guest holds no photos.read.
        RecipesRead,
        RecipeTagsRead,
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
