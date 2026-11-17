using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Odyssey.Client.Components;

/// <summary>
/// Renders nothing; hands <see cref="OdsModal"/> the <see cref="IMudDialogInstance"/> its body is shown in, so a
/// change to <see cref="OdsModal.Dismissible"/> reaches the open dialog.
/// </summary>
public sealed class OdsModalInstanceProbe : ComponentBase
{
    [CascadingParameter] private IMudDialogInstance? Instance { get; set; }

    [Parameter] public Action<IMudDialogInstance?>? Register { get; set; }

    protected override void OnParametersSet() => Register?.Invoke(Instance);
}
