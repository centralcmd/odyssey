using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsHomeownerAssociationSelect (issue #217; design system · HomeownerAssociationSelect): only active
/// Organizations are offered, a kept archived or re-typed link reads as a stated legacy notice rather
/// than an error (§8.3), and a changed id gets no such notice.
/// </summary>
public class OdsHomeownerAssociationSelectTests
{
    private static readonly ExistingContact Active = Contact("Storgata Borettslag", ContactType.Organization);
    private static readonly ExistingContact Archived = Contact("Gamle Sameie", ContactType.Organization, archived: true);
    private static readonly ExistingContact Person = Contact("Kari Nordmann", ContactType.Person);

    private static ExistingContact Contact(string name, ContactType type, bool archived = false) => new()
    {
        ContactId = Guid.NewGuid(),
        ResolvedDisplayName = name,
        NormalizedName = name.ToUpperInvariant(),
        ExternalUid = string.Empty,
        Type = type,
        Archived = archived ? new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) : null,
    };

    private sealed class Host : ComponentBase
    {
        [Parameter] public string? Value { get; set; }
        [Parameter] public string? StoredValue { get; set; }
        [Parameter] public string? Error { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsHomeownerAssociationSelect>(1);
            builder.AddComponentParameter(2, nameof(OdsHomeownerAssociationSelect.Contacts),
                (IReadOnlyList<ExistingContact>)[Active, Archived, Person]);
            builder.AddComponentParameter(3, nameof(OdsHomeownerAssociationSelect.Value), Value);
            builder.AddComponentParameter(4, nameof(OdsHomeownerAssociationSelect.StoredValue), StoredValue);
            builder.AddComponentParameter(5, nameof(OdsHomeownerAssociationSelect.Error), Error);
            builder.CloseComponent();
        }
    }

    private static IRenderedComponent<Host> Render(string? value = null, string? stored = null, string? error = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx.Render<Host>(p => p
            .Add(h => h.Value, value)
            .Add(h => h.StoredValue, stored)
            .Add(h => h.Error, error));
    }

    private static IReadOnlyList<string> OptionValues(IRenderedComponent<Host> cut) =>
        [.. cut.FindComponent<OdsCombobox>().Instance.Options.Select(o => o.Value)];

    [Fact]
    public void Offers_only_active_organizations()
    {
        var cut = Render();

        Assert.Equal([Active.ContactId.ToString()], OptionValues(cut));
        Assert.Contains("The borettslag, sameie or HOA that administers it.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kept_archived_link_stays_selectable_and_reads_as_a_legacy_notice()
    {
        var id = Archived.ContactId.ToString();
        var cut = Render(value: id, stored: id);

        Assert.Contains(id, OptionValues(cut));
        var status = cut.Find(".odc-field-help[role=status]");
        Assert.Contains("warn", status.ClassList);
        Assert.Contains("Gamle Sameie is archived. Saving keeps this link", status.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.Find(".odc-field-help[role=alert]").TextContent);
    }

    [Fact]
    public void A_kept_link_retyped_to_a_person_reads_as_a_legacy_notice()
    {
        var id = Person.ContactId.ToString();
        var cut = Render(value: id, stored: id);

        Assert.Contains("Kari Nordmann is no longer an organization.", cut.Find(".odc-field-help[role=status]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_id_gets_no_legacy_notice()
    {
        var cut = Render(value: Archived.ContactId.ToString(), stored: Active.ContactId.ToString());

        Assert.DoesNotContain("warn", cut.Find(".odc-field-help[role=status]").ClassList);
        Assert.DoesNotContain("Saving keeps this link", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_goes_to_the_alert_region_and_silences_the_status()
    {
        var cut = Render(error: "That organization is archived. Restore it in Contacts or pick another.");

        Assert.Contains("That organization is archived.", cut.Find(".odc-field-help[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.Find(".odc-field-help[role=status]").TextContent.Trim());
        Assert.Contains("error", cut.Find(".odc-hoa-field").ClassList);
    }
}
