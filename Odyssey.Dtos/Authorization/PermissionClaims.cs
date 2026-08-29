namespace Odyssey.Dtos.Authorization;

/// <summary>
/// The permission-claim vocabulary shared by the API, the Blazor client and the test suites — the
/// single definition of all 98 <c>permission</c> claim values.
/// </summary>
/// <remarks>
/// This lives in <c>Odyssey.Dtos</c> rather than in the API or the client because both sides
/// must agree on the exact strings: the server authorizes against them and the client registers one
/// authorization policy per constant (reflectively, in <c>Odyssey.Client/Program.cs</c>). It used to
/// be two hand-maintained copies, where adding a claim on the server and forgetting the client copy
/// would silently leave the new claim with no client policy.
///
/// Which claims each <b>role</b> holds is a server concern and is not here — see
/// <c>Odyssey.Context.Authorization.RoleClaims</c>.
/// </remarks>
public static class PermissionClaims
{
    public const string Type = "permission";

    public const string AccountsCreate = "accounts.create";
    public const string AccountsRead = "accounts.read";
    public const string AccountsUpdate = "accounts.update";
    public const string AccountsDelete = "accounts.delete";
    public const string AccountsTermsRead = "accounts.terms.read";
    public const string AccountsTermsWrite = "accounts.terms.write";
    public const string AccountsEstimatesRead = "accounts.estimates.read";
    public const string AccountsEstimatesWrite = "accounts.estimates.write";

    public const string BudgetsCreate = "budgets.create";
    public const string BudgetsRead = "budgets.read";
    public const string BudgetsUpdate = "budgets.update";
    public const string BudgetsDelete = "budgets.delete";
    
    public const string TransactionsCreate = "transactions.create";
    public const string TransactionsRead = "transactions.read";
    public const string TransactionsUpdate = "transactions.update";
    public const string TransactionsDelete = "transactions.delete";
    
    public const string TransactionTagsCreate = "transactions.tags.create";
    public const string TransactionTagsRead = "transactions.tags.read";
    public const string TransactionTagsUpdate = "transactions.tags.update";
    public const string TransactionTagsDelete = "transactions.tags.delete";

    public const string ContactsCreate = "contacts.create";
    public const string ContactsRead = "contacts.read";
    public const string ContactsUpdate = "contacts.update";
    public const string ContactsDelete = "contacts.delete";

    public const string CurrenciesCreate = "currencies.create";
    public const string CurrenciesRead = "currencies.read";
    public const string CurrenciesUpdate = "currencies.update";
    public const string CurrenciesDelete = "currencies.delete";

    public const string ExchangeRatesCreate = "exchangerates.create";
    public const string ExchangeRatesRead = "exchangerates.read";
    public const string ExchangeRatesUpdate = "exchangerates.update";
    public const string ExchangeRatesDelete = "exchangerates.delete";

    public const string UserPreferencesCreate = "user-preferences.create";
    public const string UserPreferencesRead = "user-preferences.read";
    public const string UserPreferencesUpdate = "user-preferences.update";
    public const string UserPreferencesDelete = "user-preferences.delete";

    public const string FilesCreate = "files.create";
    public const string FilesRead = "files.read";
    public const string FilesUpdate = "files.update";
    public const string FilesDelete = "files.delete";

    public const string UsersManage = "users.manage";
    public const string UsersRead = "users.read";
    public const string UsersUpdate = "users.update";
    public const string UsersDelete = "users.delete";

    public const string FileAnalysisCreate = "file-analysis.create";
    public const string FileAnalysisRead = "file-analysis.read";
    public const string FileAnalysisImport = "file-analysis.import";

    // Admin-only accountability surface — the external-AI transfer audit trail. Granted to Admin
    // only (via AllClaims), never to Owner/User/Guest, so it can diverge from users.read.
    public const string FileAnalysisAudit = "file-analysis.audit";

    public const string DataExport = "data.export";

    public const string FilesExportAll = "files.export-all";

    public const string TaxesCreate = "taxes.create";
    public const string TaxesRead = "taxes.read";
    public const string TaxesUpdate = "taxes.update";
    public const string TaxesDelete = "taxes.delete";

    public const string InsuranceCreate = "insurance.create";
    public const string InsuranceRead = "insurance.read";
    public const string InsuranceUpdate = "insurance.update";
    public const string InsuranceDelete = "insurance.delete";

    public const string ContractsCreate = "contracts.create";
    public const string ContractsRead = "contracts.read";
    public const string ContractsUpdate = "contracts.update";
    public const string ContractsDelete = "contracts.delete";

    public const string SubscriptionsCreate = "subscriptions.create";
    public const string SubscriptionsRead = "subscriptions.read";
    public const string SubscriptionsUpdate = "subscriptions.update";
    public const string SubscriptionsDelete = "subscriptions.delete";

    // Journal module (issue #311). Guest is granted none of these — the whole module is 403 for Guest.
    public const string JournalCreate = "journal.create";
    public const string JournalRead = "journal.read";
    public const string JournalUpdate = "journal.update";
    public const string JournalDelete = "journal.delete";

    public const string JournalTagsCreate = "journal.tags.create";
    public const string JournalTagsRead = "journal.tags.read";
    public const string JournalTagsUpdate = "journal.tags.update";
    public const string JournalTagsDelete = "journal.tags.delete";

    public const string TasksCreate = "tasks.create";
    public const string TasksRead = "tasks.read";
    public const string TasksUpdate = "tasks.update";
    public const string TasksDelete = "tasks.delete";

    public const string TaskTagsCreate = "tasks.tags.create";
    public const string TaskTagsRead = "tasks.tags.read";
    public const string TaskTagsUpdate = "tasks.tags.update";
    public const string TaskTagsDelete = "tasks.tags.delete";

    // Photos module (issue #321). Guest is granted none of these — the whole module is 403 for Guest.
    public const string PhotosCreate = "photos.create";
    public const string PhotosRead = "photos.read";
    public const string PhotosUpdate = "photos.update";
    public const string PhotosDelete = "photos.delete";

    public const string PhotoTagsCreate = "photos.tags.create";
    public const string PhotoTagsRead = "photos.tags.read";
    public const string PhotoTagsUpdate = "photos.tags.update";
    public const string PhotoTagsDelete = "photos.tags.delete";

    public const string PhotoAlbumsCreate = "photos.albums.create";
    public const string PhotoAlbumsRead = "photos.albums.read";
    public const string PhotoAlbumsUpdate = "photos.albums.update";
    public const string PhotoAlbumsDelete = "photos.albums.delete";

    // Calendar module (issue #323). A single claim group covers Calendar, CalendarEvent and
    // RecurrencePattern uniformly — they're always accessed in the context of calendar read/write and
    // don't warrant their own sub-resource claims. Guest is granted none of these — the whole module
    // is 403 for Guest.
    public const string CalendarCreate = "calendar.create";
    public const string CalendarRead = "calendar.read";
    public const string CalendarUpdate = "calendar.update";
    public const string CalendarDelete = "calendar.delete";

    // System settings (issue #349). Admin-only across all three — never granted to Owner/User/Guest.
    // Read is uniform sensitivity (no PII, no IDOR surface); the write claim is split by sensitivity:
    // Update covers the two cosmetic/policy fields (Insurance), SecurityUpdate covers the three
    // authentication-perimeter fields (2FA persistence, registration approval, email confirmation).
    public const string SystemSettingsRead = "system-settings.read";
    public const string SystemSettingsUpdate = "system-settings.update";
    public const string SystemSettingsSecurityUpdate = "system-settings.security.update";
}
