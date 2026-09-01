using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class CreateInsurancePolicyDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Active contact options (non-archived), shared by the three contact pickers.</summary>
    [Parameter] public IReadOnlyList<OdsOption> Contacts { get; set; } = [];

    /// <summary>Active insured-account options (non-archived accounts).</summary>
    [Parameter] public IReadOnlyList<OdsOption> Accounts { get; set; } = [];

    /// <summary>
    /// The host has not finished loading the option lists. Passed through to all four pickers so an
    /// early open shows the loading row rather than "No contacts match", which would be
    /// indistinguishable from an empty address book. The submit stays enabled throughout — the four
    /// collections are optional, so a slow contact load must never block creating a policy.
    /// </summary>
    [Parameter] public bool OptionsLoading { get; set; }

    /// <summary>Raised after a successful create/update so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>
    /// Raised when a save was rejected because an id named no live record: the host owns the option
    /// cache, so only it can invalidate and reload. Without this the dialog would re-serve the same
    /// stale list it just failed on.
    /// </summary>
    [Parameter] public EventCallback OnStaleOptions { get; set; }

    /// <summary>When set, the dialog edits this policy. Null = create mode.</summary>
    [Parameter] public ExistingInsurancePolicy? InsurancePolicy { get; set; }

    private bool IsEdit => InsurancePolicy is not null;

    private string FieldId(string field) => $"ins-{(IsEdit ? "edit" : "new")}-{field}";

    private string? _name;
    private string? _policyNumber;
    private string _type = string.Empty;
    private string? _notes;

    private IReadOnlyCollection<string> _insurerIds = [];
    private IReadOnlyCollection<string> _insuredAccountIds = [];
    private IReadOnlyCollection<string> _insuredContactIds = [];
    private IReadOnlyCollection<string> _beneficiaryIds = [];

    private string? _nameError;
    private string? _typeError;

    // Server- and client-side field errors, keyed by the write DTO's property name — the same join key
    // ApiProblem.Errors uses, so a rejection maps onto its picker without a translation table.
    private readonly Dictionary<string, string> _fieldErrors = new(StringComparer.OrdinalIgnoreCase);

    private OdsTagMultiSelect? _insurersPicker;
    private OdsTagMultiSelect? _insuredAccountsPicker;
    private OdsTagMultiSelect? _insuredContactsPicker;
    private OdsTagMultiSelect? _beneficiariesPicker;

    // Every contact link the loaded policy carries, by id — the source the chip template reads its
    // availability from. The picker's OdsOption carries none, and should not grow one for a single
    // consumer.
    private readonly Dictionary<string, PolicyContactReference> _contactLinks = new(StringComparer.OrdinalIgnoreCase);

    private const string UnnamedHelp =
        "A member whose contact is archived or no longer resolves keeps its place without a name and "
        + "cannot be removed here — detach the contact's insurance links, or unarchive the contact first.";

    protected override void OnInitialized()
    {
        if (InsurancePolicy is not { } policy)
        {
            return;
        }

        _name = policy.Name;
        _policyNumber = policy.PolicyNumber;
        _type = policy.Type.ToString();
        _notes = policy.Notes;

        // The server orders by resolved display name, so loading in its order is the re-sort: the only
        // reordering a user ever sees is at the moment they reopen a saved policy, never mid-edit.
        _insurerIds = [.. policy.Insurers.Select(i => i.ContactId.ToString())];
        _insuredAccountIds = [.. policy.InsuredAccounts.Select(a => a.AccountId.ToString())];
        _insuredContactIds = [.. policy.InsuredContacts.Select(c => c.ContactId.ToString())];
        _beneficiaryIds = [.. policy.Beneficiaries.Select(b => b.ContactId.ToString())];

        foreach (var link in policy.Insurers.Concat(policy.InsuredContacts).Concat(policy.Beneficiaries))
        {
            _contactLinks[link.ContactId.ToString()] = link;
        }
    }

    private PolicyContactReference? LinkFor(string id) => _contactLinks.GetValueOrDefault(id);

    /// <summary>
    /// True for a member the picker must not offer to remove: its contact is archived or no longer
    /// resolves, so the write path refuses the removal (a 422 naming both routes that do work). A
    /// remove button would silently no-op and the member would reappear on reload.
    /// </summary>
    private bool IsUnnamedContact(string id) =>
        LinkFor(id) is { Availability: not LinkAvailability.Available };

    // The unnamed-member rule is stated where it is LIVE — on a field that actually holds one —
    // rather than three times over as standing noise.
    private string Help(IReadOnlyCollection<string> ids, string help) =>
        ids.Any(IsUnnamedContact) ? $"{help} {UnnamedHelp}" : help;

    private string InsurersHelp => Help(_insurerIds,
        "The contacts that carry this cover — several where it is placed across co-insurers.");

    private string InsuredContactsHelp => Help(_insuredContactIds,
        "The people and organisations insured under this policy — the policyholder, a spouse, named drivers.");

    private string BeneficiariesHelp => Help(_beneficiaryIds,
        "Who receives on this policy. A person, or an organisation such as a trust or an estate.");

    private string? Error(string field) => _fieldErrors.GetValueOrDefault(field);

    private void Set(ref IReadOnlyCollection<string> target, IReadOnlyCollection<string> value, string field)
    {
        target = value;
        _fieldErrors.Remove(field);
    }

    private async Task<bool> SaveAsync()
    {
        _nameError = string.IsNullOrWhiteSpace(_name) ? "Give the policy a name." : null;
        _typeError = string.IsNullOrEmpty(_type) ? "Pick a policy type." : null;
        _fieldErrors.Clear();

        // Parsed once, here. An unparseable id fails the save with a field error rather than being
        // silently dropped: discarding a link the user can see as a chip is the worst available
        // outcome.
        var insurerIds = ParseIds(_insurerIds, nameof(UpdateInsurancePolicy.InsurerIds), "insurers");
        var insuredAccountIds = ParseIds(_insuredAccountIds, nameof(UpdateInsurancePolicy.InsuredAccountIds), "insured accounts");
        var insuredContactIds = ParseIds(_insuredContactIds, nameof(UpdateInsurancePolicy.InsuredContactIds), "insured contacts");
        var beneficiaryIds = ParseIds(_beneficiaryIds, nameof(UpdateInsurancePolicy.BeneficiaryIds), "beneficiaries");

        if (_nameError is not null || _typeError is not null || _fieldErrors.Count > 0)
        {
            await FocusFirstInvalidAsync();
            return false;
        }

        var name = _name!.Trim();
        var policyNumber = string.IsNullOrWhiteSpace(_policyNumber) ? null : _policyNumber!.Trim();
        var type = Enum.TryParse<InsurancePolicyType>(_type, out var t) ? t : InsurancePolicyType.Other;
        var notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes!.Trim();

        ApiClient.ApiResult result;
        if (InsurancePolicy is { } existing)
        {
            // The dialog always sends every collection: it holds the complete desired set for each,
            // so there is nothing for the null-means-unchanged shorthand to protect here.
            result = await Insurance.UpdateAsync(existing.InsurancePolicyId, new UpdateInsurancePolicy
            {
                Name = name,
                PolicyNumber = policyNumber,
                Type = type,
                InsurerIds = insurerIds,
                InsuredAccountIds = insuredAccountIds,
                InsuredContactIds = insuredContactIds,
                BeneficiaryIds = beneficiaryIds,
                Notes = notes,
                // Archive/restore is a separate row action — preserve whatever the record already has.
                Archived = existing.Archived is not null,
            });
        }
        else
        {
            result = await Insurance.CreateAsync(new NewInsurancePolicy
            {
                Name = name,
                PolicyNumber = policyNumber,
                Type = type,
                InsurerIds = insurerIds,
                InsuredAccountIds = insuredAccountIds,
                InsuredContactIds = insuredContactIds,
                BeneficiaryIds = beneficiaryIds,
                Notes = notes,
            });
        }

        var lead = IsEdit ? "Unable to update policy" : "Unable to create policy";
        var success = result.ToastOrFields(Snackbar, lead, AssignFieldError,
            IsEdit ? "Policy updated." : "Policy created.");
        if (success)
        {
            return true;
        }

        // A 400 means an id named no live record: the options the user chose from are stale, and only
        // the host can invalidate the reference cache behind them.
        if (result.Status == System.Net.HttpStatusCode.BadRequest && _fieldErrors.Count > 0)
        {
            await OnStaleOptions.InvokeAsync();
        }

        await FocusFirstInvalidAsync();
        return false;
    }

    private bool AssignFieldError(string field, string message)
    {
        if (!KnownFields.Contains(field))
        {
            return false;
        }

        _fieldErrors[field] = message;
        return true;
    }

    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(UpdateInsurancePolicy.InsurerIds),
        nameof(UpdateInsurancePolicy.InsuredAccountIds),
        nameof(UpdateInsurancePolicy.InsuredContactIds),
        nameof(UpdateInsurancePolicy.BeneficiaryIds),
    };

    private List<Guid> ParseIds(IReadOnlyCollection<string> values, string field, string noun)
    {
        var parsed = new List<Guid>(values.Count);
        foreach (var value in values)
        {
            if (Guid.TryParse(value, out var id))
            {
                parsed.Add(id);
            }
            else
            {
                _fieldErrors[field] = $"One of the {noun} could not be read. Remove it and add it again.";
                return parsed;
            }
        }

        // The compile-time ceiling is a shared constant in Odyssey.Dtos, which the WASM client already
        // references — so guarding against it locally holds no copy of anything mutable. The EFFECTIVE
        // cap is a server setting and is deliberately NOT copied here; an over-cap collection is
        // learned from the save's 422, with the cap interpolated into the message.
        if (parsed.Count > InsuranceLinkLimits.MaxLinksPerPolicy)
        {
            _fieldErrors[field] = $"A policy takes at most {InsuranceLinkLimits.MaxLinksPerPolicy} {noun}.";
        }

        return parsed;
    }

    /// <summary>Moves focus to the first picker carrying an error, in the order the fields render.</summary>
    private async Task FocusFirstInvalidAsync()
    {
        var first = new (string Field, OdsTagMultiSelect? Picker)[]
        {
            (nameof(UpdateInsurancePolicy.InsurerIds), _insurersPicker),
            (nameof(UpdateInsurancePolicy.InsuredAccountIds), _insuredAccountsPicker),
            (nameof(UpdateInsurancePolicy.InsuredContactIds), _insuredContactsPicker),
            (nameof(UpdateInsurancePolicy.BeneficiaryIds), _beneficiariesPicker),
        }.FirstOrDefault(entry => _fieldErrors.ContainsKey(entry.Field));

        if (first.Picker is { } picker)
        {
            try
            {
                await picker.FocusAsync();
            }
            catch (Exception)
            {
                // Best-effort focus; the message is already associated and announced.
            }
        }
    }
}
