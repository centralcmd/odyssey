using Microsoft.AspNetCore.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class CreateInsurancePolicyDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>When set, the dialog edits this policy. Null = create mode.</summary>
    [Parameter] public ExistingInsurancePolicy? InsurancePolicy { get; set; }

    private bool IsEdit => InsurancePolicy is not null;

    private string? _name;
    private string? _policyNumber;
    private string _type = string.Empty;
    private string? _notes;

    private string? _nameError;
    private string? _typeError;

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
    }

    private async Task<bool> SaveAsync()
    {
        _nameError = string.IsNullOrWhiteSpace(_name) ? "Give the policy a name." : null;
        _typeError = string.IsNullOrEmpty(_type) ? "Pick a policy type." : null;

        if (_nameError is not null || _typeError is not null)
        {
            return false;
        }

        var name = _name!.Trim();
        var policyNumber = string.IsNullOrWhiteSpace(_policyNumber) ? null : _policyNumber!.Trim();
        var type = Enum.TryParse<InsurancePolicyType>(_type, out var t) ? t : InsurancePolicyType.Other;
        var notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes!.Trim();

        ApiClient.ApiResult result;
        if (InsurancePolicy is { } existing)
        {
            // Every link collection is omitted, which UpdateInsurancePolicy reads as "leave unchanged".
            // Parties are written one at a time from the policy's own actions, so this dialog must not
            // be able to add or drop one.
            result = await Insurance.UpdateAsync(existing.InsurancePolicyId, new UpdateInsurancePolicy
            {
                Name = name,
                PolicyNumber = policyNumber,
                Type = type,
                Notes = notes,
                // Archive/restore is a separate row action — preserve whatever the record already has.
                Archived = existing.Archived is not null,
            });
        }
        else
        {
            // A new policy names nobody; every collection is optional and zero members is healthy.
            result = await Insurance.CreateAsync(new NewInsurancePolicy
            {
                Name = name,
                PolicyNumber = policyNumber,
                Type = type,
                Notes = notes,
            });
        }

        var lead = IsEdit ? "Unable to update policy" : "Unable to create policy";
        return result.Toast(Snackbar, lead, IsEdit ? "Policy updated." : "Policy created.");
    }
}
