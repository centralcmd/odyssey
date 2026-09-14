using System.Text.RegularExpressions;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The source-lint half of issue #75 §6's invariant: <b>a budget item's tag is set by scalar id only</b>.
///
/// <para>
/// The nesting the change adds is read-only and one-directional. What makes that structural rather
/// than a matter of vigilance is that <c>BudgetItemService.Create</c>/<c>Update</c> assign
/// field-by-field with no DTO-to-entity <c>Adapt</c> — so a nested <c>tag</c> in a request body is
/// inert, because nothing ever maps a request DTO onto the entity. A future
/// <c>newBudgetItem.Adapt&lt;BudgetItem&gt;()</c> would undo that silently and in one line, which is
/// exactly the kind of regression a runtime test catches only if someone thought to write it for the
/// right payload. This catches it at the source.
/// </para>
/// </summary>
public class BudgetItemWritePathSourceTests
{
    // Matches `Adapt<BudgetItem>()` and `Adapt<Context.BudgetItem>()` / `Adapt<Odyssey.Context.BudgetItem>()`,
    // in any of the aliased spellings the codebase uses.
    private static readonly Regex AdaptToEntity =
        new(@"Adapt\s*<\s*(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*BudgetItem\s*>", RegexOptions.Compiled);

    [Fact]
    public void NoServiceAdaptsADtoOntoTheBudgetItemEntity()
    {
        var offenders = SourceFiles()
            .Select(file => (File: file, Text: File.ReadAllText(file)))
            .Where(candidate => AdaptToEntity.IsMatch(candidate.Text))
            .Select(candidate => Path.GetFileName(candidate.File))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Issue #75 §6: a budget item's tag is set by scalar id ONLY, and that invariant is held by "
            + "the write path assigning field-by-field with no DTO→entity Adapt. Mapping a request DTO "
            + "onto BudgetItem would make a nested `tag` in the body live. Offenders: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The write DTO's side of the same invariant: it carries a scalar and no nested tag, so there is
    /// nothing for a mass-assignment to reach even if the lint above were bypassed.
    /// </summary>
    [Fact]
    public void NewBudgetItemCarriesAScalarTagIdAndNoNestedTag()
    {
        var properties = typeof(NewBudgetItem).GetProperties().ToDictionary(p => p.Name, p => p.PropertyType);

        Assert.Equal(typeof(Guid), properties[nameof(NewBudgetItem.TransactionTagId)]);
        Assert.DoesNotContain(typeof(ExistingTransactionTag), properties.Values);
        Assert.Equal(
            ["BudgetId", "CategoryType", "PlannedAmount", "TransactionTagId"],
            properties.Keys.OrderBy(name => name, StringComparer.Ordinal));
    }

    // The projects that could plausibly write a budget item. Scanning the whole tree would also sweep
    // the tests, where an Adapt in an assertion is legitimate.
    private static IEnumerable<string> SourceFiles()
    {
        var root = SolutionRoot();
        return new[] { "Odyssey.Core", "Odyssey.Api", "Odyssey.MigrationService" }
            .Select(project => Path.Combine(root, project))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the solution root from the test binary.");
    }
}
