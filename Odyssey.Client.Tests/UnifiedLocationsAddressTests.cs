using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.UnifiedLocationsTestKit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The shared address block (issue #383 §3.3, AC 4–6, 19): the state table, the apply-to rule, the coordinates
/// warning, the property tile, and what each state sends — then the same rules through the contact and property
/// dialogs, including the status-chosen 403 copy (§5.3) and the 409 <c>applyTo</c> re-ask.
/// </summary>
public class UnifiedLocationsAddressTests
{
    private static AddressFields Fields(string line1 = "Storgata 1", string? line2 = null, string? region = null) => new()
    {
        Line1 = line1, Line2 = line2, PostalCode = "0155", City = "Oslo", Region = region, CountryCode = "NO",
    };

    private static AddressBlockStored Stored(int otherUses = 2, bool coordinates = false, AddressFields? fields = null) => new()
    {
        LocationId = StorgataId,
        Fields = fields ?? Fields(),
        OtherUses = otherUses,
        PlaceHasCoordinates = coordinates,
    };

    private static AddressBlockModel Model(
        AddressBlockStored? stored = null, AddressBlockVariant variant = AddressBlockVariant.Contact, AddressBlockPermissions? permissions = null) =>
        new(variant, stored, permissions ?? AddressBlockPermissions.Full);

    // ── State table (AC 4) ─────────────────────────────────────────────────────

    [Fact]
    public void A_new_typed_address_sends_its_fields()
    {
        var model = Model();
        model.Edit(AddressBlockModel.Line1Key, " Storgata  1 ");
        model.Edit(AddressBlockModel.CityKey, "Oslo");
        model.Edit(AddressBlockModel.CountryCodeKey, "no");

        var write = model.ToWrite();

        Assert.Equal(AddressBlockState.Typed, model.State);
        Assert.Equal(AddressWriteKind.Typed, write.Kind);
        Assert.Null(write.LocationId);
        Assert.Equal(("Storgata 1", "Oslo", "NO"), (write.Fields!.Line1, write.Fields.City, write.Fields.CountryCode));
        Assert.Equal("Addresses are shared places. Saving uses the existing place at this address, or adds a new one.", model.PickerHelp);
    }

    [Fact]
    public void Picking_fills_the_fields_and_sends_the_id()
    {
        var model = Model();

        var said = model.Pick(Hytta());

        Assert.Equal("Address filled from Hytta", said);
        Assert.Equal(AddressBlockState.Picked, model.State);
        Assert.Equal("Fjellveien 12", model.Fields.Line1);
        Assert.Equal(new AddressWrite(AddressWriteKind.LinkById, HyttaId, null, null, null), model.ToWrite());
    }

    [Fact]
    public void Editing_a_picked_field_clears_the_pick_and_says_so()
    {
        var model = Model();
        model.Pick(Hytta());

        var said = model.Edit(AddressBlockModel.Line1Key, "Fjellveien 14");

        Assert.Equal("Place cleared — saving uses the address you type", said);
        Assert.Equal(AddressBlockState.Typed, model.State);
        Assert.Null(model.ToWrite().LocationId);
        Assert.Equal("Fjellveien 14", model.ToWrite().Fields!.Line1);
    }

    [Fact]
    public void Clearing_the_picker_keeps_the_filled_values()
    {
        var model = Model();
        model.Pick(Hytta());

        model.ClearPick();

        Assert.Equal(AddressBlockState.Typed, model.State);
        Assert.Equal("Fjellveien 12", model.Fields.Line1);
    }

    [Fact]
    public void An_existing_address_unchanged_sends_the_stored_id_and_picking_a_different_place_sends_the_new_one()
    {
        var model = Model(Stored());
        Assert.Equal(AddressBlockState.Unchanged, model.State);
        Assert.Equal(StorgataId, model.ToWrite().LocationId);
        Assert.Equal(AddressWriteKind.None, model.ToWrite().Kind);

        model.Pick(Hytta());

        Assert.Equal(AddressBlockState.Picked, model.State);
        Assert.Equal(HyttaId, model.ToWrite().LocationId);
    }

    [Fact]
    public void Clearing_every_property_field_unlinks_and_says_the_place_stays()
    {
        var model = Model(Stored(), AddressBlockVariant.Property);
        foreach (var key in new[] { AddressBlockModel.Line1Key, AddressBlockModel.PostalCodeKey, AddressBlockModel.CityKey, AddressBlockModel.CountryCodeKey })
            model.Edit(key, "");
        // A property's Line 2 / Region are the place's, not inputs: they are emptied with the address.
        model.Fields.Line2 = null;
        model.Fields.Region = null;

        Assert.Equal(AddressBlockState.Cleared, model.State);
        Assert.Equal(new AddressWrite(AddressWriteKind.None, null, null, null, null), model.ToWrite());
        Assert.Equal("The address stays as a saved place on the Locations page.", AddressBlockModel.ClearedHelp);
    }

    [Fact]
    public void Changed_compares_cleaned_values_ordinally()
    {
        var model = Model(Stored());

        model.Edit(AddressBlockModel.Line1Key, "  Storgata   1 ");
        Assert.Equal(AddressBlockState.Unchanged, model.State);

        model.Edit(AddressBlockModel.Line1Key, "Storgata 2");
        Assert.Equal(AddressBlockState.Changed, model.State);
    }

    [Fact]
    public void A_case_only_change_is_sent_without_asking_as_all_uses()
    {
        var model = Model(Stored(otherUses: 3));

        model.Edit(AddressBlockModel.Line1Key, "STORGATA 1");

        Assert.Equal(AddressBlockState.Changed, model.State);
        Assert.True(model.CaseOnly);
        Assert.False(model.AsksApplyTo);
        var write = model.ToWrite();
        Assert.Equal(AddressApplyTo.AllUses, write.ApplyTo);
        Assert.Equal(3, write.ExpectedOtherUses);
        Assert.Equal("You can’t change this address for everyone. Choose “Only this contact”.",
            AddressBlockModel.ForbiddenMessage(write.Kind, model.Noun));
    }

    // ── Apply-to rule (AC 5, SEC F1/N1) ────────────────────────────────────────

    [Fact]
    public void The_question_is_skipped_only_when_nothing_else_uses_it_and_every_kind_is_readable()
    {
        var alone = Model(Stored(otherUses: 0));
        alone.Edit(AddressBlockModel.Line1Key, "Storgata 2");
        Assert.False(alone.AsksApplyTo);
        var write = alone.ToWrite();
        Assert.Equal((AddressWriteKind.AllUses, (AddressApplyTo?)AddressApplyTo.AllUses, (int?)0), (write.Kind, write.ApplyTo, write.ExpectedOtherUses));

        var hidden = Model(Stored(otherUses: 0), permissions: new AddressBlockPermissions(true, true, AllKindsReadable: false));
        hidden.Edit(AddressBlockModel.Line1Key, "Storgata 2");
        Assert.True(hidden.AsksApplyTo);

        var shared = Model(Stored(otherUses: 2));
        shared.Edit(AddressBlockModel.Line1Key, "Storgata 2");
        Assert.True(shared.AsksApplyTo);
    }

    [Fact]
    public void Without_locations_update_all_uses_is_never_offered_and_the_save_sends_this_record()
    {
        var model = Model(Stored(otherUses: 2), permissions: new AddressBlockPermissions(true, false, true));
        model.Edit(AddressBlockModel.Line1Key, "Storgata 2");

        Assert.False(model.AsksApplyTo);
        Assert.Equal(AddressApplyTo.ThisRecord, model.ToWrite().ApplyTo);
    }

    [Fact]
    public void The_group_has_no_default_requires_a_choice_and_sends_it_with_the_count_shown()
    {
        var model = Model(Stored(otherUses: 3));
        model.Edit(AddressBlockModel.Line1Key, "Storgata 2");

        Assert.Null(model.ApplyTo);
        Assert.Equal("Choose who this change applies to", model.Validate()[AddressBlockModel.ApplyToKey]);
        Assert.Equal(["All 4 uses of this address", "Only this contact"], model.ApplyToOptions.Select(o => o.Label));
        Assert.All(model.ApplyToOptions, o => Assert.False(string.IsNullOrEmpty(o.Description)));

        model.ApplyTo = AddressBlockModel.ThisRecord;

        Assert.Empty(model.Validate());
        Assert.Equal((AddressApplyTo?)AddressApplyTo.ThisRecord, model.ToWrite().ApplyTo);
        Assert.Equal(3, model.ToWrite().ExpectedOtherUses);
    }

    [Fact]
    public void Without_every_read_claim_the_all_uses_option_names_no_count()
    {
        var model = Model(Stored(otherUses: 3), permissions: new AddressBlockPermissions(true, true, false));
        model.Edit(AddressBlockModel.Line1Key, "Storgata 2");

        Assert.Equal("All uses of this address", model.ApplyToOptions[0].Label);
    }

    // ── Coordinates warning (AC 6, decision 8) ─────────────────────────────────

    [Fact]
    public void The_warning_shows_only_when_the_save_sends_all_uses_to_a_geocoded_place()
    {
        var skipped = Model(Stored(otherUses: 0, coordinates: true));
        skipped.Edit(AddressBlockModel.Line1Key, "Storgata 2");
        Assert.True(skipped.ShowsCoordinatesWarning);

        var asked = Model(Stored(otherUses: 2, coordinates: true));
        asked.Edit(AddressBlockModel.Line1Key, "Storgata 2");
        Assert.False(asked.ShowsCoordinatesWarning);
        asked.ApplyTo = AddressBlockModel.AllUses;
        Assert.True(asked.ShowsCoordinatesWarning);
        asked.ApplyTo = AddressBlockModel.ThisRecord;
        Assert.False(asked.ShowsCoordinatesWarning);

        var ungeocoded = Model(Stored(otherUses: 0, coordinates: false));
        ungeocoded.Edit(AddressBlockModel.Line1Key, "Storgata 2");
        Assert.False(ungeocoded.ShowsCoordinatesWarning);
    }

    // ── The property tile (AC 19, FE c) ────────────────────────────────────────

    [Fact]
    public void Also_on_the_saved_place_shows_in_picked_unchanged_and_all_uses_changed_only()
    {
        var stored = Stored(otherUses: 2, fields: Fields(line2: "Etg. 4", region: "Oslo"));
        var model = Model(stored, AddressBlockVariant.Property);
        Assert.Equal("Etg. 4 · Oslo", model.AlsoOnSavedPlace);

        model.Edit(AddressBlockModel.Line1Key, "Storgata 2");
        Assert.Null(model.AlsoOnSavedPlace);
        model.ApplyTo = AddressBlockModel.AllUses;
        Assert.Equal("Etg. 4 · Oslo", model.AlsoOnSavedPlace);
        model.ApplyTo = AddressBlockModel.ThisRecord;
        Assert.Null(model.AlsoOnSavedPlace);

        var typed = Model(null, AddressBlockVariant.Property);
        typed.Edit(AddressBlockModel.Line1Key, "Storgata 2");
        Assert.Null(typed.AlsoOnSavedPlace);

        var picked = Model(null, AddressBlockVariant.Property);
        picked.Pick(Storgata());
        Assert.Equal("Etg. 4 · Oslo", picked.AlsoOnSavedPlace);
    }

    [Fact]
    public void A_property_never_sends_line_two_or_region()
    {
        var model = Model(null, AddressBlockVariant.Property);
        model.Pick(Storgata());
        model.Edit(AddressBlockModel.Line1Key, "Storgata 3");

        var fields = model.ToWrite().Fields!;

        Assert.Null(fields.Line2);
        Assert.Null(fields.Region);
    }

    // ── Usage line (§3.3) ──────────────────────────────────────────────────────

    [Fact]
    public void The_usage_line_follows_the_state_and_disappears_without_locations_read()
    {
        Assert.Equal("Shared with 2 other records", Model(Stored()).UsageLine);
        Assert.Null(Model(Stored(), permissions: new AddressBlockPermissions(false, false, true)).UsageLine);

        var picked = Model();
        picked.Pick(Storgata());
        Assert.Equal("Shared with 2 other records — 1 contact, 1 property", picked.UsageLine);
    }

    // ── 403 copy (§5.3) and the 409/422 messages (§7) ──────────────────────────

    [Theory]
    [InlineData(AddressWriteKind.LinkById, "You don’t have permission to choose a saved place.")]
    [InlineData(AddressWriteKind.Typed, "Adding a new address needs permission to create locations.")]
    [InlineData(AddressWriteKind.ThisRecord, "Changing this address for only this property needs permission to create locations.")]
    [InlineData(AddressWriteKind.AllUses, "You can’t change this address for everyone. Choose “Only this property”.")]
    public void A_403_is_explained_by_the_request_that_was_sent(AddressWriteKind kind, string message)
    {
        Assert.Equal(message, AddressBlockModel.ForbiddenMessage(kind, "property"));
    }

    [Fact]
    public void A_duplicate_address_mentions_merging_only_to_someone_who_can()
    {
        Assert.Contains("Merge them on the Locations page", AddressBlockModel.DuplicateAddressMessage("contact", canUpdateLocations: true));
        Assert.DoesNotContain("Merge", AddressBlockModel.DuplicateAddressMessage("contact", canUpdateLocations: false));
    }

    // ── The block rendered (AC 5, 6) ───────────────────────────────────────────

    private static (BunitContext Ctx, IRenderedComponent<OdsAddressBlock> Cut, List<string> Said) RenderBlock(AddressBlockModel model)
    {
        var ctx = Context();
        ctx.Services.AddSingleton(Locations(Hytta(), Storgata()).Object);
        var said = new List<string>();
        var cut = ctx.Render<OdsAddressBlock>(p => p
            .Add(b => b.Model, model)
            .Add(b => b.ApplyGroupId, "apply")
            .Add(b => b.PickerDebounceMs, 0)
            .Add(b => b.OnAnnounce, EventCallback.Factory.Create<string>(new object(), m => said.Add(m))));
        return (ctx, cut, said);
    }

    private static Task Edit(IRenderedComponent<OdsAddressBlock> cut, string label, string value) =>
        cut.InvokeAsync(() => cut.FindComponents<OdsField>().Single(f => f.Instance.Label == label).Instance.ValueChanged.InvokeAsync(value));

    [Fact]
    public async Task The_group_appears_after_a_change_is_announced_once_and_nothing_is_preselected()
    {
        var (ctx, cut, said) = RenderBlock(Model(Stored(otherUses: 2)));
        await using var _ = ctx;
        Assert.Empty(cut.FindAll("fieldset.odc-radiofield"));

        await Edit(cut, "Line 1", "Storgata 2");
        await Edit(cut, "Line 1", "Storgata 22");

        Assert.Equal("Apply this change to", cut.Find("fieldset#apply > legend").FirstChild!.TextContent.Trim());
        Assert.Equal("true", cut.Find("[role=radiogroup]").GetAttribute("aria-required"));
        Assert.Empty(cut.FindAll("input[type=radio][checked]"));
        Assert.Single(said, m => m.Contains("This address is shared — choose who the change applies to", StringComparison.Ordinal));

        // Back to unchanged: the group goes, silently.
        await Edit(cut, "Line 1", "Storgata 1");
        Assert.Empty(cut.FindAll("fieldset.odc-radiofield"));
        Assert.Single(said, m => m.Contains("This address is shared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_group_is_skipped_the_warning_is_announced_once_and_describes_the_changed_field()
    {
        var (ctx, cut, said) = RenderBlock(Model(Stored(otherUses: 0, coordinates: true)));
        await using var _ = ctx;

        await Edit(cut, "City", "Bergen");
        await Edit(cut, "City", "Bergen sentrum");

        var warning = cut.Find(".odc-addrblock-warn");
        Assert.Contains("This place has map coordinates for the old address", warning.TextContent);
        Assert.Single(said, m => m.Contains("map coordinates for the old address", StringComparison.Ordinal));
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsAddDescribedBy"
            && ((string)i.Arguments[0]!).EndsWith("-city", StringComparison.Ordinal) && Equals(i.Arguments[1], warning.Id));
    }

    [Fact]
    public async Task Choosing_all_uses_shows_the_warning_and_joins_it_to_the_groups_description()
    {
        var model = Model(Stored(otherUses: 2, coordinates: true));
        var (ctx, cut, said) = RenderBlock(model);
        await using var _ = ctx;

        await Edit(cut, "Line 1", "Storgata 2");
        Assert.Empty(cut.FindAll(".odc-addrblock-warn"));
        cut.FindAll("input[type=radio]")[0].Change(true);

        var warning = cut.Find(".odc-addrblock-warn");
        Assert.Contains(warning.Id, cut.Find("[role=radiogroup]").GetAttribute("aria-describedby")!.Split(' '));
        Assert.Single(said, m => m.Contains("map coordinates for the old address", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Picking_a_place_fills_the_fields_and_is_announced()
    {
        var (ctx, cut, said) = RenderBlock(Model());
        await using var _ = ctx;

        await cut.InvokeAsync(() => cut.FindComponent<OdsLocationSelect>().Instance.ValueChanged.InvokeAsync(LocationPick.Existing(HyttaId)));

        Assert.Equal("Fjellveien 12", cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Line 1").Instance.Value);
        Assert.Contains("Address filled from Hytta", said);
        Assert.Equal(LocationPickFilter.AddressComplete, cut.FindComponent<OdsLocationSelect>().Instance.Filter);
        Assert.False(cut.FindComponent<OdsLocationSelect>().Instance.AllowCreate);
    }

    [Fact]
    public async Task Without_locations_read_there_is_no_picker_and_no_usage_line()
    {
        var (ctx, cut, _) = RenderBlock(Model(Stored(), permissions: new AddressBlockPermissions(false, false, false)));
        await using var _ = ctx;

        Assert.Empty(cut.FindComponents<OdsLocationSelect>());
        Assert.Empty(cut.FindAll(".odc-addrblock-usage"));
        Assert.NotEmpty(cut.FindComponents<OdsField>());
    }

    // ── Through the contact dialog (AC 4, 5) ───────────────────────────────────

    private static ExistingAddress Address(int otherUses = 2, bool coordinates = false) => new()
    {
        Id = Guid.NewGuid(),
        ContactId = Guid.NewGuid(),
        Label = AddressLabel.Home,
        IsPrimary = true,
        LocationId = StorgataId,
        Line1 = "Storgata 1",
        PostalCode = "0155",
        City = "Oslo",
        CountryCode = "NO",
        OtherUses = otherUses,
        PlaceHasCoordinates = coordinates,
    };

    private sealed record ContactHarness(
        BunitContext Ctx, IRenderedComponent<ProvidersHost> Cut, Mock<IContactsApiClient> Contacts, List<NewAddress> Sent);

    private static ContactHarness RenderContactDialog(
        ExistingAddress? stored, Func<NewAddress, ApiResult>? reply = null, ExistingAddress? reloaded = null)
    {
        var ctx = Context();
        var sent = new List<NewAddress>();
        var contacts = new Mock<IContactsApiClient>();
        contacts.Setup(c => c.UpdateAddressAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NewAddress>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid _, NewAddress body, CancellationToken _) => { sent.Add(body); return reply?.Invoke(body) ?? ApiResult.Success(HttpStatusCode.OK); });
        contacts.Setup(c => c.AddAddressAsync(It.IsAny<Guid>(), It.IsAny<NewAddress>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, NewAddress body, CancellationToken _) => { sent.Add(body); return reply?.Invoke(body) ?? ApiResult.Success(HttpStatusCode.Created); });
        contacts.Setup(c => c.ListAddressesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingAddress>>.Success(reloaded is null ? [] : [reloaded], HttpStatusCode.OK));
        ctx.Services.AddSingleton(contacts.Object);
        ctx.Services.AddSingleton(Locations(Hytta(), Storgata()).Object);
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());

        var draft = stored is null
            ? new ContactMethodDraft { Label = nameof(AddressLabel.Home), IsPrimary = true }
            : ContactMethodDraft.FromAddress(stored);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<ContactMethodDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Kind, "address")
            .Add(x => x.Draft, draft)
            .Add(x => x.IsEdit, stored is not null)
            .Add(x => x.ContactType, ContactType.Person)
            .Add(x => x.ContactId, Guid.NewGuid())
            .Add(x => x.Permissions, AddressBlockPermissions.Full)));
        return new(ctx, cut, contacts, sent);
    }

    private static Task EditField(IRenderedComponent<ProvidersHost> cut, string label, string value) =>
        cut.InvokeAsync(() => cut.FindComponents<OdsField>().Single(f => f.Instance.Label == label).Instance.ValueChanged.InvokeAsync(value));

    private static void Submit(IRenderedComponent<ProvidersHost> cut, string text) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    [Fact]
    public async Task Saving_a_shared_change_without_a_choice_shows_the_summary_which_takes_focus()
    {
        var h = RenderContactDialog(Address(otherUses: 2));
        await using var _ = h.Ctx;

        await EditField(h.Cut, "Line 1", "Storgata 2");
        Submit(h.Cut, "Save changes");

        var summary = h.Cut.WaitForElement(".loc-err-summary");
        Assert.Equal("alert", summary.GetAttribute("role"));
        Assert.Equal("-1", summary.GetAttribute("tabindex"));
        Assert.Contains(h.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && Equals(i.Arguments[0], summary.Id));
        Assert.Equal("true", h.Cut.Find("[role=radiogroup]").GetAttribute("aria-invalid"));
        Assert.Empty(h.Sent);

        // The summary's link focuses the group's first radio.
        var link = summary.QuerySelector("a")!;
        Assert.Equal("Choose who this change applies to", link.TextContent);
        await h.Cut.InvokeAsync(() => link.Click());
        var groupId = h.Cut.Find("fieldset.odc-radiofield").Id;
        Assert.Contains(h.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusFirstIn" && Equals(i.Arguments[0], groupId));
    }

    [Fact]
    public async Task The_choice_is_sent_with_the_count_that_was_shown()
    {
        var h = RenderContactDialog(Address(otherUses: 2));
        await using var _ = h.Ctx;

        await EditField(h.Cut, "Line 1", "Storgata 2");
        h.Cut.FindAll("input[type=radio]")[1].Change(true);
        Submit(h.Cut, "Save changes");

        h.Cut.WaitForAssertion(() => Assert.Single(h.Sent));
        Assert.Equal(AddressApplyTo.ThisRecord, h.Sent[0].ApplyTo);
        Assert.Equal(2, h.Sent[0].ExpectedOtherUses);
        Assert.Equal("Storgata 2", h.Sent[0].Line1);
        Assert.Null(h.Sent[0].LocationId);
    }

    [Fact]
    public async Task A_409_on_apply_to_reloads_the_usage_and_asks_again()
    {
        var stored = Address(otherUses: 2);
        var h = RenderContactDialog(stored,
            reply: _ => ApiResult.Failure(HttpStatusCode.Conflict, Problem(409, "applyTo", "The uses changed.")),
            reloaded: stored with { OtherUses = 5 });
        await using var _ = h.Ctx;

        await EditField(h.Cut, "Line 1", "Storgata 2");
        h.Cut.FindAll("input[type=radio]")[0].Change(true);
        Submit(h.Cut, "Save changes");

        h.Cut.WaitForAssertion(() => Assert.Contains("Someone changed who uses this address. Check the count and choose again.", h.Cut.Markup));
        Assert.Contains("All 6 uses of this address", h.Cut.Markup);
        Assert.Empty(h.Cut.FindAll("input[type=radio][checked]"));
        Assert.Contains(h.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusFirstIn");
    }

    [Fact]
    public async Task A_403_on_a_case_only_change_says_it_cannot_change_the_address_for_everyone()
    {
        var h = RenderContactDialog(Address(otherUses: 2),
            reply: _ => ApiResult.Failure(HttpStatusCode.Forbidden, Problem(403)));
        await using var _ = h.Ctx;

        await EditField(h.Cut, "Line 1", "STORGATA 1");
        Submit(h.Cut, "Save changes");

        h.Cut.WaitForAssertion(() => Assert.Contains("You can’t change this address for everyone. Choose “Only this contact”.", h.Cut.Markup));
        Assert.Equal(AddressApplyTo.AllUses, h.Sent.Single().ApplyTo);
        Assert.Equal(2, h.Sent.Single().ExpectedOtherUses);
        Assert.Empty(h.Cut.FindAll("fieldset.odc-radiofield"));
    }

    [Fact]
    public async Task Picking_a_different_place_sends_its_id()
    {
        var h = RenderContactDialog(Address());
        await using var _ = h.Ctx;

        await h.Cut.InvokeAsync(() => h.Cut.FindComponent<OdsLocationSelect>().Instance.ValueChanged.InvokeAsync(LocationPick.Existing(HyttaId)));
        Submit(h.Cut, "Save changes");

        h.Cut.WaitForAssertion(() => Assert.Equal(HyttaId, h.Sent.Single().LocationId));
        Assert.Null(h.Sent.Single().Line1);
    }

    [Fact]
    public async Task A_409_on_line_one_explains_the_duplicate()
    {
        var h = RenderContactDialog(null,
            reply: _ => ApiResult.Failure(HttpStatusCode.Conflict, Problem(409, "line1", "Exists.")));
        await using var _ = h.Ctx;

        await EditField(h.Cut, "Line 1", "Storgata 1");
        await EditField(h.Cut, "City", "Oslo");
        await EditField(h.Cut, "Country", "no");
        Submit(h.Cut, "Create address");

        h.Cut.WaitForAssertion(() => Assert.Contains("Another saved place already has this address.", h.Cut.Markup));
        Assert.Equal("NO", h.Sent.Single().CountryCode);
    }

    // ── Through the property dialog (AC 4) ─────────────────────────────────────

    [Fact]
    public async Task Clearing_every_property_field_saves_an_unlinked_property()
    {
        var ctx = Context(Odyssey.Dtos.Authorization.PermissionClaims.LocationsRead, Odyssey.Dtos.Authorization.PermissionClaims.LocationsUpdate);
        await using var _ = ctx;
        var properties = new Mock<IPropertiesApiClient>();
        NewProperty? sent = null;
        properties.Setup(p => p.UpdateAsync(It.IsAny<Guid>(), It.IsAny<NewProperty>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, NewProperty body, CancellationToken _) => { sent = body; return ApiResult.Success(HttpStatusCode.OK); });
        ctx.Services.AddSingleton(properties.Object);
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton(Locations(Storgata()).Object);
        var preferences = new Mock<Odyssey.Client.Theme.IUserPreferenceService>();
        ctx.Services.AddSingleton(preferences.Object);
        var property = new ExistingProperty
        {
            PropertyId = Guid.NewGuid(), Name = "Flat", Description = "Home", Type = PropertyType.RealEstate, CurrencyCode = "NOK",
            RealEstateDetails = new RealEstateDetailsDto
            {
                Kind = RealEstateKind.Apartment, LocationId = StorgataId, AddressLine = "Storgata 1", PostalCode = "0155", City = "Oslo",
                CountryCode = "NO", OtherUses = 1, PlaceHasCoordinates = false,
            },
        };
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<PropertyDialog>(d => d.Add(x => x.Open, true).Add(x => x.Property, property)));
        Assert.Contains("Etg. 4 · Oslo", cut.Markup);

        foreach (var label in new[] { "Address line", "Postal code", "City", "Country" })
            await cut.InvokeAsync(() => cut.FindComponents<OdsField>().Single(f => f.Instance.Label == label).Instance.ValueChanged.InvokeAsync(""));
        Assert.Contains("The address stays as a saved place on the Locations page.", cut.Markup);
        Assert.DoesNotContain("Also on the saved place", cut.Markup);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Null(sent!.RealEstateDetails!.LocationId);
        Assert.Null(sent.RealEstateDetails.AddressLine);
        Assert.Null(sent.RealEstateDetails.City);
    }
}
