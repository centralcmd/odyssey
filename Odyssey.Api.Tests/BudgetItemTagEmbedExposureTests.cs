using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The cross-claim exposure boundary for the embedded transaction tag (issue #75 §10.2, AC 25, 28–29).
///
/// <para>
/// <c>ExistingBudgetItem.Tag</c> is reached through <c>budgets.read</c>, which is <b>not</b>
/// <c>transactions.tags.read</c>. That crossover is deliberate and accepted — a budget item IS a plan
/// for a tag, and an id-only payload would force every consumer to hold a second claim and make a
/// second call to render a row — but it is pinned here so it cannot silently widen.
/// </para>
///
/// <para>
/// The assertions run against a <b>live response body</b>, not a projection's member list: a
/// member-level check alone is the shape <see cref="ContactEmbedExposureTests"/> documents as
/// insufficient, having stayed green throughout the period a real leak existed.
/// </para>
/// </summary>
public class BudgetItemTagEmbedExposureTests
{
    private const string ActorUserId = "budget-tag-actor-id";

    /// <summary>
    /// AC 25, structural half. The embed is <see cref="ExistingTransactionTag"/> exactly — a label with
    /// a name, a description and an archival date. A member added to that type, or a wider type swapped
    /// in for it, fails here.
    /// </summary>
    [Fact]
    public void EmbeddedTag_IsExistingTransactionTag_WithExactlyFourMembers()
    {
        var tagProperty = typeof(ExistingBudgetItem).GetProperty(nameof(ExistingBudgetItem.Tag));

        Assert.NotNull(tagProperty);
        Assert.Equal(typeof(ExistingTransactionTag), tagProperty!.PropertyType);

        var members = typeof(ExistingTransactionTag).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(["Archived", "Description", "Name", "TransactionTagId"], members);
    }

    /// <summary>
    /// AC 25, the "second embed" half. The item carries ONE nested object. A second one — a budget, a
    /// contact, a transaction list — would be a new crossover nobody argued for.
    /// </summary>
    [Fact]
    public void ExistingBudgetItem_CarriesExactlyOneNestedObject()
    {
        var nested = typeof(ExistingBudgetItem).GetProperties()
            .Where(p => p.PropertyType.Namespace?.StartsWith("Odyssey.Dtos", StringComparison.Ordinal) == true
                        && !p.PropertyType.IsEnum)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal([nameof(ExistingBudgetItem.Tag)], nested);
    }

    /// <summary>
    /// AC 29. A principal holding <c>budgets.read</c> and no tag claim lists budget items and receives
    /// fully populated tags — the crossover working as designed — and the body carries nothing beyond
    /// the four members.
    /// </summary>
    [Fact]
    public async Task ListBudgetItems_WithoutTagsReadClaim_EmbedsTheWholeTagAndNothingMore()
    {
        await using var factory = new ApiFactory([PermissionClaims.BudgetsRead]);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/api/budget-items");

        using var document = JsonDocument.Parse(body);
        var item = document.RootElement.GetProperty("items")[0];
        var tag = item.GetProperty("tag");

        Assert.Equal(
            ["archived", "description", "name", "transactionTagId"],
            tag.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal("Groceries", tag.GetProperty("name").GetString());
        Assert.Equal("Weekly food", tag.GetProperty("description").GetString());

        // The scalar round-trip key and the embed always agree.
        Assert.Equal(seeded.TagId, tag.GetProperty("transactionTagId").GetGuid());
        Assert.Equal(seeded.TagId, item.GetProperty("transactionTagId").GetGuid());

        // The two columns this change destroyed are gone from the wire too.
        Assert.Equal(
            ["budgetId", "budgetItemId", "categoryType", "plannedAmount", "tag", "transactionTagId"],
            item.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// AC 28. The nesting is READ-ONLY and one-directional: the service assigns field-by-field with no
    /// DTO-to-entity <c>Adapt</c>, and <c>System.Text.Json</c> ignores unknown members, so a nested
    /// <c>tag</c> in a request body is inert. It must not create a tag, mutate the referenced one, or
    /// change which tag the item links.
    /// </summary>
    [Fact]
    public async Task PostWithANestedTagObject_NeitherCreatesNorMutatesNorRelinks()
    {
        await using var factory = new ApiFactory(
            [PermissionClaims.BudgetsRead, PermissionClaims.BudgetsCreate]);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/budget-items", new
        {
            budgetId = seeded.SecondBudgetId,
            categoryType = Odyssey.Dtos.Finance.BudgetCategoryType.Expense,
            plannedAmount = 250m,
            transactionTagId = seeded.TagId,
            // Every way a nested object could try to reach the write path at once.
            tag = new
            {
                transactionTagId = Guid.NewGuid(),
                name = "Injected",
                description = "Injected description",
                archived = "2020-01-01T00:00:00Z",
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        // No tag was created…
        Assert.Equal(1, context.TransactionTags.Count());

        // …the referenced one is untouched…
        var tag = context.TransactionTags.Single();
        Assert.Equal("Groceries", tag.Name);
        Assert.Equal("Weekly food", tag.Description);
        Assert.Null(tag.Archived);

        // …and the new item links the tag the SCALAR named, not the nested one.
        var created = context.BudgetItems.Single(i => i.BudgetId == seeded.SecondBudgetId);
        Assert.Equal(seeded.TagId, created.TransactionTagId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed record Seeded(Guid BudgetId, Guid SecondBudgetId, Guid TagId);

    private static async Task<Seeded> SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var budgetId = Guid.NewGuid();
        var secondBudgetId = Guid.NewGuid();
        var tagId = Guid.NewGuid();

        context.TransactionTags.Add(new TransactionTag
        {
            TransactionTagId = tagId,
            Name = "Groceries",
            Description = "Weekly food",
        });

        context.Budgets.Add(new Budget
        {
            BudgetId = budgetId,
            Name = "2026",
            Description = "Annual",
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            BaseCurrencyCode = "USD",
        });

        context.Budgets.Add(new Budget
        {
            BudgetId = secondBudgetId,
            Name = "2027",
            Description = "Annual",
            StartDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            BaseCurrencyCode = "USD",
        });

        context.BudgetItems.Add(new BudgetItem
        {
            BudgetItemId = Guid.NewGuid(),
            BudgetId = budgetId,
            CategoryType = Odyssey.Context.BudgetCategoryType.Expense,
            PlannedAmount = 500m,
            TransactionTagId = tagId,
        });

        await context.SaveChangesAsync();
        return new Seeded(budgetId, secondBudgetId, tagId);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
