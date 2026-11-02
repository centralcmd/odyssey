using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for issue #383. The server's PUT is a full replacement and an omitted <c>locationId</c> clears a
/// record's place, so the four write DTOs are built ONLY by their builders, which take the link as a required
/// argument (§5.2, AC 1) — a path added later (an occurrence override, a series split, a duplicate, an inline
/// edit) fails here until it uses them. And the location surfaces render plain text only (AC 12, SEC N3).
/// </summary>
public class LocationWriteBuilderLintTests
{
    private static IEnumerable<string> ClientCode() =>
        Directory.EnumerateFiles(ClientSource.Root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string path) => Path.GetRelativePath(ClientSource.Root, path).Replace('\\', '/');

    [Theory]
    [InlineData("NewCalendarEvent", "Pages/Calendar/CalendarWrites.cs")]
    [InlineData("NewRecurrencePattern", "Pages/Calendar/CalendarWrites.cs")]
    [InlineData("NewJournalEntry", "Pages/Journal/JournalEntryDraft.cs")]
    [InlineData("UpdateJournalEntry", "Pages/Journal/JournalEntryDraft.cs")]
    public void A_write_DTO_is_constructed_only_by_its_builder(string dto, string builder)
    {
        var patterns = ConstructionPatterns(dto);

        var offenders = ClientCode()
            .Where(f => Relative(f) != builder)
            .Where(f => patterns.Any(p => p.IsMatch(File.ReadAllText(f))))
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0, $"{dto} is built outside {builder}: {string.Join(", ", offenders)}");
        Assert.Matches(patterns[^1], File.ReadAllText(Path.Combine(ClientSource.Root, builder)));
    }

    /// <summary>
    /// Every way C# builds a <paramref name="dto"/>: <c>new Dto</c>, a (nullable) local or field
    /// <c>Dto x = new(</c>, a property initialiser <c>Dto X { get; set; } = new()</c> (frontend review N6), and a
    /// member declared to return Dto from a target-typed <c>new()</c> — the builder's own shape, last.
    /// </summary>
    private static Regex[] ConstructionPatterns(string dto) =>
    [
        new Regex($@"\bnew\s+{dto}\b"),
        new Regex($@"\b{dto}\??\s+\w+\s*=\s*new\s*\("),
        new Regex($@"\b{dto}\??\s+\w+\s*\{{[^{{}}]*\}}\s*=\s*new\s*\("),
        new Regex($@"\b{dto}\s+\w+\s*\([^)]*\)\s*=>\s*new\s*\("),
    ];

    [Theory]
    [InlineData("private NewCalendarEvent _body = new();")]
    [InlineData("private NewCalendarEvent? _body = new() { Title = \"x\" };")]
    [InlineData("public NewCalendarEvent Body { get; set; } = new();")]
    [InlineData("internal NewCalendarEvent? Body { get; init; } = new() { Title = \"x\" };")]
    [InlineData("var body = new NewCalendarEvent { Title = \"x\" };")]
    public void The_construction_patterns_catch_every_shape(string line)
    {
        Assert.Contains(ConstructionPatterns("NewCalendarEvent")[..3], p => p.IsMatch(line));
    }

    /// <summary>
    /// N6: an <c>AddressWrite</c> decides which place a contact or property links to, so like the four DTOs it is
    /// built in one place — <c>AddressBlockModel.ToWrite</c> and <c>AddressWrite.None</c> — and nowhere else.
    /// </summary>
    [Fact]
    public void An_AddressWrite_is_constructed_only_by_the_address_block_model()
    {
        const string home = "Components/OdsAddressBlockModel.cs";
        var patterns = ConstructionPatterns("AddressWrite")[..3].Append(new Regex(@"\bnew\s*\(\s*AddressWriteKind\.")).ToArray();

        var offenders = ClientCode()
            .Where(f => Relative(f) != home)
            .Where(f => patterns.Any(p => p.IsMatch(File.ReadAllText(f))))
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0, $"AddressWrite is built outside {home}: {string.Join(", ", offenders)}");
        Assert.Matches(new Regex(@"\bnew\s*\(\s*AddressWriteKind\."), File.ReadAllText(Path.Combine(ClientSource.Root, home)));
    }

    [Theory]
    [InlineData("Pages/Calendar/CalendarWrites.cs", @"NewCalendarEvent Event\([^)]*LocationLink link\)")]
    [InlineData("Pages/Calendar/CalendarWrites.cs", @"NewRecurrencePattern Pattern\([\s\S]*?LocationLink link\)")]
    [InlineData("Pages/Journal/JournalEntryDraft.cs", @"NewJournalEntry New\([\s\S]*?LocationLink link\)")]
    [InlineData("Pages/Journal/JournalEntryDraft.cs", @"UpdateJournalEntry Update\([\s\S]*?LocationLink link\)")]
    public void Every_builder_takes_the_link_as_a_required_argument(string file, string signature)
    {
        Assert.Matches(new Regex(signature), File.ReadAllText(Path.Combine(ClientSource.Root, file)));
    }

    [Theory]
    [InlineData("Pages/Calendar/CalendarPage.razor.cs", @"HandleEventDrop[\s\S]*?DropUpdate\(existing")]
    [InlineData("Pages/Calendar/CalendarPage.razor.cs", @"HandleEventChange[\s\S]*?ResizeUpdate\(existing")]
    [InlineData("Pages/Journal/JournalCard.razor.cs", @"RemoveAttachmentAsync[\s\S]*?JournalWrite\.WithoutAttachment\(detail")]
    [InlineData("Pages/Journal/JournalCard.razor.cs", @"ToggleArchive[\s\S]*?JournalWrite\.FromDetail\(detail")]
    [InlineData("Pages/Journal/CreateJournalEntryDialog.razor.cs", @"JournalWrite\.ToUpdate\(_draft, [^;]*link\)")]
    [InlineData("Pages/Calendar/CalendarEventDialog.razor.cs", @"_draft\.ToNewCalendarEvent\(link\)")]
    public void The_existing_write_paths_use_the_builders(string file, string pattern)
    {
        Assert.Matches(new Regex(pattern), File.ReadAllText(Path.Combine(ClientSource.Root, file)));
    }

    [Fact]
    public void The_place_picker_never_requests_the_unpaged_list()
    {
        var code = File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "OdsLocationSelect.razor.cs"));

        Assert.DoesNotContain("ListAllAsync", code);
        Assert.Contains("Locations.ListAsync(1, PageSize", code);
    }

    /// <summary>The location components and the address block render text, never markup (AC 12, SEC N3/F9).</summary>
    [Fact]
    public void No_location_component_renders_a_MarkupString()
    {
        var files = ClientCode()
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.StartsWith("Location", StringComparison.Ordinal)
                       || name.StartsWith("OdsLocation", StringComparison.Ordinal)
                       || name.StartsWith("OdsAddressBlock", StringComparison.Ordinal)
                       || name.StartsWith("OdsComboboxOptionText", StringComparison.Ordinal)
                       || name.StartsWith("AddCoordinatesFirst", StringComparison.Ordinal);
            })
            .ToList();

        Assert.True(files.Count >= 12, $"The lint's scan set shrank to {files.Count} files.");
        var offenders = files.Where(f => File.ReadAllText(f).Contains("MarkupString", StringComparison.Ordinal)).Select(Relative).ToList();
        Assert.True(offenders.Count == 0, $"MarkupString in: {string.Join(", ", offenders)}");
    }
}
