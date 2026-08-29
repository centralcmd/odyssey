using Odyssey.Dtos.Journal;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the contact vCard import/export endpoints (issue #338). Export streams a
/// <c>text/vcard</c> body and import posts multipart, so both go through the transport core's file
/// helpers rather than its JSON ones — mirroring <see cref="CalendarApiClient"/>'s ICS pattern
/// (issue #330).
/// </summary>
public interface IContactVCardApiClient
{
    /// <summary>Exports a single contact as a single-entry <c>.vcf</c>.</summary>
    Task<ApiResult<ApiFile>> ExportOneAsync(Guid contactId, CancellationToken ct = default);

    /// <summary>
    /// Exports every contact matching the given filters, or all of them when none are supplied. Takes
    /// the same filter surface as <c>IContactsApiClient.ListAsync</c> minus paging — the export is
    /// unpaginated server-side — so the caller never has to know the route.
    /// </summary>
    Task<ApiResult<ApiFile>> ExportManyAsync(
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? status = null,
        CancellationToken ct = default);

    /// <summary>Imports a <c>.vcf</c> file (multipart).</summary>
    Task<ApiResult<VCardImportResult>> ImportAsync(ApiUpload file, CancellationToken ct = default);
}

/// <inheritdoc cref="IContactVCardApiClient" />
public sealed class ContactVCardApiClient(IOdysseyApi api) : IContactVCardApiClient
{
    /// <summary>
    /// The migration-seeded default for <c>ContactVCardMaxImportMegabytes</c> — the real, effective
    /// cap is dynamic (System Settings, issue #343) and read via <c>IImportLimitsApiClient</c>; this
    /// constant exists solely as <c>ImportLimitsCache.Fallback</c>'s input for when that read fails.
    /// </summary>
    public const long MaxImportBytes = 64L * 1024 * 1024;

    private const string Base = "api/contacts";

    public Task<ApiResult<ApiFile>> ExportOneAsync(Guid contactId, CancellationToken ct = default) =>
        api.GetFileAsync($"{Base}/{contactId}/vcard", "contact.vcf", ct: ct);

    public Task<ApiResult<ApiFile>> ExportManyAsync(
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? status = null,
        CancellationToken ct = default) =>
        api.GetFileAsync(
            PagedQuery.For($"{Base}/vcard")
                .Add("search", search)
                .AddMany("types", types)
                .AddSingle("status", status)
                .Build(),
            "contacts.vcf",
            completenessMarker: "BEGIN:VCARD",
            ct: ct);

    public Task<ApiResult<VCardImportResult>> ImportAsync(ApiUpload file, CancellationToken ct = default) =>
        api.UploadAsync<VCardImportResult>(
            $"{Base}/vcard",
            string.IsNullOrWhiteSpace(file.FileName) ? file with { FileName = "contacts.vcf" } : file,
            contentTypeOverride: "text/vcard",
            ct: ct);
}
