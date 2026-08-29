using Odyssey.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

/// <summary>vCard import/export endpoints (issue #338): permission matrix and endpoint contracts.</summary>
public class ContactVCardApiTests
{
    private const string ActorUserId = "contact-vcard-actor-id";
    private const string BasePath = "/api/contacts";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate, PermissionClaims.ContactsUpdate,
    ];

    // ------------------------------------------------------------------ export (single)

    [Fact]
    public async Task ExportOne_ReturnsVCardWithNoSniff()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory, "Acme");
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{BasePath}/{id}/vcard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/vcard", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join("", v) : null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VCARD", body);
        Assert.Contains("ORG:Acme", body);
        Assert.Contains("END:VCARD", body);
        Assert.EndsWith(".vcf", response.Content.Headers.ContentDisposition?.FileNameStar
                              ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "");
    }

    [Fact]
    public async Task ExportOne_MissingContact_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{BasePath}/{Guid.NewGuid()}/vcard");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExportOne_WithoutReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{BasePath}/{Guid.NewGuid()}/vcard");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------ export (collection)

    [Fact]
    public async Task ExportAll_NoFilters_ReturnsEveryContact()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SeedContactAsync(factory, "Acme");
        await SeedContactAsync(factory, "Globex");
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{BasePath}/vcard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(2, CountOccurrences(body, "BEGIN:VCARD"));
    }

    [Fact]
    public async Task ExportFiltered_BySearch_ReturnsOnlyMatching()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SeedContactAsync(factory, "Acme");
        await SeedContactAsync(factory, "Globex");
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{BasePath}/vcard?search=acme");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(1, CountOccurrences(body, "BEGIN:VCARD"));
        Assert.Contains("ORG:Acme", body);
    }

    [Fact]
    public async Task ExportMany_WithoutReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{BasePath}/vcard");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_ValidVCard_CreatesContact()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var vcf = Vcard("UID:api-import-1", "FN:Ada Lovelace", "N:Lovelace;Ada;;;");
        var result = await ImportAsync(client, vcf);

        Assert.Equal(1, result!.CreatedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(result.Skipped);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal("api-import-1", ctx.Contacts.Single().ExternalUid);
    }

    [Fact]
    public async Task Import_ExportedFile_IsIdempotent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory, "Acme");
        using var client = factory.CreateClient();

        var exported = await (await client.GetAsync($"{BasePath}/{id}/vcard")).Content.ReadAsStringAsync();
        var result = await ImportAsync(client, exported);

        Assert.Equal(0, result!.CreatedCount);
        Assert.Equal(1, result.UpdatedCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Single(ctx.Contacts);
    }

    [Fact]
    public async Task Import_MissingContactType_IsSkippedWithReason()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var result = await ImportAsync(client, Vcard("UID:no-type", "FN:Nobody"));

        Assert.Equal(0, result!.CreatedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("Could not determine contact type", group.Reason);
    }

    [Fact]
    public async Task Import_MalformedFile_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await PostVCardAsync(client, "this is not a vcard", "notes.vcf");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_WrongContentType_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await PostVCardAsync(client, Vcard("UID:x1", "FN:X", "ORG:X"), "data.vcf", contentType: "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_NonVcfExtension_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await PostVCardAsync(client, Vcard("UID:x2", "FN:X", "ORG:X"), "data.txt");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_OctetStreamContentType_IsAccepted()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await PostVCardAsync(
            client, Vcard("UID:octet-1", "FN:Acme", "ORG:Acme"), "cal.vcf", contentType: "application/octet-stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Import_EmptyFile_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await PostVCardAsync(client, "", "empty.vcf");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------ configured caps (issue #343, AC 4/5)

    [Fact]
    public async Task Import_ConfiguredEntryCap_RejectsOverCap_AcceptsAtCap()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        using var client = factory.CreateClient();
        // The export cap must be lowered alongside import (or the round-trip rule — export ≤ import,
        // §9 — rejects this: export defaults to unlimited, which always exceeds a finite import).
        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            ContactVCardMaxExportRows = new CapacityLimit { Value = 3 },
            ContactVCardMaxImportEntries = new CapacityLimit { Value = 3 },
        })).EnsureSuccessStatusCode();

        var overCap = string.Concat(Enumerable.Range(0, 4).Select(i => Vcard($"UID:over-{i}", $"FN:P{i}", $"ORG:P{i}")));
        var overResponse = await PostVCardAsync(client, overCap);
        Assert.Equal(HttpStatusCode.BadRequest, overResponse.StatusCode);
        var overBody = await overResponse.Content.ReadAsStringAsync();
        Assert.Contains("3", overBody);

        var atCap = string.Concat(Enumerable.Range(0, 3).Select(i => Vcard($"UID:at-{i}", $"FN:P{i}", $"ORG:P{i}")));
        var atResult = await ImportAsync(client, atCap);
        Assert.Equal(3, atResult!.CreatedCount);
    }

    [Fact]
    public async Task Export_ConfiguredExportRowCap_RejectsOverCap_AcceptsAtCap()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        await SeedContactAsync(factory, "One");
        await SeedContactAsync(factory, "Two");
        await SeedContactAsync(factory, "Three");
        using var client = factory.CreateClient();
        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            ContactVCardMaxExportRows = new CapacityLimit { Value = 2 },
        })).EnsureSuccessStatusCode();

        var overCap = await client.GetAsync($"{BasePath}/vcard");
        Assert.Equal(HttpStatusCode.BadRequest, overCap.StatusCode);

        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            ContactVCardMaxExportRows = new CapacityLimit { Value = 3 },
        })).EnsureSuccessStatusCode();

        var atCap = await client.GetAsync($"{BasePath}/vcard");
        Assert.Equal(HttpStatusCode.OK, atCap.StatusCode);
        Assert.Equal(3, CountOccurrences(await atCap.Content.ReadAsStringAsync(), "BEGIN:VCARD"));
    }

    [Fact]
    public async Task Export_OverConfiguredByteCap_TruncatesBelowThePromisedRowCount()
    {
        // Follow-up to #343: ContactVCardMaxExportMegabytes can't be rejected up front like the
        // row-count cap above (total output size isn't knowable until it's generated) — once writing
        // the next chunk would cross it, the stream stops, leaving the body with fewer vCards than
        // X-Odyssey-Export-Rows already promised.
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate]);
        var legalName = "Bulky " + new string('x', 700);
        for (var i = 0; i < 2000; i++)
        {
            await SeedContactAsync(factory, $"{legalName} {i}");
        }

        using var client = factory.CreateClient();
        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            ContactVCardMaxExportMegabytes = 1,
        })).EnsureSuccessStatusCode();

        var response = await client.GetAsync($"{BasePath}/vcard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var promisedRows = int.Parse(response.Headers.GetValues("X-Odyssey-Export-Rows").Single());
        Assert.Equal(2000, promisedRows);

        var body = await response.Content.ReadAsStringAsync();
        var deliveredRows = CountOccurrences(body, "BEGIN:VCARD");
        Assert.True(deliveredRows < promisedRows,
            $"Expected the export to be truncated below {promisedRows} rows, but delivered {deliveredRows}.");
    }

    [Fact]
    public async Task Import_UnlimitedEntryCap_AcceptsBatchAboveAConfiguredFiniteCap()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        using var client = factory.CreateClient();
        // First tighten to a finite cap (export set alongside, or the round-trip rule rejects it — see
        // the previous test), then explicitly widen back to unlimited.
        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            ContactVCardMaxExportRows = new CapacityLimit { Value = 5 },
            ContactVCardMaxImportEntries = new CapacityLimit { Value = 5 },
        })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            ContactVCardMaxImportEntries = new CapacityLimit { Unlimited = true },
        })).EnsureSuccessStatusCode();

        var batch = string.Concat(Enumerable.Range(0, 50).Select(i => Vcard($"UID:many-{i}", $"FN:P{i}", $"ORG:P{i}")));
        var result = await ImportAsync(client, batch);

        Assert.Equal(50, result!.CreatedCount);
    }

    [Fact]
    public async Task Import_OverConfiguredByteCap_IsRejectedAtTheTransport_BelowCapStillSucceeds()
    {
        // PR #403 test-review finding: ImportSizeLimitMiddleware IS the fix for the vCard import's size
        // cap actually taking effect (the dead [RequestSizeLimit] it replaced never touched the real
        // multipart limit) — but nothing exercised it over real HTTP; every other size-cap test drives
        // the service directly with a raw contentLength. This posts an actual oversized multipart body
        // and confirms the transport itself rejects it, then confirms a same-shape body under the cap
        // still succeeds (so a rejection above isn't just "everything is broken").
        await using var factory = new ApiFactory(
            [.. ReadWrite, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate]);
        using var client = factory.CreateClient();
        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            ContactVCardMaxImportMegabytes = 1, // the smallest legal value, [Range(1, 1024)]
        })).EnsureSuccessStatusCode();

        var overCap = Vcard("UID:oversize-1", "FN:Big", "ORG:Big", "NOTE:" + new string('x', 2 * 1024 * 1024));
        var overResponse = await PostVCardAsync(client, overCap);
        // MultipartBodyLengthLimit failing form-reads a model-binding failure ([ApiController]'s
        // automatic ModelState validation), not an unhandled exception — 400, not 500.
        Assert.Equal(HttpStatusCode.BadRequest, overResponse.StatusCode);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(ctx.Contacts);

        var underCap = await ImportAsync(client, Vcard("UID:undersize-1", "FN:Small", "ORG:Small"));
        Assert.Equal(1, underCap!.CreatedCount);
    }

    // ------------------------------------------------------------------ permission matrix

    [Fact]
    public async Task Import_WithOnlyCreateClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.ContactsCreate, PermissionClaims.ContactsRead]);
        using var client = factory.CreateClient();

        var response = await PostVCardAsync(client, Vcard("UID:p1", "FN:X", "ORG:X"), "x.vcf");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Import_WithOnlyUpdateClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.ContactsUpdate, PermissionClaims.ContactsRead]);
        using var client = factory.CreateClient();

        var response = await PostVCardAsync(client, Vcard("UID:p2", "FN:X", "ORG:X"), "x.vcf");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Import_WithBothCreateAndUpdateClaims_Succeeds()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await PostVCardAsync(client, Vcard("UID:p3", "FN:Acme", "ORG:Acme"), "x.vcf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Import_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await PostVCardAsync(client, Vcard("UID:p4", "FN:X", "ORG:X"), "x.vcf");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Vcard(params string[] lines) =>
        "BEGIN:VCARD\r\nVERSION:4.0\r\n" + string.Join("\r\n", lines) + "\r\nEND:VCARD\r\n";

    private static async Task<VCardImportResult?> ImportAsync(HttpClient client, string vcf)
    {
        var response = await PostVCardAsync(client, vcf);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VCardImportResult>();
    }

    private static async Task<HttpResponseMessage> PostVCardAsync(
        HttpClient client, string vcf, string fileName = "contacts.vcf", string contentType = "text/vcard")
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(vcf));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        return await client.PostAsync($"{BasePath}/vcard", content);
    }

    private static async Task<Guid> SeedContactAsync(WebApplicationFactory<Program> factory, string legalName)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = legalName.ToUpperInvariant(),
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = legalName },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
