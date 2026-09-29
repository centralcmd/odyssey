using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsFileTypeKind"/> selects the vocabulary of <c>OdsFileTypeSelect</c> /
/// <c>OdsFileTypeMultiSelect</c> (Odyssey Design System · FileTypeSelect <c>FILE_TYPE_REGISTRIES</c>). Both
/// lookups fall through to Account, so a mis-mapped or newly added kind would silently hand a picker the
/// account vocabulary; these pin every kind to its own registry and filter glyph, and every attachment
/// surface to the kind it attaches to.
/// </summary>
public sealed class OdsFileTypeKindTests
{
    public static TheoryData<OdsFileTypeKind, string, string> Kinds => new()
    {
        { OdsFileTypeKind.Account, nameof(OdsTypeRegistries.AccountFileOptions), "folder" },
        { OdsFileTypeKind.Transaction, nameof(OdsTypeRegistries.TransactionFileOptions), "receipt_long" },
        { OdsFileTypeKind.TaxStatement, nameof(OdsTypeRegistries.TaxStatementFileOptions), "request_quote" },
        { OdsFileTypeKind.Property, nameof(OdsTypeRegistries.PropertyFileOptions), "home_work" },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Each_kind_maps_to_its_own_registry_and_filter_glyph(OdsFileTypeKind kind, string optionsField, string icon)
    {
        Assert.Same(OptionsField(optionsField), OdsTypeRegistries.FileOptionsFor(kind));
        Assert.Equal(icon, OdsTypeRegistries.FileTypeFilterIcon(kind));
    }

    [Fact]
    public void Every_kind_is_mapped_and_no_two_share_a_vocabulary()
    {
        var kinds = Enum.GetValues<OdsFileTypeKind>();
        Assert.Equal(kinds.Length, Kinds.Count());

        var vocabularies = kinds.Select(OdsTypeRegistries.FileOptionsFor).ToList();
        Assert.Equal(vocabularies.Count, vocabularies.Distinct(ReferenceEqualityComparer.Instance).Count());

        var glyphs = kinds.Select(OdsTypeRegistries.FileTypeFilterIcon).ToList();
        Assert.Equal(glyphs.Count, glyphs.Distinct().Count());
    }

    [Fact]
    public void The_filter_glyphs_agree_with_the_design_system()
    {
        var path = ClientSource.Sibling(Path.Combine("Odyssey Design System", "components", "FileTypeSelect.jsx"));
        var declared = Regex.Matches(
                File.ReadAllText(path),
                @"(?<kind>\w+):\s*\{\s*types:\s*\w+,\s*filterIcon:\s*'(?<icon>[^']+)'\s*\}")
            .ToDictionary(m => m.Groups["kind"].Value, m => m.Groups["icon"].Value);

        Assert.Equal(Enum.GetValues<OdsFileTypeKind>().Length, declared.Count);
        foreach (var kind in Enum.GetValues<OdsFileTypeKind>())
        {
            var dsKey = char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];
            Assert.Equal(declared[dsKey], OdsTypeRegistries.FileTypeFilterIcon(kind));
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task The_select_renders_the_vocabulary_of_its_kind(OdsFileTypeKind kind, string optionsField, string _)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsFileTypeSelect>(p => p.Add(s => s.Kind, kind));

        Assert.Same(OptionsField(optionsField), cut.FindComponent<OdsSelect>().Instance.Options);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task The_filter_renders_the_vocabulary_and_glyph_of_its_kind(OdsFileTypeKind kind, string optionsField, string icon)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsFileTypeMultiSelect>(p => p.Add(s => s.Kind, kind));

        var multi = cut.FindComponent<OdsMultiSelect>().Instance;
        Assert.Same(OptionsField(optionsField), multi.Options);
        Assert.Equal(icon, multi.Icon);
    }

    [Theory]
    [InlineData("AccountFilesSection.razor", "Account")]
    [InlineData("TransactionFilesSection.razor", "Transaction")]
    [InlineData("TaxStatementFilesSection.razor", "TaxStatement")]
    public void Each_attachment_surface_passes_the_kind_it_attaches_to(string file, string kind)
    {
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", file));
        var pickers = Regex.Matches(markup, @"<OdsFileType(?:Multi)?Select\b[^>]*>", RegexOptions.Singleline);

        Assert.NotEmpty(pickers);
        Assert.All(pickers, m => Assert.Contains($"Kind=\"OdsFileTypeKind.{kind}\"", m.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// The shared "Attach documents" dialog takes its vocabulary as a registry rather than a kind, so
    /// each host is pinned to the registry of the record it attaches to — a property host handing it the
    /// account vocabulary would compile, render and post a type the server reads as a different member.
    /// </summary>
    [Theory]
    [InlineData("AccountsCard.razor", "AccountFileTypes")]
    [InlineData("TransactionAttachDialog.razor", "TransactionFileTypes")]
    [InlineData("TaxStatementsCard.razor", "TaxStatementFileTypes")]
    [InlineData("PropertyDocumentsSection.razor", "PropertyFileTypes")]
    [InlineData("ContractsCard.razor", "ContractFileTypes")]
    public void Each_attach_documents_host_passes_the_vocabulary_it_attaches_to(string file, string registry)
    {
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", file));
        var dialogs = Regex.Matches(markup, @"<AttachDocumentsDialog\b.*?/>", RegexOptions.Singleline);

        Assert.Single(dialogs);
        Assert.Contains($"Kinds=\"OdsTypeRegistries.{registry}\"", dialogs[0].Value, StringComparison.Ordinal);
    }

    private static IReadOnlyList<OdsOption> OptionsField(string name) =>
        (IReadOnlyList<OdsOption>)typeof(OdsTypeRegistries).GetField(name)!.GetValue(null)!;

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Render<MudPopoverProvider>();
        return ctx;
    }
}
