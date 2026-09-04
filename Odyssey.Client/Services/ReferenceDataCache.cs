using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Services;

/// <summary>
/// A per-session cache for the reference data every amount-entry dialog needs: currencies,
/// transaction tags and contacts (issue #372).
/// </summary>
/// <remarks>
/// <para>
/// These three lists changed almost never yet were re-fetched on <em>every</em> dialog open — twelve
/// components each carried a byte-identical "load all currencies, drop the archived ones, order by
/// code" body, and six of them carried an identical <c>SearchCurrencyCodes</c> alongside it. Both live
/// here now, once.
/// </para>
/// <para>
/// Registered scoped, which in Blazor WebAssembly means one instance for the life of the app, so a
/// list is fetched once and every later reader is served from memory. Concurrent readers share the
/// single in-flight request rather than racing two of them.
/// </para>
/// <para>
/// <b>Failures are never cached.</b> A failed load toasts (the app-wide "Unable to load …" wording),
/// returns an empty list, and leaves the slot empty so the next reader retries — a transient 500
/// while a dialog opens must not poison the picker for the rest of the session.
/// </para>
/// <para>
/// <b>Mutations must invalidate.</b> The admin surfaces that write these resources call the matching
/// <c>Invalidate*</c> so a picker opened afterwards sees the change; without that the cache would
/// serve a stale list until reload.
/// </para>
/// </remarks>
public interface IReferenceDataCache
{
    /// <summary>Every currency, archived included, as the API returned it. For callers that resolve a
    /// symbol for a code that may since have been archived.</summary>
    Task<IReadOnlyList<ExistingCurrency>> CurrenciesAsync(CancellationToken ct = default);

    /// <summary>The live currencies ordered by ISO code — what every currency picker offers.</summary>
    Task<IReadOnlyList<ExistingCurrency>> ActiveCurrenciesAsync(CancellationToken ct = default);

    /// <summary>
    /// The live currencies as picker options for <c>OdsCurrencySelect</c> / <c>OdsMoneyField</c>: the
    /// ISO code as the value, the currency NAME alone as the label. Both controls render the code
    /// themselves, in mono and in its own gutter, so a "USD · US Dollar" label would print it twice.
    /// </summary>
    Task<IReadOnlyList<OdsOption>> CurrencyOptionsAsync(CancellationToken ct = default);

    /// <summary>Every transaction tag, archived included — call sites filter to taste.</summary>
    Task<IReadOnlyList<ExistingTransactionTag>> TransactionTagsAsync(CancellationToken ct = default);

    /// <summary>Every contact, archived included — call sites filter to taste.</summary>
    Task<IReadOnlyList<ExistingContact>> ContactsAsync(CancellationToken ct = default);

    /// <summary>Drops the cached currencies; the next reader re-fetches.</summary>
    void InvalidateCurrencies();

    /// <summary>Drops the cached transaction tags; the next reader re-fetches.</summary>
    void InvalidateTransactionTags();

    /// <summary>Drops the cached contacts; the next reader re-fetches.</summary>
    void InvalidateContacts();
}

/// <inheritdoc cref="IReferenceDataCache" />
public sealed class ReferenceDataCache(
    ICurrenciesApiClient currencies,
    ITransactionTagsApiClient transactionTags,
    IContactsApiClient contacts,
    ISnackbar snackbar) : IReferenceDataCache
{
    private readonly Slot<ExistingCurrency> currencySlot = new();
    private readonly Slot<ExistingTransactionTag> tagSlot = new();
    private readonly Slot<ExistingContact> contactSlot = new();

    public Task<IReadOnlyList<ExistingCurrency>> CurrenciesAsync(CancellationToken ct = default) =>
        Load(currencySlot, async () =>
        {
            var result = await currencies.ListAllAsync(ct: ct);
            return Unwrap(result.IsSuccess, result.ValueOr([]), result.Error, "currencies");
        });

    public async Task<IReadOnlyList<ExistingCurrency>> ActiveCurrenciesAsync(CancellationToken ct = default) =>
        [.. (await CurrenciesAsync(ct))
            .Where(currency => currency.Archived is null)
            .OrderBy(currency => currency.CurrencyCode, StringComparer.OrdinalIgnoreCase)];

    public async Task<IReadOnlyList<OdsOption>> CurrencyOptionsAsync(CancellationToken ct = default) =>
        [.. (await ActiveCurrenciesAsync(ct)).Select(currency => new OdsOption(currency.CurrencyCode, currency.Name))];

    public Task<IReadOnlyList<ExistingTransactionTag>> TransactionTagsAsync(CancellationToken ct = default) =>
        Load(tagSlot, async () =>
        {
            var result = await transactionTags.ListAllAsync(ct: ct);
            return Unwrap(result.IsSuccess, result.ValueOr([]), result.Error, "transaction tags");
        });

    public Task<IReadOnlyList<ExistingContact>> ContactsAsync(CancellationToken ct = default) =>
        Load(contactSlot, async () =>
        {
            var result = await contacts.ListAllAsync(ct: ct);
            return Unwrap(result.IsSuccess, result.ValueOr([]), result.Error, "contacts");
        });

    public void InvalidateCurrencies() => currencySlot.Pending = null;

    public void InvalidateTransactionTags() => tagSlot.Pending = null;

    public void InvalidateContacts() => contactSlot.Pending = null;

    // A null result means "the load failed" — toast once, then leave the slot empty so the next
    // reader retries instead of being served a cached failure.
    private IReadOnlyList<T>? Unwrap<T>(bool isSuccess, List<T> items, string? error, string what)
    {
        if (isSuccess)
            return items;

        snackbar.Add($"Unable to load {what}: {error}", Severity.Error);
        return null;
    }

    private static async Task<IReadOnlyList<T>> Load<T>(Slot<T> slot, Func<Task<IReadOnlyList<T>?>> load)
    {
        // Readers that arrive while a fetch is in flight await that same task rather than issuing
        // their own — two dialogs opening back to back cost one request, not two.
        var pending = slot.Pending ??= load();
        var items = await pending;

        if (items is not null)
            return items;

        // Only clear the slot if it still holds the failed task: an Invalidate (or a retry that
        // already succeeded) may have replaced it while this one was in flight.
        if (ReferenceEquals(slot.Pending, pending))
            slot.Pending = null;

        return [];
    }

    private sealed class Slot<T>
    {
        public Task<IReadOnlyList<T>?>? Pending { get; set; }
    }
}
