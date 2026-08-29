using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.Api.Tests;

public class TaxStatementApiTests
{
    private const string ActorUserId = "tax-statements-actor-id";
    private const string Path = "/api/tax-statements";

    private static readonly string[] ReadOnly = [PermissionClaims.TaxesRead];

    private static readonly string[] ReadWrite =
        [PermissionClaims.TaxesRead, PermissionClaims.TaxesCreate, PermissionClaims.TaxesUpdate, PermissionClaims.TaxesDelete];

    private static NewTaxStatement NewStatement() => new()
    {
        Name = "2024 assessment",
        FiscalYear = 2024,
        StartDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        BaseCurrencyCode = "USD",
    };

    // ── Authorization matrix ──────────────────────────────────────────────────

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

        var post = await client.PostAsJsonAsync(Path, NewStatement());
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var put = await client.PutAsJsonAsync($"{Path}/{Guid.NewGuid()}", new UpdateTaxStatement
        {
            Name = "x", FiscalYear = 2024,
            StartDate = DateTime.UtcNow, EndDate = DateTime.UtcNow,
        });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        var delete = await client.DeleteAsync($"{Path}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_PersistsAndIsListed()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await EnsureDatabaseAsync(factory);

        var request = NewStatement();
        request.SettlementAmount = 1000m;
        request.FiledAtUtc = new DateTime(2025, 4, 30, 0, 0, 0, DateTimeKind.Utc);
        request.TaxOfficeApprovedAtUtc = new DateTime(2025, 6, 20, 0, 0, 0, DateTimeKind.Utc);

        var post = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = await post.Content.ReadFromJsonAsync<ExistingTaxStatement>();

        var list = await client.GetPagedItemsAsync<ExistingTaxStatement>(Path);
        var listed = Assert.Single(list!);
        Assert.Equal(created!.TaxStatementId, listed.TaxStatementId);
        Assert.Equal(1000m, listed.SettlementAmount);
        Assert.Equal(request.FiledAtUtc, listed.FiledAtUtc);
        Assert.Equal(request.TaxOfficeApprovedAtUtc, listed.TaxOfficeApprovedAtUtc);
    }

    [Fact]
    public async Task Post_EndBeforeStart_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await EnsureDatabaseAsync(factory);

        var request = NewStatement();
        request.EndDate = request.StartDate.AddDays(-1);

        var post = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_UpdatesDeclaredFigures_ReflectedInReport()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await EnsureDatabaseAsync(factory);

        var id = await CreateAsync(client);

        var update = new UpdateTaxStatement
        {
            Name = "2024 assessment (final)",
            FiscalYear = 2024,
            StartDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            BaseCurrencyCode = "USD",
            DeclaredTotalIncome = 850000m,
            AssessedTax = 210000m,
        };
        var put = await client.PutAsJsonAsync($"{Path}/{id}", update);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var report = await client.GetFromJsonAsync<TaxStatementReport>($"{Path}/{id}/report");
        Assert.Equal(850000m, report!.Declared.TotalIncome);
        Assert.Equal(210000m, report.Declared.AssessedTax);
        // No tags yet → paidTax 0 → outstandingTax = assessed.
        Assert.Equal(210000m, report.Reconciliation.OutstandingTax);
    }

    [Fact]
    public async Task PatchStatus_PersistsStatusAndComment()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await EnsureDatabaseAsync(factory);

        var id = await CreateAsync(client);

        var patch = await client.PatchAsJsonAsync($"{Path}/{id}/status", new UpdateTaxStatementStatus
        {
            Status = TaxStatementStatus.Flagged,
            StatusComment = "Net worth mismatch",
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var fetched = await client.GetFromJsonAsync<ExistingTaxStatement>($"{Path}/{id}");
        Assert.Equal(TaxStatementStatus.Flagged, fetched!.Status);
        Assert.Equal("Net worth mismatch", fetched.StatusComment);

        var report = await client.GetFromJsonAsync<TaxStatementReport>($"{Path}/{id}/report");
        Assert.Equal(TaxStatementStatus.Flagged, report!.Status);
    }

    [Fact]
    public async Task PutTags_DerivesPaidTaxAndIncome_ExcludesOffCurrency()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (taxTagId, incomeTagId) = await SeedTaggedTransactionsAsync(factory);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        var put = await client.PutAsJsonAsync($"{Path}/{id}/tags", new UpdateTaxStatementTags
        {
            TaxTagIds = [taxTagId],
            IncomeTagIds = [incomeTagId],
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var report = await client.GetFromJsonAsync<TaxStatementReport>($"{Path}/{id}/report");
        Assert.Equal(209000m, report!.Derived.PaidTax);
        Assert.Equal(842000m, report.Derived.ActualIncome);
        Assert.Equal(1, report.ExcludedTransactionCount);
        Assert.Equal(1, report.ExcludedCurrencies["EUR"]);
    }

    [Fact]
    public async Task PutTags_UnknownTag_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await EnsureDatabaseAsync(factory);

        var id = await CreateAsync(client);

        var put = await client.PutAsJsonAsync($"{Path}/{id}/tags", new UpdateTaxStatementTags
        {
            TaxTagIds = [Guid.NewGuid()],
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Delete_ArchivesAndHidesFromList()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await EnsureDatabaseAsync(factory);

        var id = await CreateAsync(client);

        var delete = await client.DeleteAsync($"{Path}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var list = await client.GetPagedItemsAsync<ExistingTaxStatement>(Path);
        Assert.Empty(list!);
    }

    [Fact]
    public async Task List_StatusArchivedFilter_ReturnsArchivedStatements()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await EnsureDatabaseAsync(factory);

        var id = await CreateAsync(client);
        var delete = await client.DeleteAsync($"{Path}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // Regression: "Archived" is a valid status filter (previously 400 — no Archived enum member)
        // and it surfaces the archived statement the default view hides.
        var response = await client.GetAsync($"{Path}?statuses=Archived");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var archived = await client.GetPagedItemsAsync<ExistingTaxStatement>($"{Path}?statuses=Archived");
        var only = Assert.Single(archived!);
        Assert.Equal(id, only.TaxStatementId);
        Assert.NotNull(only.Archived);

        // A stored-status filter still excludes archived rows.
        var live = await client.GetPagedItemsAsync<ExistingTaxStatement>($"{Path}?statuses=New");
        Assert.Empty(live!);
    }

    // ── Files ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AttachFile_PdfAndJpeg_Retrievable_UnsupportedRejected()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var pdfId = await SeedFileAsync(factory, "statement.pdf", "application/pdf");
        var jpegId = await SeedFileAsync(factory, "scan.jpg", "image/jpeg");
        var textId = await SeedFileAsync(factory, "notes.txt", "text/plain");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client);

        var pdf = await client.PostAsJsonAsync($"{Path}/{id}/files", new AttachTaxStatementFileRequest(pdfId));
        Assert.Equal(HttpStatusCode.Created, pdf.StatusCode);

        var jpeg = await client.PostAsJsonAsync($"{Path}/{id}/files", new AttachTaxStatementFileRequest(jpegId));
        Assert.Equal(HttpStatusCode.Created, jpeg.StatusCode);

        var unsupported = await client.PostAsJsonAsync($"{Path}/{id}/files", new AttachTaxStatementFileRequest(textId));
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);

        var downloadPdf = await client.GetAsync($"{Path}/{id}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.OK, downloadPdf.StatusCode);
        // Safe-download headers: forbid content-type sniffing and force an attachment so a
        // mislabeled upload can't render inline in the app origin.
        Assert.Equal("nosniff", downloadPdf.Headers.TryGetValues("X-Content-Type-Options", out var cto)
            ? string.Join(",", cto) : null);
        Assert.Equal("attachment", downloadPdf.Content.Headers.ContentDisposition?.DispositionType);
        var downloadJpeg = await client.GetAsync($"{Path}/{id}/files/{jpegId}");
        Assert.Equal(HttpStatusCode.OK, downloadJpeg.StatusCode);

        // Reflected in the statement DTO.
        var fetched = await client.GetFromJsonAsync<ExistingTaxStatement>($"{Path}/{id}");
        Assert.Equal(2, fetched!.Files.Count);

        var detach = await client.DeleteAsync($"{Path}/{id}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NoContent, detach.StatusCode);

        var afterDetach = await client.GetAsync($"{Path}/{id}/files/{pdfId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDetach.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // The Finance in-memory DB seeds its currency table (HasData) only once EnsureCreated runs;
    // currency validation on POST needs it, so ensure creation before the first request.
    private static async Task EnsureDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
    }

    private static async Task<Guid> CreateAsync(HttpClient client)
    {
        var post = await client.PostAsJsonAsync(Path, NewStatement());
        post.EnsureSuccessStatusCode();
        var created = await post.Content.ReadFromJsonAsync<ExistingTaxStatement>();
        return created!.TaxStatementId;
    }

    private static async Task<(Guid TaxTagId, Guid IncomeTagId)> SeedTaggedTransactionsAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var account = new Account
        {
            AccountId = Guid.NewGuid(),
            Name = "Checking",
            Description = "Test",
            Opened = DateTime.UtcNow,
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);

        var taxTag = new TransactionTag { TransactionTagId = Guid.NewGuid(), Name = "Advance tax" };
        var incomeTag = new TransactionTag { TransactionTagId = Guid.NewGuid(), Name = "Wages" };
        context.TransactionTags.AddRange(taxTag, incomeTag);

        context.Transactions.AddRange(
            Txn(account.AccountId, taxTag.TransactionTagId, 209000m, new DateTime(2024, 11, 1, 0, 0, 0, DateTimeKind.Utc), "USD"),
            Txn(account.AccountId, taxTag.TransactionTagId, 500m, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), "EUR"),
            Txn(account.AccountId, incomeTag.TransactionTagId, 842000m, new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), "USD"));

        await context.SaveChangesAsync();
        return (taxTag.TransactionTagId, incomeTag.TransactionTagId);
    }

    private static Transaction Txn(Guid accountId, Guid tagId, decimal amount, DateTime timestamp, string currency)
    {
        var transactionId = Guid.NewGuid();
        return new Transaction
        {
            TransactionId = transactionId,
            Description = "Test",
            Amount = amount,
            TimeStamp = timestamp,
            AccountId = accountId,
            TransactionTagLinks = new List<TransactionTagLink>
            {
                new() { TransactionId = transactionId, TransactionTagId = tagId },
            },
            CurrencyCode = currency,
        };
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

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
