using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AddRenewalDialog
{
    [Parameter, EditorRequired] public ExistingInsurancePolicy Policy { get; set; } = default!;

    /// <summary>The renewal being edited, or null to add a new one.</summary>
    [Parameter] public ExistingPolicyRenewal? Renewal { get; set; }

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can reload.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    private bool IsEdit => Renewal is not null;

    private DateTime? _fromDate;
    private DateTime? _toDate;
    private string _premiumStr = string.Empty;
    private string _premiumCurrency = "USD";
    private string _coverageStr = string.Empty;
    private string _coverageCurrency = "USD";
    private string? _notes;
    private bool _isSaving;

    private List<OdsOption> _currencyOptions = [];
    private Dictionary<string, string> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new();
}
