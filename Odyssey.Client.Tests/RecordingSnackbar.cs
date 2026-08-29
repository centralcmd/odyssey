using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Odyssey.Client.Tests;

/// <summary>
/// Captures what reached the snackbar, so a silent failure is a failing test. Shared by every test
/// that asserts on the app's "tell the user why the read failed" behaviour.
/// </summary>
internal sealed class RecordingSnackbar : ISnackbar
{
    public List<(string Message, Severity Severity)> Toasts { get; } = [];

    public Snackbar? Add(string message, Severity severity = Severity.Normal,
        Action<SnackbarOptions>? configure = null, string? key = "")
    {
        Toasts.Add((message, severity));
        return null;
    }

    public Snackbar? Add(MarkupString message, Severity severity = Severity.Normal,
        Action<SnackbarOptions>? configure = null, string? key = "")
    {
        Toasts.Add((message.Value, severity));
        return null;
    }

    public Snackbar? Add(RenderFragment message, Severity severity = Severity.Normal,
        Action<SnackbarOptions>? configure = null, string? key = "")
    {
        Toasts.Add(("<render fragment>", severity));
        return null;
    }

    public IEnumerable<Snackbar> ShownSnackbars => [];
    public SnackbarConfiguration Configuration { get; } = new();
    public event Action? OnSnackbarsUpdated { add { } remove { } }

    public Snackbar? Add<T>(Dictionary<string, object>? componentParameters = null,
        Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null)
        where T : IComponent => null;
    public Snackbar? AddNew(Severity severity, string message, Action<SnackbarOptions>? configure) => null;
    public void Clear() { }
    public void Remove(Snackbar snackbar) { }
    public void RemoveByKey(string key) { }
    public void Dispose() { }
}
