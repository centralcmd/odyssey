using Odyssey.Dtos;
using System.Net;
using System.Net.Http.Json;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.Api.Tests;

public class InsuranceApiTests
{
    private const string ActorUserId = "insurance-actor-id";
    private const string Path = "/api/insurance-policies";

    // A fixed UTC "today" so derived coverage status is deterministic.
    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ReadOnly = [PermissionClaims.InsuranceRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.InsuranceRead, PermissionClaims.InsuranceCreate,
        PermissionClaims.InsuranceUpdate, PermissionClaims.InsuranceDelete,
    ];

    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.InsuranceRead, PermissionClaims.InsuranceCreate,
        PermissionClaims.InsuranceUpdate, PermissionClaims.InsuranceDelete, PermissionClaims.FilesRead,
    ];

    // ── Authorization matrix (criterion #8) ───────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutReadPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mutations_WithReadOnlyPermission_ReturnForbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewPolicy(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var delete = await client.DeleteAsync($"{Path}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ── Create → read round-trip + minimal projections (criteria #1, #9) ───────

    [Fact]
    public async Task Post_PersistsWithMinimalProjections_NoCoverage()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, accountId) = await SeedInsurerAndAccountAsync(factory);
        using var client = factory.CreateClient();

        var request = NewPolicy(insurerId, accountId);
        var post = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = await post.Content.ReadFromJsonAsync<ExistingInsurancePolicy>();

        var fetched = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{created!.InsurancePolicyId}");
        Assert.Equal(CoverageStatus.NoCoverage, fetched!.CoverageStatus);
        Assert.Null(fetched.CurrentRenewal);
        Assert.Equal(insurerId, fetched.Insurer.ContactId);
        Assert.Equal("Acme Insurance", fetched.Insurer.Name);
        Assert.NotNull(fetched.InsuredAccount);
        Assert.Equal(accountId, fetched.InsuredAccount!.AccountId);
        Assert.Equal("Apartment", fetched.InsuredAccount.Name);
    }

    [Fact]
    public async Task Get_ReadPath_OmitsInsurerOrgNumberAndDescription()
    {
        // Serialize the raw JSON: the minimal InsurerReference must not leak the richer contact
        // fields gated by contacts.read (criterion #9).
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, accountId) = await SeedInsurerAndAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurerId, accountId);

        var json = await client.GetStringAsync($"{Path}/{id}");
        Assert.DoesNotContain("organizationNumber", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ORG-12345", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret insurer notes", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── Hard delete + archive / unarchive ──────────────────────────────────────

    [Fact]
    public async Task Delete_HardDeletes_PolicyGone_And_RenewalsCascade()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var insurerId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurerId);
        var addRenewal = await client.PostAsJsonAsync($"{Path}/{id}/renewals",
            Renewal(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)));
        addRenewal.EnsureSuccessStatusCode();

        var delete = await client.DeleteAsync($"{Path}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // Hard delete: the policy is gone entirely (a soft-archive would still GET 200).
        var fetch = await client.GetAsync($"{Path}/{id}");
        Assert.Equal(HttpStatusCode.NotFound, fetch.StatusCode);

        var list = await client.GetPagedItemsAsync<InsurancePolicyListItem>(Path);
        Assert.DoesNotContain(list!, p => p.InsurancePolicyId == id);

        // The cascade also removed the renewal rows.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.PolicyRenewals.AnyAsync(r => r.InsurancePolicyId == id));
    }

    [Fact]
    public async Task Put_ArchiveThenUnarchive_TogglesListAndSummary()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var insurerId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurerId);
        // An active renewal so the policy contributes to the portfolio summary.
        (await client.PostAsJsonAsync($"{Path}/{id}/renewals",
            Renewal(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)))).EnsureSuccessStatusCode();

        var before = await client.GetFromJsonAsync<InsurancePortfolioSummary>($"{Path}/summary");
        Assert.Equal(1, before!.TotalPolicies);

        // Archive via PUT.
        var archive = await client.PutAsJsonAsync($"{Path}/{id}", UpdatePolicy(insurerId, archived: true));
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        var fetched = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}");
        Assert.NotNull(fetched!.Archived);                       // kept, not deleted
        Assert.Equal(CoverageStatus.Archived, fetched.CoverageStatus); // terminal status takes precedence
        // Archived policies are shown in the default list (the design system no longer hides them);
        // a status filter still narrows to just the Archived ones.
        var defaultList = await client.GetPagedItemsAsync<InsurancePolicyListItem>(Path);
        Assert.Contains(defaultList!, p => p.InsurancePolicyId == id
            && p.Archived is not null && p.CoverageStatus == CoverageStatus.Archived);
        var archivedList = await client.GetPagedItemsAsync<InsurancePolicyListItem>($"{Path}?statuses=Archived");
        Assert.Contains(archivedList!, p => p.InsurancePolicyId == id
            && p.Archived is not null && p.CoverageStatus == CoverageStatus.Archived); // reachable, reads Archived
        var archivedSummary = await client.GetFromJsonAsync<InsurancePortfolioSummary>($"{Path}/summary");
        Assert.Equal(0, archivedSummary!.TotalPolicies);         // dropped from the live portfolio rollup
        Assert.Equal(1, archivedSummary.CountsByStatus.Archived); // but surfaced in the by-status breakdown
        Assert.Empty(archivedSummary.CountsByType);                // and excluded from the by-type rollup (live only)

        // A non-Archived status filter keeps a genuinely-active policy (a current-year renewal ⇒ Active
        // under FixedToday) and still excludes the archived one — the invariant that moved from the
        // removed SQL pre-filter onto the derived-status post-filter.
        var activeId = await CreateAsync(client, insurerId);
        (await client.PostAsJsonAsync($"{Path}/{activeId}/renewals",
            Renewal(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)))).EnsureSuccessStatusCode();
        var activeOnly = await client.GetPagedItemsAsync<InsurancePolicyListItem>($"{Path}?statuses=Active");
        Assert.Contains(activeOnly!, p => p.InsurancePolicyId == activeId);
        Assert.DoesNotContain(activeOnly!, p => p.InsurancePolicyId == id);

        // Unarchive via PUT.
        var unarchive = await client.PutAsJsonAsync($"{Path}/{id}", UpdatePolicy(insurerId, archived: false));
        Assert.Equal(HttpStatusCode.OK, unarchive.StatusCode);

        var restored = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}");
        Assert.Null(restored!.Archived);
        var afterSummary = await client.GetFromJsonAsync<InsurancePortfolioSummary>($"{Path}/summary");
        Assert.Equal(0, afterSummary!.CountsByStatus.Archived);   // nothing archived after the restore
        Assert.Equal(2, afterSummary.TotalPolicies);              // restored id + the active guard policy
    }

    [Fact]
    public async Task Put_RevalidatesReferences_OnlyWhenTheyChange()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, accountId) = await SeedInsurerAndAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurerId, accountId);

        // The linked insurer is archived after the policy was created.
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var insurer = await context.Contacts.Include(c => c.OrganizationDetails).FirstAsync(c => c.ContactId == insurerId);
            insurer.Archived = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // An unrelated edit that keeps the same (now-archived) insurer + account still succeeds —
        // references are only revalidated when their id changes.
        var rename = new UpdateInsurancePolicy
        {
            Name = "Renamed home contents",
            Type = Odyssey.Dtos.Finance.InsurancePolicyType.Contents,
            InsurerId = insurerId,
            InsuredAccountId = accountId,
        };
        var unchanged = await client.PutAsJsonAsync($"{Path}/{id}", rename);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);

        // Switching to a *different* archived insurer is a change → revalidated → 400.
        var otherArchivedInsurer = await SeedContactAsync(factory, archived: true);
        var switchInsurer = await client.PutAsJsonAsync($"{Path}/{id}", rename with { InsurerId = otherArchivedInsurer });
        Assert.Equal(HttpStatusCode.BadRequest, switchInsurer.StatusCode);
    }

    // ── Mass-assignment guard (criterion #10) ──────────────────────────────────

    [Fact]
    public async Task Post_WithNestedInsurerObject_DoesNotMutateContact()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, accountId) = await SeedInsurerAndAccountAsync(factory);
        using var client = factory.CreateClient();

        // A populated nested "insurer" object must be ignored — only the scalar insurerId links.
        var body = new
        {
            name = "Home contents",
            type = (int)Odyssey.Dtos.Finance.InsurancePolicyType.Contents,
            insurerId,
            insuredAccountId = accountId,
            insurer = new { contactId = insurerId, name = "HIJACKED", organizationNumber = "EVIL" },
        };

        var post = await client.PostAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var insurer = await context.Contacts.Include(c => c.OrganizationDetails).FirstAsync(c => c.ContactId == insurerId);
        Assert.Equal("Acme Insurance", insurer.OrganizationDetails!.LegalName);
        Assert.Equal("ORG-12345", insurer.OrganizationNumber);
    }

    // ── Validation (criterion #4) ──────────────────────────────────────────────

    [Fact]
    public async Task Post_ArchivedInsurer_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var insurerId = await SeedContactAsync(factory, archived: true);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewPolicy(insurerId));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_UnknownInsurer_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewPolicy(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task AddRenewal_ToBeforeFrom_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, _) = await SeedInsurerAndAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurerId);
        var renewal = Renewal(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                              new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var response = await client.PostAsJsonAsync($"{Path}/{id}/renewals", renewal);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddRenewal_UnknownCurrency_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, _) = await SeedInsurerAndAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurerId);
        var renewal = Renewal(YearStart, YearEnd);
        renewal.PremiumCurrencyCode = "ZZZ";

        var response = await client.PostAsJsonAsync($"{Path}/{id}/renewals", renewal);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Status determinism (criteria #2, #3) ───────────────────────────────────

    [Theory]
    [InlineData("2026-01-01", "2026-12-31", CoverageStatus.Active)]
    [InlineData("2026-01-01", "2026-06-30", CoverageStatus.ExpiringSoon)]
    [InlineData("2027-01-01", "2027-12-31", CoverageStatus.Upcoming)]
    [InlineData("2025-01-01", "2025-12-31", CoverageStatus.Lapsed)]
    public async Task SingleRenewal_DerivesOrderedStatus(string from, string to, CoverageStatus expected)
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, _) = await SeedInsurerAndAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurerId);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/renewals", Renewal(Parse(from), Parse(to)));
        add.EnsureSuccessStatusCode();

        var fetched = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}");
        Assert.Equal(expected, fetched!.CoverageStatus);
    }

    [Fact]
    public async Task OverlappingRenewals_CurrentIsLatestFromDate()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, _) = await SeedInsurerAndAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurerId);
        // Both windows contain FixedToday (2026-06-15); the later FromDate wins.
        await client.PostAsJsonAsync($"{Path}/{id}/renewals", Renewal(Parse("2026-01-01"), Parse("2026-12-31"), premium: 100m));
        await client.PostAsJsonAsync($"{Path}/{id}/renewals", Renewal(Parse("2026-03-01"), Parse("2027-02-28"), premium: 200m));

        var fetched = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}");
        Assert.NotNull(fetched!.CurrentRenewal);
        Assert.Equal(200m, fetched.CurrentRenewal!.Premium);
        Assert.Equal(Parse("2026-03-01"), fetched.CurrentRenewal.FromDate);
    }

    [Fact]
    public async Task DeletingOnlyRenewal_RevertsToNoCoverage()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, _) = await SeedInsurerAndAccountAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurerId);
        var add = await client.PostAsJsonAsync($"{Path}/{id}/renewals", Renewal(Parse("2026-01-01"), Parse("2026-12-31")));
        var renewal = await add.Content.ReadFromJsonAsync<ExistingPolicyRenewal>();

        var beforeDelete = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}");
        Assert.Equal(CoverageStatus.Active, beforeDelete!.CoverageStatus);

        var delete = await client.DeleteAsync($"{Path}/{id}/renewals/{renewal!.PolicyRenewalId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var afterDelete = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{id}");
        Assert.Equal(CoverageStatus.NoCoverage, afterDelete!.CoverageStatus);
    }

    [Fact]
    public async Task Renewal_WrongParentPolicy_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, _) = await SeedInsurerAndAccountAsync(factory);
        using var client = factory.CreateClient();

        var policyA = await CreateAsync(client, insurerId);
        var policyB = await CreateAsync(client, insurerId);
        var add = await client.PostAsJsonAsync($"{Path}/{policyA}/renewals", Renewal(YearStart, YearEnd));
        var renewal = await add.Content.ReadFromJsonAsync<ExistingPolicyRenewal>();

        // Renewal belongs to A; addressing it under B must 404 (parent-chain assertion).
        var response = await client.DeleteAsync($"{Path}/{policyB}/renewals/{renewal!.PolicyRenewalId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Files (criteria #5, #11) ───────────────────────────────────────────────

    [Fact]
    public async Task AttachFile_DownloadDetach_BlobSurvives_ParentChainEnforced()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        var (insurerId, _) = await SeedInsurerAndAccountAsync(factory);
        var pdfId = await SeedFileAsync(factory, "contract.pdf", "application/pdf");
        var textId = await SeedFileAsync(factory, "notes.txt", "text/plain");
        using var client = factory.CreateClient();

        var policyA = await CreateAsync(client, insurerId);
        var policyB = await CreateAsync(client, insurerId);

        var attach = await client.PostAsJsonAsync($"{Path}/{policyA}/files", AttachRequest(pdfId));
        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);

        // Disallowed content type.
        var bad = await client.PostAsJsonAsync($"{Path}/{policyA}/files", AttachRequest(textId));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Duplicate (parent, file) → 409.
        var duplicate = await client.PostAsJsonAsync($"{Path}/{policyA}/files", AttachRequest(pdfId));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Download via the parent-scoped route sets safe headers (criterion #11).
        var download = await client.GetAsync($"{Path}/{policyA}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("nosniff", download.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join(",", v) : null);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);

        // A file attached to A is not downloadable through B.
        var crossPolicy = await client.GetAsync($"{Path}/{policyB}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NotFound, crossPolicy.StatusCode);

        // Detach removes the join only; the underlying blob survives.
        var detach = await client.DeleteAsync($"{Path}/{policyA}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NoContent, detach.StatusCode);

        var afterDetach = await client.GetAsync($"{Path}/{policyA}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDetach.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.True(await context.FileMetadata.AnyAsync(f => f.Id == pdfId));
    }

    [Fact]
    public async Task AttachRenewalFile_DownloadDetach_BlobSurvives_CompoundParentChainEnforced()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        var insurerId = await SeedContactAsync(factory);
        var pdfId = await SeedFileAsync(factory, "schedule.pdf", "application/pdf");
        var textId = await SeedFileAsync(factory, "notes.txt", "text/plain");
        using var client = factory.CreateClient();

        var policyA = await CreateAsync(client, insurerId);
        var policyB = await CreateAsync(client, insurerId);
        var renewalA = await AddRenewalAsync(client, policyA);
        var renewalB = await AddRenewalAsync(client, policyB);

        var attach = await client.PostAsJsonAsync($"{Path}/{policyA}/renewals/{renewalA}/files", AttachRequest(pdfId));
        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);

        // Disallowed content type → 400 (same allow-list as the policy route, distinct endpoint).
        var bad = await client.PostAsJsonAsync($"{Path}/{policyA}/renewals/{renewalA}/files", AttachRequest(textId));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Duplicate (renewal, file) → 409.
        var duplicate = await client.PostAsJsonAsync($"{Path}/{policyA}/renewals/{renewalA}/files", AttachRequest(pdfId));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Download via the renewal-scoped route sets safe headers.
        var download = await client.GetAsync($"{Path}/{policyA}/renewals/{renewalA}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("nosniff", download.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join(",", v) : null);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);

        // Compound parent-chain guard (RenewalExists(id, renewalId) && IsFileAttachedToRenewal):
        // the file is reachable via neither the wrong renewal nor the wrong policy id.
        var wrongRenewal = await client.GetAsync($"{Path}/{policyA}/renewals/{renewalB}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NotFound, wrongRenewal.StatusCode);   // renewalB is not under policyA
        var wrongPolicy = await client.GetAsync($"{Path}/{policyB}/renewals/{renewalA}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NotFound, wrongPolicy.StatusCode);    // renewalA is not under policyB

        // Detach removes the join only; the blob survives.
        var detach = await client.DeleteAsync($"{Path}/{policyA}/renewals/{renewalA}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NoContent, detach.StatusCode);
        var afterDetach = await client.GetAsync($"{Path}/{policyA}/renewals/{renewalA}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDetach.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.True(await context.FileMetadata.AnyAsync(f => f.Id == pdfId));
    }

    [Fact]
    public async Task AttachPolicyFile_ExceedingCap_Returns422()
    {
        // Shrink the per-parent file cap so the boundary is cheap to hit. Set as a settings row, not as
        // configuration: the cap moved out of `InsuranceOptions` in issue #421 Wave 3.
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.InsuranceMaxFilesPerParent, "2");
        var insurerId = await SeedContactAsync(factory);
        var f1 = await SeedFileAsync(factory, "a.pdf", "application/pdf");
        var f2 = await SeedFileAsync(factory, "b.pdf", "application/pdf");
        var f3 = await SeedFileAsync(factory, "c.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var policy = await CreateAsync(client, insurerId);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{Path}/{policy}/files", AttachRequest(f1))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{Path}/{policy}/files", AttachRequest(f2))).StatusCode);

        var overCap = await client.PostAsJsonAsync($"{Path}/{policy}/files", AttachRequest(f3));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overCap.StatusCode);
    }

    [Fact]
    public async Task AddRenewal_ExceedingCap_Returns422()
    {
        // Shrink the per-policy renewal cap so the boundary is cheap to hit. Mirrors the file-cap test;
        // guards the off-by-one in `count >= MaxRenewalsPerPolicy` (#240 H3).
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.InsuranceMaxRenewalsPerPolicy, "2");
        var insurerId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var policy = await CreateAsync(client, insurerId);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync($"{Path}/{policy}/renewals", Renewal(Parse("2024-01-01"), Parse("2024-12-31")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync($"{Path}/{policy}/renewals", Renewal(Parse("2025-01-01"), Parse("2025-12-31")))).StatusCode);

        var overCap = await client.PostAsJsonAsync($"{Path}/{policy}/renewals", Renewal(Parse("2026-01-01"), Parse("2026-12-31")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overCap.StatusCode);
    }

    [Fact]
    public async Task PutRenewal_UpdatesValues_RevalidatesDates_And_WrongParentReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var insurerId = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var policyA = await CreateAsync(client, insurerId);
        var policyB = await CreateAsync(client, insurerId);
        var renewalA = await AddRenewalAsync(client, policyA);

        var update = new UpdatePolicyRenewal
        {
            FromDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ToDate = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            Premium = 4242m,
            PremiumCurrencyCode = "USD",
            CoverageAmount = 999000m,
            CoverageCurrencyCode = "USD",
            Notes = "raised cover",
        };

        // Happy path: the new values round-trip.
        var put = await client.PutAsJsonAsync($"{Path}/{policyA}/renewals/{renewalA}", update);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var policy = await client.GetFromJsonAsync<ExistingInsurancePolicy>($"{Path}/{policyA}");
        var stored = policy!.Renewals.Single(r => r.PolicyRenewalId == renewalA);
        Assert.Equal(4242m, stored.Premium);
        Assert.Equal(999000m, stored.CoverageAmount);
        Assert.Equal("raised cover", stored.Notes);

        // Revalidation on the modify path: ToDate < FromDate → 400.
        var badDates = await client.PutAsJsonAsync($"{Path}/{policyA}/renewals/{renewalA}",
            update with { FromDate = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc), ToDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        Assert.Equal(HttpStatusCode.BadRequest, badDates.StatusCode);

        // Parent-chain: renewalA addressed under policyB → 404.
        var wrongParent = await client.PutAsJsonAsync($"{Path}/{policyB}/renewals/{renewalA}", update);
        Assert.Equal(HttpStatusCode.NotFound, wrongParent.StatusCode);
    }

    [Fact]
    public async Task AttachFile_WithoutFilesReadClaim_ReturnsForbidden()
    {
        // Confused-deputy guard (criterion #6): insurance.update alone is not enough to attach.
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, _) = await SeedInsurerAndAccountAsync(factory);
        var pdfId = await SeedFileAsync(factory, "contract.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, insurerId);

        var attach = await client.PostAsJsonAsync($"{Path}/{id}/files", AttachRequest(pdfId));
        Assert.Equal(HttpStatusCode.Forbidden, attach.StatusCode);
    }

    // ── Summary (criterion #7) ─────────────────────────────────────────────────

    [Fact]
    public async Task Summary_MultiCurrency_ConvertsAndExcludesMissingRates()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, _) = await SeedInsurerAndAccountAsync(factory);
        await SeedRateAsync(factory, "NOK", "USD", 0.1m); // JPY→USD deliberately missing.
        using var client = factory.CreateClient();

        // Active NOK policy.
        var nokPolicy = await CreateAsync(client, insurerId);
        await client.PostAsJsonAsync($"{Path}/{nokPolicy}/renewals",
            Renewal(Parse("2026-01-01"), Parse("2026-12-31"), premium: 1000m, currency: "NOK", coverage: 500000m));

        // Active JPY policy (no rate).
        var jpyPolicy = await CreateAsync(client, insurerId);
        await client.PostAsJsonAsync($"{Path}/{jpyPolicy}/renewals",
            Renewal(Parse("2026-01-01"), Parse("2026-06-30"), premium: 2000m, currency: "JPY", coverage: 300000m));

        var summary = await client.GetFromJsonAsync<InsurancePortfolioSummary>($"{Path}/summary?baseCurrency=USD");

        Assert.Equal(2, summary!.TotalPolicies);
        Assert.Equal(1, summary.CountsByStatus.Active);
        Assert.Equal(1, summary.CountsByStatus.ExpiringSoon);
        Assert.Equal(2, summary.CountsByType.Sum(t => t.Count)); // both live policies counted by type
        Assert.Equal("USD", summary.BaseCurrency);
        // Only the NOK premium converts (1000 * 0.1 = 100); JPY is excluded.
        Assert.Equal(100m, summary.ConvertedTotalPremium);
        Assert.Contains("JPY", summary.UnconvertedCurrencies);
        Assert.DoesNotContain("NOK", summary.UnconvertedCurrencies);
    }

    [Fact]
    public async Task Summary_MultiCurrency_SumsConvertibleTotals_AndListsAllMissingRates()
    {
        // Two convertible currencies (NOK, SEK) + one missing-rate currency (JPY). Asserts both
        // converted grand totals (premium AND coverage) and the full UnconvertedCurrencies set (#240 M2).
        await using var factory = new ApiFactory(ReadWrite);
        var (insurerId, _) = await SeedInsurerAndAccountAsync(factory);
        await SeedRateAsync(factory, "NOK", "USD", 0.1m);
        await SeedRateAsync(factory, "SEK", "USD", 0.2m); // JPY→USD deliberately missing.
        using var client = factory.CreateClient();

        var nokPolicy = await CreateAsync(client, insurerId);
        (await client.PostAsJsonAsync($"{Path}/{nokPolicy}/renewals",
            Renewal(Parse("2026-01-01"), Parse("2026-12-31"), premium: 1000m, currency: "NOK", coverage: 500000m)))
            .EnsureSuccessStatusCode();

        var sekPolicy = await CreateAsync(client, insurerId);
        (await client.PostAsJsonAsync($"{Path}/{sekPolicy}/renewals",
            Renewal(Parse("2026-01-01"), Parse("2026-12-31"), premium: 2000m, currency: "SEK", coverage: 100000m)))
            .EnsureSuccessStatusCode();

        var jpyPolicy = await CreateAsync(client, insurerId);
        (await client.PostAsJsonAsync($"{Path}/{jpyPolicy}/renewals",
            Renewal(Parse("2026-01-01"), Parse("2026-12-31"), premium: 3000m, currency: "JPY", coverage: 200000m)))
            .EnsureSuccessStatusCode();

        var summary = (await client.GetFromJsonAsync<InsurancePortfolioSummary>($"{Path}/summary?baseCurrency=USD"))!;

        Assert.Equal(3, summary.TotalPolicies);
        Assert.Equal("USD", summary.BaseCurrency);
        // Premium: 1000*0.1 + 2000*0.2 = 100 + 400 = 500. JPY (3000) excluded.
        Assert.Equal(500m, summary.ConvertedTotalPremium);
        // Coverage: 500000*0.1 + 100000*0.2 = 50000 + 20000 = 70000. JPY (200000) excluded.
        Assert.Equal(70000m, summary.ConvertedTotalCoverage);
        // The unconverted set is exactly {JPY} — no convertible currency leaks in.
        Assert.Equal("JPY", Assert.Single(summary.UnconvertedCurrencies));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static readonly DateTime YearStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime YearEnd = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime Parse(string date) =>
        DateTime.SpecifyKind(DateTime.Parse(date), DateTimeKind.Utc);

    private static NewInsurancePolicy NewPolicy(Guid insurerId, Guid? insuredAccountId = null) => new()
    {
        Name = "Home contents",
        Type = Odyssey.Dtos.Finance.InsurancePolicyType.Contents,
        InsurerId = insurerId,
        InsuredAccountId = insuredAccountId,
    };

    private static UpdateInsurancePolicy UpdatePolicy(Guid insurerId, bool archived) => new()
    {
        Name = "Home contents",
        Type = Odyssey.Dtos.Finance.InsurancePolicyType.Contents,
        InsurerId = insurerId,
        Archived = archived,
    };

    private static NewPolicyRenewal Renewal(
        DateTime from, DateTime to, decimal premium = 100m, string currency = "USD", decimal coverage = 10000m) => new()
    {
        FromDate = from,
        ToDate = to,
        Premium = premium,
        PremiumCurrencyCode = currency,
        CoverageAmount = coverage,
        CoverageCurrencyCode = currency,
    };

    private static AttachInsurancePolicyFileRequest AttachRequest(Guid fileId) => new()
    {
        FileId = fileId,
        FileType = Odyssey.Dtos.Finance.PolicyFileType.Contract,
    };

    private static async Task<Guid> CreateAsync(HttpClient client, Guid insurerId, Guid? accountId = null)
    {
        var post = await client.PostAsJsonAsync(Path, NewPolicy(insurerId, accountId));
        post.EnsureSuccessStatusCode();
        var created = await post.Content.ReadFromJsonAsync<ExistingInsurancePolicy>();
        return created!.InsurancePolicyId;
    }

    private static async Task<Guid> AddRenewalAsync(HttpClient client, Guid policyId)
    {
        var add = await client.PostAsJsonAsync($"{Path}/{policyId}/renewals", Renewal(YearStart, YearEnd));
        add.EnsureSuccessStatusCode();
        var renewal = await add.Content.ReadFromJsonAsync<ExistingPolicyRenewal>();
        return renewal!.PolicyRenewalId;
    }

    private static async Task EnsureDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
    }

    private static async Task<Guid> SeedContactAsync(WebApplicationFactory<Program> factory, bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        // Reference currencies live in OdysseyContext (HasData); ensure it exists too (Contact moved to
        // OdysseyContext, so seeding a contact no longer creates OdysseyContext as a side effect).
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "acme insurance",
            Type = ContactType.Organization,
            OrganizationNumber = "ORG-12345",
            Notes = "secret insurer notes",
            Archived = archived ? DateTime.UtcNow : null,
            OrganizationDetails = new() { LegalName = "Acme Insurance", OrganizationNumber = "ORG-12345" },
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task<(Guid InsurerId, Guid AccountId)> SeedInsurerAndAccountAsync(WebApplicationFactory<Program> factory)
    {
        var insurerId = await SeedContactAsync(factory);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var accountId = Guid.NewGuid();
        context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = "Apartment",
            Description = "Insured asset",
            Opened = DateTime.UtcNow,
            AccountType = ContextAccountType.Property,
            CurrencyCode = "USD",
        });
        await context.SaveChangesAsync();
        return (insurerId, accountId);
    }

    private static async Task SeedRateAsync(WebApplicationFactory<Program> factory, string from, string to, decimal rate)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        context.ExchangeRates.Add(new ExchangeRate
        {
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = rate,
            AsOf = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private static async Task<Guid> SeedFileAsync(WebApplicationFactory<Program> factory, string fileName, string contentType)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadataId = Guid.NewGuid();
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = metadataId,
            UploadedByUserId = ActorUserId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = 3,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = blob.Id,
            FileBlob = blob,
        });
        await context.SaveChangesAsync();
        return metadataId;
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(
            IReadOnlyCollection<string>? permissions,
            IReadOnlyDictionary<string, string?>? configuration = null)
            : base(permissions, ActorUserId, configuration, configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedToday));
            })
        {
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
