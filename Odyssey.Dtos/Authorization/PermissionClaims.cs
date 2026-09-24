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
    public const string AccountsEstimatesRead = "accounts.estimates.read";
    public const string AccountsEstimatesWrite = "accounts.estimates.write";

    // Properties (issue #167) — a family of their own rather than a reuse of accounts.*: a claim is a
    // revocation lever that must exist BEFORE release, and a property is meant to outlive some account
    // types.
    public const string PropertiesCreate = "properties.create";
    public const string PropertiesRead = "properties.read";
    public const string PropertiesUpdate = "properties.update";
    public const string PropertiesDelete = "properties.delete";
    public const string PropertiesEstimatesRead = "properties.estimates.read";
    public const string PropertiesEstimatesWrite = "properties.estimates.write";

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

    public const string ContractsCreate = "contracts.create";
    public const string ContractsRead = "contracts.read";
    public const string ContractsUpdate = "contracts.update";
    public const string ContractsDelete = "contracts.delete";

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

    /// <summary>
    /// Read another user's profile picture (issue #94 §10.5). A deliberately <b>low-tier</b> claim,
    /// granted to <b>every</b> role including Guest, so its effective reach equals "any authenticated
    /// caller" — what it buys is a revocation lever that exists <i>before</i> release, since claim
    /// values are baked into the auth cookie at sign-in and retrofitting one later de-authorizes live
    /// sessions.
    ///
    /// <para>
    /// The tempting argument for claim-free access — a caller can only fetch a picture for someone they
    /// already see <i>named</i> — is <b>false</b> here, and that is why the claim exists.
    /// <c>ExistingTransactionFile.AttachedByUserId</c>, <c>ExistingAccountFile.AttachedByUserId</c> and
    /// the nested <c>ExistingFileMetadata.UploadedByUserId</c> returned raw user ids with no name
    /// attached, and Guest holds <c>transactions.read</c>, <c>accounts.read</c> and <c>files.read</c>,
    /// so a Guest could harvest ids and build a face↔id mapping for people the application never named
    /// to them. Issue #106 routed those fields through <c>IUserDisplayNameResolver</c>, so each now
    /// carries a claim-conditional label beside its id.
    /// </para>
    ///
    /// <para>
    /// It covers all five file surfaces, not the two the issue named: the contract, tax-statement and
    /// policy-renewal ones carried the identical pair, and <c>taxes.read</c> and <c>budgets.read</c>
    /// matter as much as <c>transactions.read</c> here because Guest holds those too. The family is
    /// pinned by <c>IAttributedFile</c> and its guard tests rather than by memory, so a sixth file DTO
    /// cannot reintroduce a bare id.
    /// </para>
    ///
    /// <para>
    /// That does <b>not</b> retire the claim. It is a revocation lever that had to exist before release
    /// regardless — claim values are baked into the auth cookie at sign-in, so a claim added later
    /// de-authorizes live sessions — and revoking it from Guest stays the supported way to narrow who
    /// sees colleagues' pictures. What changed is that it is no longer the <i>only</i> control: the ids
    /// it was compensating for are gone from the file surfaces.
    /// </para>
    ///
    /// <para>
    /// The two <i>write</i> endpoints stay claim-free and self-scoped, like <c>PUT /api/profile</c>.
    /// </para>
    /// </summary>
    public const string ProfileImagesRead = "profile-images.read";

    // System settings (issue #349). Admin-only across all three — never granted to Owner/User/Guest.
    // Read is uniform sensitivity (no PII, no IDOR surface); the write claim is split by sensitivity:
    // Update covers the cosmetic/policy fields, SecurityUpdate covers the three
    // authentication-perimeter fields (2FA persistence, registration approval, email confirmation).
    public const string SystemSettingsRead = "system-settings.read";
    public const string SystemSettingsUpdate = "system-settings.update";
    public const string SystemSettingsSecurityUpdate = "system-settings.security.update";
}
