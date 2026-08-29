using Microsoft.Extensions.DependencyInjection;
using Odyssey.ApiClient.Auth;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.ApiClient;

/// <summary>Registers the Odyssey API client and its typed resource clients.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IOdysseyApi"/>, the antiforgery pipeline and every typed resource client
    /// as scoped services. The caller owns the <see cref="HttpClient"/> registration — its base
    /// address, and how it obtains the auth cookie, differ per host (the Blazor client adds a
    /// browser-credentials handler; a console or test consumer supplies a
    /// <see cref="HttpClientHandler"/> with a cookie container).
    /// </summary>
    public static IServiceCollection AddOdysseyApiClient(this IServiceCollection services)
    {
        services.AddScoped<AntiforgeryTokenStore>();
        services.AddTransient<AntiforgeryHandler>();
        services.AddScoped<AuthApiClient>();
        services.AddScoped<IOdysseyApi, OdysseyApi>();

        services.AddScoped<IAccountsApiClient, AccountsApiClient>();
        services.AddScoped<ITaxStatementsApiClient, TaxStatementsApiClient>();
        services.AddScoped<IBudgetsApiClient, BudgetsApiClient>();
        services.AddScoped<IBudgetItemsApiClient, BudgetItemsApiClient>();
        services.AddScoped<IUsersApiClient, UsersApiClient>();
        services.AddScoped<IUserPreferencesApiClient, UserPreferencesApiClient>();

        // The four tag resources share one contract; each closed client is bound to its own route.
        services.AddScoped<ITagsApiClient<ExistingTransactionTag>>(sp =>
            new TagsApiClient<ExistingTransactionTag>(sp.GetRequiredService<IOdysseyApi>(), "api/transaction-tags"));
        services.AddScoped<ITagsApiClient<ExistingJournalTag>>(sp =>
            new TagsApiClient<ExistingJournalTag>(sp.GetRequiredService<IOdysseyApi>(), "api/journal-tags"));
        services.AddScoped<ITagsApiClient<ExistingJournalTaskTag>>(sp =>
            new TagsApiClient<ExistingJournalTaskTag>(sp.GetRequiredService<IOdysseyApi>(), "api/task-tags"));
        services.AddScoped<ITagsApiClient<ExistingPhotoTag>>(sp =>
            new TagsApiClient<ExistingPhotoTag>(sp.GetRequiredService<IOdysseyApi>(), "api/photo-tags"));
        services.AddScoped<IFileAnalysisApiClient, FileAnalysisApiClient>();
        services.AddScoped<ICurrenciesApiClient, CurrenciesApiClient>();
        services.AddScoped<IExchangeRatesApiClient, ExchangeRatesApiClient>();
        services.AddScoped<ITransactionsApiClient, TransactionsApiClient>();
        services.AddScoped<ITransactionTagsApiClient, TransactionTagsApiClient>();
        services.AddScoped<IContactsApiClient, ContactsApiClient>();
        services.AddScoped<IFilesApiClient, FilesApiClient>();
        services.AddScoped<IInsuranceApiClient, InsuranceApiClient>();
        services.AddScoped<IContractsApiClient, ContractsApiClient>();
        services.AddScoped<ISubscriptionApiClient, SubscriptionApiClient>();
        services.AddScoped<IJournalApiClient, JournalApiClient>();
        services.AddScoped<IJournalIcsApiClient, JournalIcsApiClient>();
        services.AddScoped<ITaskApiClient, TaskApiClient>();
        services.AddScoped<ICalendarApiClient, CalendarApiClient>();
        services.AddScoped<IContactVCardApiClient, ContactVCardApiClient>();
        services.AddScoped<IPhotosApiClient, PhotosApiClient>();
        services.AddScoped<IPhotoTagsApiClient, PhotoTagsApiClient>();
        services.AddScoped<IAlbumsApiClient, AlbumsApiClient>();
        services.AddScoped<IDataExportApiClient, DataExportApiClient>();
        services.AddScoped<IFileExportApiClient, FileExportApiClient>();
        services.AddScoped<IProfileApiClient, ProfileApiClient>();
        services.AddScoped<ISystemSettingsApiClient, SystemSettingsApiClient>();
        services.AddScoped<ISecretSettingsApiClient, SecretSettingsApiClient>();
        services.AddScoped<ILegalApiClient, LegalApiClient>();
        services.AddScoped<IImportLimitsApiClient, ImportLimitsApiClient>();
        services.AddScoped<IUploadLimitsApiClient, UploadLimitsApiClient>();
        services.AddScoped<IAccountLimitsApiClient, AccountLimitsApiClient>();
        services.AddScoped<IFileAnalysisDisclosureApiClient, FileAnalysisDisclosureApiClient>();

        return services;
    }
}
