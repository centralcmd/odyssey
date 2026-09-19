using System.Globalization;
using Microsoft.AspNetCore.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class TermHistoryTable
{
    /// <summary>The rows to render, already ordered by the caller (newest effective first).</summary>
    [Parameter, EditorRequired] public IReadOnlyList<ExistingTerm> Terms { get; set; } = [];

    /// <summary>
    /// The ids currently in force — one per <c>(kind, label)</c> series. Supplied by the caller
    /// rather than derived here, so the tiles above a table and the badges inside it are resolved
    /// from one computation and cannot disagree.
    /// </summary>
    [Parameter] public IReadOnlySet<Guid> CurrentIds { get; set; } = new HashSet<Guid>();

    /// <summary>
    /// The owning account, or <c>null</c> for a contract-owned history (issue #135). It supplies the
    /// cost-rate wording and colour, which only an account type can decide.
    /// </summary>
    [Parameter] public ExistingAccount? Account { get; set; }

    /// <summary>Formats a money-valued term in its own currency — supplied by the host.</summary>
    [Parameter, EditorRequired]
    public Func<decimal, string?, string> FormatMoney { get; set; } = (v, _) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Gates the per-row edit / delete actions. False drops the actions column entirely rather than
    /// disabling it: on an archived contract every write is refused, so an affordance that could only
    /// fail is not offered.
    /// </summary>
    [Parameter] public bool CanWrite { get; set; }

    [Parameter] public EventCallback<ExistingTerm> OnEdit { get; set; }

    [Parameter] public EventCallback<ExistingTerm> OnDelete { get; set; }
}
