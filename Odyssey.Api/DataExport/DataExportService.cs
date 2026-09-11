using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Odyssey.Dtos;
using Odyssey.Context;
using Microsoft.EntityFrameworkCore;
using FinanceDtos = Odyssey.Dtos.Finance;

namespace Odyssey.Api.DataExport;

/// <summary>Metadata fixed before the first byte is written, so the response headers can be sent
/// ahead of the body. <see cref="ExportedAt"/> is both the document's timestamp and the source of the
/// download filename, so the two cannot drift.</summary>
public sealed record DataExportHeader(DateTime ExportedAt, string ExportedByUserId)
{
    public string FileName =>
        $"odyssey-database-export-{ExportedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}Z.json";
}

/// <summary>What was written, for the success log — never the payload itself.</summary>
public sealed record DataExportSummary(long ByteCount, IReadOnlyDictionary<string, int> RowCounts);

/// <summary>
/// Streams the finance database export document (issue #160). Every table is queried separately,
/// <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/>, projected to a flat
/// export DTO (scalar + foreign-key columns only — never a navigation graph), and ordered
/// deterministically by primary key. File blob payloads are never loaded: <see cref="FileMetadata"/>
/// is projected without its <c>FileBlob.Content</c>, and the <c>FileBlob</c>, file-analysis job, and
/// candidate tables are not exported at all.
///
/// Issue #33: the covered set is a deliberate list, not every table in the context — the contact
/// detail tables have their own vCard export and the journal side has its own surfaces. What that
/// list must not do is omit a Finance table silently, which is what it was doing for insurance,
/// contracts, tax statements, subscriptions and the two account side-tables. Anything genuinely left
/// out belongs in <see cref="DataExportExclusions.ExcludedTables"/> with its reason; an omission that
/// is stated is a different thing from one that is merely absent.
///
/// Issue #395: rows are written to the response as they are read rather than materialized. Each table
/// is enumerated with <see cref="EntityFrameworkQueryableExtensions.AsAsyncEnumerable{TResult}"/> and
/// serialized row by row through a writer that drains to the response every
/// <see cref="FlushThresholdBytes"/>, so peak managed memory is that fixed buffer plus one row —
/// not the whole dataset, and certainly not the whole dataset twice. The emitted bytes are identical
/// to what <see cref="JsonSerializer"/> would produce for the equivalent
/// <see cref="DataExportDocument"/>: the envelope property names come from <c>nameof</c> on that type
/// run through the same camelCase naming policy, and every row is handed to the same serializer with
/// the same options.
///
/// Entity enums are cast explicitly to their <c>Odyssey.Dtos.Finance</c> counterparts (issue #392).
/// The two copies are numerically identical today, so the casts are wire-neutral; they exist so that
/// a member added to one copy and not the other fails to compile here instead of silently changing
/// what an exported number means.
/// </summary>
public sealed class DataExportService
{
    /// <summary>camelCase property names; enums serialize as their stored integer (no string-enum
    /// converter); decimals as JSON numbers and UTC DateTimes as ISO-8601 — all System.Text.Json
    /// defaults. Shared with the tests that pin the wire format.</summary>
    public static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>How much serialized output may sit in the writer's buffer before it is pushed to the
    /// response. The cap is what keeps peak memory flat regardless of database size.</summary>
    private const int FlushThresholdBytes = 32 * 1024;

    private static readonly JsonNamingPolicy PropertyNamingPolicy =
        ExportJsonOptions.PropertyNamingPolicy ?? JsonNamingPolicy.CamelCase;

    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public DataExportService(OdysseyContext context, TimeProvider timeProvider)
    {
        this.context = context;
        this.timeProvider = timeProvider;
    }

    /// <summary>Stamps the export. Called before the response headers go out, since the filename is
    /// derived from the same instant the document carries.</summary>
    public DataExportHeader CreateHeader(string exportedByUserId) =>
        new(timeProvider.GetUtcNow().UtcDateTime, exportedByUserId);

    /// <summary>
    /// Writes the whole export document to <paramref name="output"/>, returning the byte count and
    /// per-table row counts for the caller's log line. The completeness sentinel is written last, so
    /// a failure part-way through leaves a document the reader can tell is partial.
    /// </summary>
    public async Task<DataExportSummary> WriteExportAsync(
        Stream output,
        DataExportHeader header,
        CancellationToken cancellationToken)
    {
        // Nothing is worth writing to a client that has already gone away.
        cancellationToken.ThrowIfCancellationRequested();

        await using var export = new ExportWriter(output);
        var writer = export.Json;
        var rowCounts = new Dictionary<string, int>();

        writer.WriteStartObject();
        writer.WriteNumber(JsonName(nameof(DataExportDocument.SchemaVersion)), DataExportDefaults.SchemaVersion);
        writer.WriteString(JsonName(nameof(DataExportDocument.ExportedAt)), header.ExportedAt);
        writer.WriteString(JsonName(nameof(DataExportDocument.ExportedByUserId)), header.ExportedByUserId);
        writer.WriteString(JsonName(nameof(DataExportDocument.Format)), DataExportDefaults.Format);

        writer.WritePropertyName(JsonName(nameof(DataExportDocument.Exclusions)));
        JsonSerializer.Serialize(writer, DataExportDefaults.Exclusions, ExportJsonOptions);

        writer.WritePropertyName(JsonName(nameof(DataExportDocument.Databases)));
        writer.WriteStartObject();
        writer.WritePropertyName(JsonName(nameof(DataExportDatabases.Finance)));
        writer.WriteStartObject();
        await WriteFinanceTablesAsync(export, rowCounts, cancellationToken);
        writer.WriteEndObject();
        writer.WriteEndObject();

        // Last property of the document: only reached when every table above was written in full.
        writer.WriteBoolean(JsonName(nameof(DataExportDocument.Complete)), true);
        writer.WriteEndObject();

        await export.DrainAsync(cancellationToken, force: true);
        return new DataExportSummary(export.ByteCount, rowCounts);
    }

    // One table at a time: the DbContexts are not thread-safe, and a streaming read holds a reader
    // open on the connection, so these cannot overlap.
    private async Task WriteFinanceTablesAsync(
        ExportWriter export,
        Dictionary<string, int> rowCounts,
        CancellationToken cancellationToken)
    {
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.Accounts), AccountsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.AccountTerms), AccountTermsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.Budgets), BudgetsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.BudgetItems), BudgetItemsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.Contacts), ContactsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.Currencies), CurrenciesQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.ExchangeRates), ExchangeRatesQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.Transactions), TransactionsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.TransactionTags), TransactionTagsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.FileMetadata), FileMetadataQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.AccountFiles), AccountFilesQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.TransactionFiles), TransactionFilesQuery(), cancellationToken);

        // Issue #33 — order matches FinanceDatabaseExport, which is the wire order.
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.AccountEstimates), AccountEstimatesQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.AccountSmartTags), AccountSmartTagsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.TaxStatements), TaxStatementsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.TaxStatementTags), TaxStatementTagsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.TaxStatementFiles), TaxStatementFilesQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.InsurancePolicies), InsurancePoliciesQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.InsurancePolicyInsurers), InsurancePolicyInsurersQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.InsurancePolicyInsuredAccounts), InsurancePolicyInsuredAccountsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.InsurancePolicyInsuredContacts), InsurancePolicyInsuredContactsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.InsurancePolicyBeneficiaries), InsurancePolicyBeneficiariesQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.PolicyRenewals), PolicyRenewalsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.PolicyRenewalFiles), PolicyRenewalFilesQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.Contracts), ContractsQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.ContractParties), ContractPartiesQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.ContractFiles), ContractFilesQuery(), cancellationToken);
        await WriteTableAsync(export, rowCounts, nameof(FinanceDatabaseExport.Subscriptions), SubscriptionsQuery(), cancellationToken);
    }

    /// <summary>
    /// Writes one table as a JSON array, serializing each row as it arrives from the database and
    /// recording the row count under the table's CLR name (the key the success log reports). Draining
    /// between rows is what bounds memory: without it the buffer would grow to the whole table.
    /// </summary>
    private static async Task WriteTableAsync<T>(
        ExportWriter export,
        Dictionary<string, int> rowCounts,
        string tableName,
        IQueryable<T> query,
        CancellationToken cancellationToken)
    {
        export.Json.WritePropertyName(JsonName(tableName));
        export.Json.WriteStartArray();

        var count = 0;
        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            JsonSerializer.Serialize(export.Json, row, ExportJsonOptions);
            count++;
            await export.DrainAsync(cancellationToken);
        }

        export.Json.WriteEndArray();
        rowCounts[tableName] = count;
    }

    private static string JsonName(string propertyName) => PropertyNamingPolicy.ConvertName(propertyName);

    /// <summary>
    /// Bounded-memory JSON sink. The <see cref="Utf8JsonWriter"/> deliberately does not sit on the
    /// response stream directly: <see cref="JsonSerializer.Serialize{TValue}(Utf8JsonWriter, TValue,
    /// JsonSerializerOptions)"/> flushes synchronously when it finishes a value, and Kestrel rejects
    /// synchronous writes (<c>AllowSynchronousIO</c> is off). Writing into an
    /// <see cref="ArrayBufferWriter{T}"/> instead makes that flush a pure buffer advance, leaving this
    /// class to decide when bytes actually go to the socket — once per
    /// <see cref="FlushThresholdBytes"/>, after which the buffer is reused rather than regrown.
    /// </summary>
    private sealed class ExportWriter : IAsyncDisposable
    {
        private readonly ArrayBufferWriter<byte> buffer = new(FlushThresholdBytes);
        private readonly Stream output;

        public ExportWriter(Stream output)
        {
            this.output = output;
            Json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });
        }

        public Utf8JsonWriter Json { get; }

        /// <summary>Bytes handed to the output stream so far — the payload size for the log.</summary>
        public long ByteCount { get; private set; }

        public async ValueTask DrainAsync(CancellationToken cancellationToken, bool force = false)
        {
            Json.Flush();
            if (buffer.WrittenCount == 0 || (!force && buffer.WrittenCount < FlushThresholdBytes))
            {
                return;
            }

            await output.WriteAsync(buffer.WrittenMemory, cancellationToken);
            ByteCount += buffer.WrittenCount;
            buffer.ResetWrittenCount();
        }

        public ValueTask DisposeAsync() => Json.DisposeAsync();
    }

    private IQueryable<AccountExport> AccountsQuery() =>
        context.Accounts.AsNoTracking()
            .OrderBy(account => account.AccountId)
            .Select(account => new AccountExport
            {
                AccountId = account.AccountId,
                Name = account.Name,
                Description = account.Description,
                Opened = account.Opened,
                AccountNumber = account.AccountNumber,
                AccountType = (FinanceDtos.AccountType)account.AccountType,
                Closed = account.Closed,
                Archived = account.Archived,
                CurrencyCode = account.CurrencyCode,
            });

    private IQueryable<AccountTermExport> AccountTermsQuery() =>
        context.AccountTerms.AsNoTracking()
            .OrderBy(term => term.AccountTermId)
            .Select(term => new AccountTermExport
            {
                AccountTermId = term.AccountTermId,
                AccountId = term.AccountId,
                TermKind = (FinanceDtos.TermKind)term.TermKind,
                Label = term.Label,
                LabelKey = term.LabelKey,
                ValueUnit = (FinanceDtos.TermValueUnit)term.ValueUnit,
                Value = term.Value,
                CurrencyCode = term.CurrencyCode,
                BillingPeriod = (FinanceDtos.BillingPeriod?)term.BillingPeriod,
                EffectiveFrom = term.EffectiveFrom,
                Note = term.Note,
                CreatedAtUtc = term.CreatedAtUtc,
            });

    private IQueryable<BudgetExport> BudgetsQuery() =>
        context.Budgets.AsNoTracking()
            .OrderBy(budget => budget.BudgetId)
            .Select(budget => new BudgetExport
            {
                BudgetId = budget.BudgetId,
                Name = budget.Name,
                Description = budget.Description,
                StartDate = budget.StartDate,
                EndDate = budget.EndDate,
                Archived = budget.Archived,
                BaseCurrencyCode = budget.BaseCurrencyCode,
            });

    private IQueryable<BudgetItemExport> BudgetItemsQuery() =>
        context.BudgetItems.AsNoTracking()
            .OrderBy(budgetItem => budgetItem.BudgetItemId)
            .Select(budgetItem => new BudgetItemExport
            {
                BudgetItemId = budgetItem.BudgetItemId,
                BudgetId = budgetItem.BudgetId,
                Name = budgetItem.Name,
                Description = budgetItem.Description,
                CategoryType = (FinanceDtos.BudgetCategoryType)budgetItem.CategoryType,
                PlannedAmount = budgetItem.PlannedAmount,
                TransactionTagId = budgetItem.TransactionTagId,
            });

    private IQueryable<ContactExport> ContactsQuery() =>
        context.Contacts.AsNoTracking()
            .OrderBy(contact => contact.ContactId)
            .Select(contact => new ContactExport
            {
                ContactId = contact.ContactId,
                // Resolved display name (issue #325): DisplayName override, else the type fallback.
                Name = contact.DisplayName != null && contact.DisplayName != ""
                    ? contact.DisplayName
                    : contact.Type == Odyssey.Dtos.ContactType.Person
                        ? (contact.PersonDetails!.FirstName + " " + contact.PersonDetails.LastName)
                        : contact.OrganizationDetails!.LegalName,
                NormalizedName = contact.NormalizedName,
                Type = contact.Type,
                Description = contact.Notes,
                Archived = contact.Archived,
            });

    private IQueryable<CurrencyExport> CurrenciesQuery() =>
        context.Currencies.AsNoTracking()
            .OrderBy(currency => currency.CurrencyCode)
            .Select(currency => new CurrencyExport
            {
                CurrencyCode = currency.CurrencyCode,
                Name = currency.Name,
                MinorUnits = currency.MinorUnits,
                Symbol = currency.Symbol,
                Archived = currency.Archived,
            });

    private IQueryable<ExchangeRateExport> ExchangeRatesQuery() =>
        context.ExchangeRates.AsNoTracking()
            .OrderBy(rate => rate.ExchangeRateId)
            .Select(rate => new ExchangeRateExport
            {
                ExchangeRateId = rate.ExchangeRateId,
                FromCurrencyCode = rate.FromCurrencyCode,
                ToCurrencyCode = rate.ToCurrencyCode,
                Rate = rate.Rate,
                AsOf = rate.AsOf,
                CreatedAt = rate.CreatedAt,
            });

    private IQueryable<TransactionExport> TransactionsQuery() =>
        context.Transactions.AsNoTracking()
            .OrderBy(transaction => transaction.TransactionId)
            .Select(transaction => new TransactionExport
            {
                TransactionId = transaction.TransactionId,
                Description = transaction.Description,
                Amount = transaction.Amount,
                TimeStamp = transaction.TimeStamp,
                AccountId = transaction.AccountId,
                TransactionTagIds = transaction.TransactionTagLinks
                    .Select(link => link.TransactionTagId)
                    .OrderBy(tagId => tagId)
                    .ToList(),
                ContactId = transaction.ContactId,
                ExternalId = transaction.ExternalId,
                InternalId = transaction.InternalId,
                ExtraData = transaction.ExtraData,
                Status = transaction.Status,
                StatusComment = transaction.StatusComment,
                StatusChangedAt = transaction.StatusChangedAt,
                CurrencyCode = transaction.CurrencyCode,
            });

    private IQueryable<TransactionTagExport> TransactionTagsQuery() =>
        context.TransactionTags.AsNoTracking()
            .OrderBy(tag => tag.TransactionTagId)
            .Select(tag => new TransactionTagExport
            {
                TransactionTagId = tag.TransactionTagId,
                Name = tag.Name,
                Description = tag.Description,
                Archived = tag.Archived,
            });

    // File metadata only — the FileBlob.Content payload is never projected, so no blob bytes leave the
    // database. FileBlobId is the relationship column, not the content.
    private IQueryable<FileMetadataExport> FileMetadataQuery() =>
        context.FileMetadata.AsNoTracking()
            .OrderBy(file => file.Id)
            .Select(file => new FileMetadataExport
            {
                Id = file.Id,
                UploadedByUserId = file.UploadedByUserId,
                FileName = file.FileName,
                ContentType = file.ContentType,
                SizeBytes = file.SizeBytes,
                Sha256Hash = file.Sha256Hash,
                FileBlobId = file.FileBlobId,
                Description = file.Description,
                UploadedAtUtc = file.UploadedAtUtc,
            });

    private IQueryable<AccountFileExport> AccountFilesQuery() =>
        context.AccountFiles.AsNoTracking()
            .OrderBy(accountFile => accountFile.Id)
            .Select(accountFile => new AccountFileExport
            {
                Id = accountFile.Id,
                AccountId = accountFile.AccountId,
                FileMetadataId = accountFile.FileMetadataId,
                AttachedByUserId = accountFile.AttachedByUserId,
                AttachedAtUtc = accountFile.AttachedAtUtc,
                FileType = (FinanceDtos.AccountFileType)accountFile.FileType,
            });

    private IQueryable<TransactionFileExport> TransactionFilesQuery() =>
        context.TransactionFiles.AsNoTracking()
            .OrderBy(transactionFile => transactionFile.Id)
            .Select(transactionFile => new TransactionFileExport
            {
                Id = transactionFile.Id,
                TransactionId = transactionFile.TransactionId,
                FileMetadataId = transactionFile.FileMetadataId,
                AttachedByUserId = transactionFile.AttachedByUserId,
                AttachedAtUtc = transactionFile.AttachedAtUtc,
                Type = (FinanceDtos.TransactionFileType)transactionFile.Type,
            });

    // ── Issue #33 ─────────────────────────────────────────────────────────────

    private IQueryable<AccountEstimateExport> AccountEstimatesQuery() =>
        context.AccountEstimates.AsNoTracking()
            .OrderBy(estimate => estimate.AccountEstimateId)
            .Select(estimate => new AccountEstimateExport
            {
                AccountEstimateId = estimate.AccountEstimateId,
                AccountId = estimate.AccountId,
                Value = estimate.Value,
                CurrencyCode = estimate.CurrencyCode,
                EffectiveFrom = estimate.EffectiveFrom,
                Note = estimate.Note,
                CreatedAtUtc = estimate.CreatedAtUtc,
            });

    // Composite-keyed, so both key columns order it — there is no single id to sort on.
    private IQueryable<AccountSmartTagExport> AccountSmartTagsQuery() =>
        context.AccountSmartTags.AsNoTracking()
            .OrderBy(smartTag => smartTag.AccountId)
            .ThenBy(smartTag => smartTag.TransactionTagId)
            .Select(smartTag => new AccountSmartTagExport
            {
                AccountId = smartTag.AccountId,
                TransactionTagId = smartTag.TransactionTagId,
                AddedAt = smartTag.AddedAt,
            });

    private IQueryable<TaxStatementExport> TaxStatementsQuery() =>
        context.TaxStatements.AsNoTracking()
            .OrderBy(statement => statement.TaxStatementId)
            .Select(statement => new TaxStatementExport
            {
                TaxStatementId = statement.TaxStatementId,
                Name = statement.Name,
                FiscalYear = statement.FiscalYear,
                StartDate = statement.StartDate,
                EndDate = statement.EndDate,
                BaseCurrencyCode = statement.BaseCurrencyCode,
                DeclaredTotalAssets = statement.DeclaredTotalAssets,
                DeclaredTotalLiabilities = statement.DeclaredTotalLiabilities,
                DeclaredNetWorth = statement.DeclaredNetWorth,
                DeclaredTotalIncome = statement.DeclaredTotalIncome,
                AssessedTax = statement.AssessedTax,
                SettlementAmount = statement.SettlementAmount,
                SettledAtUtc = statement.SettledAtUtc,
                FiledAtUtc = statement.FiledAtUtc,
                TaxOfficeApprovedAtUtc = statement.TaxOfficeApprovedAtUtc,
                // No Odyssey.Context copy of this enum exists, so there is nothing to cast across.
                Status = statement.Status,
                StatusComment = statement.StatusComment,
                StatusChangedAt = statement.StatusChangedAt,
                Notes = statement.Notes,
                Archived = statement.Archived,
                CreatedAtUtc = statement.CreatedAtUtc,
            });

    private IQueryable<TaxStatementTagExport> TaxStatementTagsQuery() =>
        context.TaxStatementTags.AsNoTracking()
            .OrderBy(tag => tag.Id)
            .Select(tag => new TaxStatementTagExport
            {
                Id = tag.Id,
                TaxStatementId = tag.TaxStatementId,
                TransactionTagId = tag.TransactionTagId,
                Role = tag.Role,
            });

    private IQueryable<TaxStatementFileExport> TaxStatementFilesQuery() =>
        context.TaxStatementFiles.AsNoTracking()
            .OrderBy(statementFile => statementFile.Id)
            .Select(statementFile => new TaxStatementFileExport
            {
                Id = statementFile.Id,
                TaxStatementId = statementFile.TaxStatementId,
                FileMetadataId = statementFile.FileMetadataId,
                AttachedByUserId = statementFile.AttachedByUserId,
                AttachedAtUtc = statementFile.AttachedAtUtc,
                FileType = (FinanceDtos.TaxStatementFileType)statementFile.FileType,
            });

    private IQueryable<InsurancePolicyExport> InsurancePoliciesQuery() =>
        context.InsurancePolicies.AsNoTracking()
            .OrderBy(policy => policy.InsurancePolicyId)
            .Select(policy => new InsurancePolicyExport
            {
                InsurancePolicyId = policy.InsurancePolicyId,
                Name = policy.Name,
                PolicyNumber = policy.PolicyNumber,
                Type = (FinanceDtos.InsurancePolicyType)policy.Type,
                Notes = policy.Notes,
                Archived = policy.Archived,
                CreatedAtUtc = policy.CreatedAtUtc,
            });

    // The four party link tables: relationship columns and the optional term, never a resolved
    // name. See InsurancePolicyInsurerExport for why the id alone is the right projection.
    private IQueryable<InsurancePolicyInsurerExport> InsurancePolicyInsurersQuery() =>
        context.InsurancePolicyInsurers.AsNoTracking()
            .OrderBy(insurer => insurer.Id)
            .Select(insurer => new InsurancePolicyInsurerExport
            {
                Id = insurer.Id,
                InsurancePolicyId = insurer.InsurancePolicyId,
                ContactId = insurer.ContactId,
                FromDate = insurer.FromDate,
                ToDate = insurer.ToDate,
            });

    private IQueryable<InsurancePolicyInsuredAccountExport> InsurancePolicyInsuredAccountsQuery() =>
        context.InsurancePolicyInsuredAccounts.AsNoTracking()
            .OrderBy(insured => insured.Id)
            .Select(insured => new InsurancePolicyInsuredAccountExport
            {
                Id = insured.Id,
                InsurancePolicyId = insured.InsurancePolicyId,
                AccountId = insured.AccountId,
                FromDate = insured.FromDate,
                ToDate = insured.ToDate,
            });

    private IQueryable<InsurancePolicyInsuredContactExport> InsurancePolicyInsuredContactsQuery() =>
        context.InsurancePolicyInsuredContacts.AsNoTracking()
            .OrderBy(insured => insured.Id)
            .Select(insured => new InsurancePolicyInsuredContactExport
            {
                Id = insured.Id,
                InsurancePolicyId = insured.InsurancePolicyId,
                ContactId = insured.ContactId,
                FromDate = insured.FromDate,
                ToDate = insured.ToDate,
            });

    private IQueryable<InsurancePolicyBeneficiaryExport> InsurancePolicyBeneficiariesQuery() =>
        context.InsurancePolicyBeneficiaries.AsNoTracking()
            .OrderBy(beneficiary => beneficiary.Id)
            .Select(beneficiary => new InsurancePolicyBeneficiaryExport
            {
                Id = beneficiary.Id,
                InsurancePolicyId = beneficiary.InsurancePolicyId,
                ContactId = beneficiary.ContactId,
                FromDate = beneficiary.FromDate,
                ToDate = beneficiary.ToDate,
                CreatedByUserId = beneficiary.CreatedByUserId,
                CreatedAtUtc = beneficiary.CreatedAtUtc,
            });

    private IQueryable<PolicyRenewalExport> PolicyRenewalsQuery() =>
        context.PolicyRenewals.AsNoTracking()
            .OrderBy(renewal => renewal.PolicyRenewalId)
            .Select(renewal => new PolicyRenewalExport
            {
                PolicyRenewalId = renewal.PolicyRenewalId,
                InsurancePolicyId = renewal.InsurancePolicyId,
                FromDate = renewal.FromDate,
                ToDate = renewal.ToDate,
                Premium = renewal.Premium,
                PremiumCurrencyCode = renewal.PremiumCurrencyCode,
                CoverageAmount = renewal.CoverageAmount,
                CoverageCurrencyCode = renewal.CoverageCurrencyCode,
                Notes = renewal.Notes,
                CreatedAtUtc = renewal.CreatedAtUtc,
            });

    private IQueryable<PolicyRenewalFileExport> PolicyRenewalFilesQuery() =>
        context.PolicyRenewalFiles.AsNoTracking()
            .OrderBy(renewalFile => renewalFile.Id)
            .Select(renewalFile => new PolicyRenewalFileExport
            {
                Id = renewalFile.Id,
                PolicyRenewalId = renewalFile.PolicyRenewalId,
                FileMetadataId = renewalFile.FileMetadataId,
                FileType = (FinanceDtos.PolicyFileType)renewalFile.FileType,
                EffectiveDate = renewalFile.EffectiveDate,
                AttachedByUserId = renewalFile.AttachedByUserId,
                AttachedAtUtc = renewalFile.AttachedAtUtc,
            });

    private IQueryable<ContractExport> ContractsQuery() =>
        context.Contracts.AsNoTracking()
            .OrderBy(contract => contract.ContractId)
            .Select(contract => new ContractExport
            {
                ContractId = contract.ContractId,
                Name = contract.Name,
                Type = (FinanceDtos.ContractType)contract.Type,
                Description = contract.Description,
                StartDate = contract.StartDate,
                EndDate = contract.EndDate,
                CompletionDate = contract.CompletionDate,
                Archived = contract.Archived,
                CreatedAtUtc = contract.CreatedAtUtc,
            });

    private IQueryable<ContractPartyExport> ContractPartiesQuery() =>
        context.ContractParties.AsNoTracking()
            .OrderBy(party => party.ContractPartyId)
            .Select(party => new ContractPartyExport
            {
                ContractPartyId = party.ContractPartyId,
                ContractId = party.ContractId,
                AccountId = party.AccountId,
                ContactId = party.ContactId,
            });

    private IQueryable<ContractFileExport> ContractFilesQuery() =>
        context.ContractFiles.AsNoTracking()
            .OrderBy(contractFile => contractFile.ContractFileId)
            .Select(contractFile => new ContractFileExport
            {
                ContractFileId = contractFile.ContractFileId,
                ContractId = contractFile.ContractId,
                FileMetadataId = contractFile.FileMetadataId,
                FileType = (FinanceDtos.ContractFileType)contractFile.FileType,
                AttachedByUserId = contractFile.AttachedByUserId,
                AttachedAtUtc = contractFile.AttachedAtUtc,
            });

    private IQueryable<SubscriptionExport> SubscriptionsQuery() =>
        context.Subscriptions.AsNoTracking()
            .OrderBy(subscription => subscription.SubscriptionId)
            .Select(subscription => new SubscriptionExport
            {
                SubscriptionId = subscription.SubscriptionId,
                Name = subscription.Name,
                ExternalId = subscription.ExternalId,
                ContactId = subscription.ContactId,
                StartDate = subscription.StartDate,
                EndDate = subscription.EndDate,
                Amount = subscription.Amount,
                CurrencyCode = subscription.CurrencyCode,
                Interval = (FinanceDtos.BillingInterval)subscription.Interval,
                IntervalCount = subscription.IntervalCount,
                FirstBillingDate = subscription.FirstBillingDate,
                Notes = subscription.Notes,
                Paused = subscription.Paused,
                Archived = subscription.Archived,
                CreatedAtUtc = subscription.CreatedAtUtc,
            });
}

/// <summary>
/// The constant parts of the envelope, taken from <see cref="DataExportDocument"/>'s own defaults so
/// the streamed document and the published contract cannot drift apart.
/// </summary>
internal static class DataExportDefaults
{
    private static readonly DataExportDocument Template = new();

    public static int SchemaVersion => Template.SchemaVersion;

    public static string Format => Template.Format;

    public static DataExportExclusions Exclusions => Template.Exclusions;
}
