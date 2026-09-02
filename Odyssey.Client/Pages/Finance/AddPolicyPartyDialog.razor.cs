using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AddPolicyPartyDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    [Parameter, EditorRequired] public ExistingInsurancePolicy Policy { get; set; } = default!;

    /// <summary>The link being edited — role plus target — or null for the one-off add.</summary>
    [Parameter] public PartyLink? Party { get; set; }

    /// <summary>Pre-loaded, active contact options (three of the four roles link a contact).</summary>
    [Parameter] public IReadOnlyList<OdsOption> Contacts { get; set; } = [];

    /// <summary>Pre-loaded, active account options.</summary>
    [Parameter] public IReadOnlyList<OdsOption> Accounts { get; set; } = [];

    /// <summary>True while the option lists are still being fetched.</summary>
    [Parameter] public bool OptionsLoading { get; set; }

    /// <summary>Raised after a successful write so the host re-fetches the policy.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>
    /// Raised when a save was rejected because the chosen id named no live record: the host owns the
    /// option cache, so only it can invalidate and reload. Without this the dialog would re-serve the
    /// same stale list it just failed on.
    /// </summary>
    [Parameter] public EventCallback OnStaleOptions { get; set; }

    /// <summary>One party link, addressed the way the API addresses it: role plus target.</summary>
    public sealed record PartyLink(InsurancePartyRole Role, Guid TargetId);

    private InsurancePartyRole _role = InsurancePartyRole.Insurer;
    private string? _value;
    private string? _error;
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private readonly Dictionary<string, string> _errors = [];
    private bool _isSaving;
    private bool _isRemoving;

    private bool IsEdit => Party is not null;

    private sealed record RoleDef(InsurancePartyRole Role, string Label, string Icon, string Noun, string Help);

    private static readonly IReadOnlyList<RoleDef> Roles =
    [
        new(InsurancePartyRole.Insurer, "Insurer", "groups", "contact", "The contact that carries this cover."),
        new(InsurancePartyRole.InsuredAccount, "Insured account", "account_balance_wallet", "account", "An account representing an insured asset."),
        new(InsurancePartyRole.InsuredContact, "Insured contact", "person", "contact", "A person or organisation insured under this policy."),
        new(InsurancePartyRole.Beneficiary, "Beneficiary", "volunteer_activism", "contact", "Who receives on this policy."),
    ];

    private readonly ElementReference[] _roleRefs = new ElementReference[Roles.Count];

    private RoleDef Current => Roles.First(r => r.Role == _role);

    protected override void OnInitialized()
    {
        if (Party is { } link)
        {
            _role = link.Role;
            _value = link.TargetId.ToString();
            var (from, to) = TermOf(link);
            _fromDate = from?.Date;
            _toDate = to?.Date;
        }
    }

    /// <summary>The stored term of the link being edited, read off whichever collection holds it.</summary>
    private (DateTime? From, DateTime? To) TermOf(PartyLink link) => link.Role switch
    {
        InsurancePartyRole.InsuredAccount => Policy.InsuredAccounts
            .Where(a => a.AccountId == link.TargetId)
            .Select(a => (a.FromDate, a.ToDate)).FirstOrDefault(),
        _ => ContactCollection(link.Role)
            .Where(c => c.ContactId == link.TargetId)
            .Select(c => (c.FromDate, c.ToDate)).FirstOrDefault(),
    };

    private IReadOnlyList<PolicyContactReference> ContactCollection(InsurancePartyRole role) => role switch
    {
        InsurancePartyRole.Insurer => Policy.Insurers,
        InsurancePartyRole.InsuredContact => Policy.InsuredContacts,
        _ => Policy.Beneficiaries,
    };

    /// <summary>
    /// Cover's first day, when the policy has any period. A party cannot be in the role before cover
    /// ever began — the one tie between the party's own term and the policy's.
    /// </summary>
    private DateTime? CoverBegan => Policy.Renewals.Count == 0
        ? null
        : Policy.Renewals.Min(r => r.FromDate).Date;

    private string FromHelp => CoverBegan is { } began
        ? $"Leave empty to start with the policy ({began.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})."
        : "Leave empty to start with the policy.";

    private IReadOnlyList<OdsOption> AllForRole =>
        _role == InsurancePartyRole.InsuredAccount ? Accounts : Contacts;

    /// <summary>
    /// Ids already in the chosen role. The party BEING EDITED is not "already linked" as far as its
    /// own picker is concerned — only its siblings are.
    /// </summary>
    private HashSet<string> LinkedForRole
    {
        get
        {
            var linked = _role == InsurancePartyRole.InsuredAccount
                ? Policy.InsuredAccounts.Select(a => a.AccountId.ToString())
                : ContactCollection(_role).Select(c => c.ContactId.ToString());
            var set = linked.ToHashSet();
            if (Party is { } p && p.Role == _role)
            {
                set.Remove(p.TargetId.ToString());
            }

            return set;
        }
    }

    private IReadOnlyList<OdsOption> Available => [.. AllForRole.Where(o => !LinkedForRole.Contains(o.Value))];

    /// <summary>
    /// The picker's helper line: what the role means, plus how many records are still linkable — so
    /// "nothing to choose" reads as a stated fact rather than as an empty list.
    /// </summary>
    private string PickerHelp => Available.Count > 0
        ? $"{Current.Help} {Available.Count} {Current.Noun}{(Available.Count == 1 ? "" : "s")} available to link."
        : $"Every {Current.Noun} is already linked to this policy in this role.";

    private void PickRole(InsurancePartyRole role)
    {
        if (_role == role) return;
        _role = role;
        // The record is role-specific, so a role change clears it — except back on the edited link's
        // own role, where the party being edited is still the obvious selection.
        _value = Party is { } p && p.Role == role ? p.TargetId.ToString() : null;
        _error = null;
    }

    // Radiogroup keyboard model (WCAG 2.1.1 / 4.1.2, APG radio pattern): arrows + Home/End move the
    // selection and the focus across the role options. Only the selected radio is in the tab order
    // (roving tabindex), so the group is a single tab stop.
    private async Task OnRoleKeyAsync(KeyboardEventArgs e, int index)
    {
        int? next = e.Key switch
        {
            "ArrowRight" or "ArrowDown" => (index + 1) % Roles.Count,
            "ArrowLeft" or "ArrowUp" => (index - 1 + Roles.Count) % Roles.Count,
            "Home" => 0,
            "End" => Roles.Count - 1,
            _ => null,
        };
        if (next is not { } ni)
            return;

        PickRole(Roles[ni].Role);
        await _roleRefs[ni].FocusAsync();
    }

    private void OnValueChanged(string? value)
    {
        _value = value;
        _error = null;
    }

    private void OnFromChanged(DateTime? value)
    {
        _fromDate = value;
        _errors.Remove("fromDate");
    }

    private void OnToChanged(DateTime? value)
    {
        _toDate = value;
        _errors.Remove("toDate");
    }

    private async Task CloseAsync() => await OpenChanged.InvokeAsync(false);

    private async Task SubmitAsync()
    {
        if (_isSaving || _isRemoving) return;
        _error = null;
        _errors.Clear();

        if (!Guid.TryParse(_value, out var targetId))
        {
            _error = $"Select {(Current.Noun[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an" : "a")} {Current.Noun} to link.";
            return;
        }

        if (_fromDate is { } from && CoverBegan is { } began && from.Date < began)
        {
            _errors["fromDate"] =
                $"Cover began {began.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} — a party can't be in the role before that.";
        }

        if (_fromDate is { } f && _toDate is { } t && t.Date < f.Date)
        {
            _errors["toDate"] = "End date can't be before the start date.";
        }

        if (_errors.Count > 0) return;

        var body = new InsurancePolicyPartyRequest
        {
            Role = _role,
            TargetId = targetId,
            FromDate = _fromDate is { } fd ? DateTime.SpecifyKind(fd.Date, DateTimeKind.Utc) : null,
            ToDate = _toDate is { } td ? DateTime.SpecifyKind(td.Date, DateTimeKind.Utc) : null,
        };

        _isSaving = true;
        try
        {
            // On the edit the ROUTE names the link as it stands and the body names what it should
            // become, so a party moved between roles stays one party rather than two.
            var result = Party is { } link
                ? await Insurance.UpdatePartyAsync(Policy.InsurancePolicyId, link.Role, link.TargetId, body)
                : await Insurance.AddPartyAsync(Policy.InsurancePolicyId, body);

            var ok = result.Toast(Snackbar,
                IsEdit ? "Unable to update party" : "Unable to add party",
                IsEdit ? "Party updated." : "Party added.");
            if (!ok)
            {
                // A 400 means the id named no live record: the options the user chose from are stale,
                // and only the host can invalidate the reference cache behind them.
                if (result.Status == System.Net.HttpStatusCode.BadRequest)
                {
                    await OnStaleOptions.InvokeAsync();
                }

                return;
            }

            await OnSaved.InvokeAsync();
            await OpenChanged.InvokeAsync(false);
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task RemoveAsync()
    {
        if (Party is not { } link || _isSaving || _isRemoving) return;

        _isRemoving = true;
        try
        {
            var ok = (await Insurance.RemovePartyAsync(Policy.InsurancePolicyId, link.Role, link.TargetId))
                .Toast(Snackbar, "Unable to remove party", "Party removed.");
            if (!ok) return;

            await OnSaved.InvokeAsync();
            await OpenChanged.InvokeAsync(false);
        }
        finally
        {
            _isRemoving = false;
        }
    }
}
