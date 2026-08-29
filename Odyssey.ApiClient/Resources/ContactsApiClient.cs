using Odyssey.Dtos.Journal;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the contacts endpoints (people and organisations), including the address / email /
/// phone sub-resources. vCard import/export lives separately in
/// <see cref="IContactVCardApiClient"/>, since those stream non-JSON bodies.
/// </summary>
public interface IContactsApiClient
{
    /// <summary>One page of contacts with server-side search, type and archival filters (issue #277).</summary>
    Task<ApiResult<PagedResult<ExistingContact>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? status = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>
    /// Every matching contact in one window — the picker/dropdown load. <paramref name="types"/> takes
    /// the <c>ContactType</c> names (the photos surfaces pass <c>Person</c> for their people options).
    /// </summary>
    Task<ApiResult<List<ExistingContact>>> ListAllAsync(
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? status = null,
        CancellationToken ct = default);

    /// <summary>Loads one contact with its details and contact methods.</summary>
    Task<ApiResult<ExistingContact>> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Creates a contact. Returns <c>201</c> with an empty body, so the new id is on
    /// <see cref="ApiResult.CreatedId"/>; a duplicate name comes back as <c>409 Conflict</c>, which the
    /// quick-create flow reconciles against the existing record rather than treating as fatal.
    /// </summary>
    Task<ApiResult> CreateAsync(NewContact contact, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, NewContact contact, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);

    // ── Contact methods ──────────────────────────────────────────────────────────
    // The server owns primary arbitration (setting one primary clears the others), so callers
    // re-fetch the collection after any mutation rather than patching locally.

    Task<ApiResult<List<ExistingAddress>>> ListAddressesAsync(Guid contactId, CancellationToken ct = default);
    Task<ApiResult> AddAddressAsync(Guid contactId, NewAddress address, CancellationToken ct = default);
    Task<ApiResult> UpdateAddressAsync(Guid contactId, Guid addressId, NewAddress address, CancellationToken ct = default);
    Task<ApiResult> DeleteAddressAsync(Guid contactId, Guid addressId, CancellationToken ct = default);

    Task<ApiResult<List<ExistingEmailAddress>>> ListEmailsAsync(Guid contactId, CancellationToken ct = default);
    Task<ApiResult> AddEmailAsync(Guid contactId, NewEmailAddress email, CancellationToken ct = default);
    Task<ApiResult> UpdateEmailAsync(Guid contactId, Guid emailId, NewEmailAddress email, CancellationToken ct = default);
    Task<ApiResult> DeleteEmailAsync(Guid contactId, Guid emailId, CancellationToken ct = default);

    Task<ApiResult<List<ExistingPhoneNumber>>> ListPhonesAsync(Guid contactId, CancellationToken ct = default);
    Task<ApiResult> AddPhoneAsync(Guid contactId, NewPhoneNumber phone, CancellationToken ct = default);
    Task<ApiResult> UpdatePhoneAsync(Guid contactId, Guid phoneId, NewPhoneNumber phone, CancellationToken ct = default);
    Task<ApiResult> DeletePhoneAsync(Guid contactId, Guid phoneId, CancellationToken ct = default);
}

/// <inheritdoc cref="IContactsApiClient" />
public sealed class ContactsApiClient(IOdysseyApi api) : IContactsApiClient
{
    private const string Base = "api/contacts";

    public Task<ApiResult<PagedResult<ExistingContact>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? status = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingContact>(
            PagedQuery.For(Base)
                .Window(page, pageSize)
                .Add("search", search)
                .AddMany("types", types)
                .AddSingle("status", status)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public Task<ApiResult<List<ExistingContact>>> ListAllAsync(
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? status = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<ExistingContact>(
            PagedQuery.For(Base).AddMany("types", types).AddSingle("status", status).Build(), ct);

    public Task<ApiResult<ExistingContact>> GetAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<ExistingContact>($"{Base}/{id}", ct);

    public Task<ApiResult> CreateAsync(NewContact contact, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, contact, ct);

    public Task<ApiResult> UpdateAsync(Guid id, NewContact contact, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", contact, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);

    // ── Contact methods ──────────────────────────────────────────────────────────

    public Task<ApiResult<List<ExistingAddress>>> ListAddressesAsync(Guid contactId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingAddress>>($"{Base}/{contactId}/addresses", ct);

    public Task<ApiResult> AddAddressAsync(Guid contactId, NewAddress address, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Base}/{contactId}/addresses", address, ct);

    public Task<ApiResult> UpdateAddressAsync(Guid contactId, Guid addressId, NewAddress address, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{contactId}/addresses/{addressId}", address, ct);

    public Task<ApiResult> DeleteAddressAsync(Guid contactId, Guid addressId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{contactId}/addresses/{addressId}", null, ct);

    public Task<ApiResult<List<ExistingEmailAddress>>> ListEmailsAsync(Guid contactId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingEmailAddress>>($"{Base}/{contactId}/emails", ct);

    public Task<ApiResult> AddEmailAsync(Guid contactId, NewEmailAddress email, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Base}/{contactId}/emails", email, ct);

    public Task<ApiResult> UpdateEmailAsync(Guid contactId, Guid emailId, NewEmailAddress email, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{contactId}/emails/{emailId}", email, ct);

    public Task<ApiResult> DeleteEmailAsync(Guid contactId, Guid emailId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{contactId}/emails/{emailId}", null, ct);

    public Task<ApiResult<List<ExistingPhoneNumber>>> ListPhonesAsync(Guid contactId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingPhoneNumber>>($"{Base}/{contactId}/phones", ct);

    public Task<ApiResult> AddPhoneAsync(Guid contactId, NewPhoneNumber phone, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Base}/{contactId}/phones", phone, ct);

    public Task<ApiResult> UpdatePhoneAsync(Guid contactId, Guid phoneId, NewPhoneNumber phone, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{contactId}/phones/{phoneId}", phone, ct);

    public Task<ApiResult> DeletePhoneAsync(Guid contactId, Guid phoneId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{contactId}/phones/{phoneId}", null, ct);
}
