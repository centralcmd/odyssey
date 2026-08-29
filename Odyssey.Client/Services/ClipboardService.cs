using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Services;

/// <summary>
/// Centralizes the copy-to-clipboard flow every page hand-rolled: the
/// <c>navigator.clipboard.writeText</c> interop wrapped in a try/catch that toasts a
/// success message on success and a "couldn't copy" error if the browser blocks it.
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// Writes <paramref name="text"/> to the clipboard. On success toasts
    /// <paramref name="successMessage"/> when given; on failure (clipboard blocked or
    /// unavailable) toasts an error. Returns whether the copy succeeded.
    /// </summary>
    Task<bool> CopyAsync(string text, string? successMessage = null);
}

/// <inheritdoc cref="IClipboardService" />
public sealed class ClipboardService(IJSRuntime js, ISnackbar snackbar) : IClipboardService
{
    public async Task<bool> CopyAsync(string text, string? successMessage = null)
    {
        try
        {
            await js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            if (!string.IsNullOrEmpty(successMessage))
                snackbar.Add(successMessage, Severity.Success);

            return true;
        }
        catch (Exception ex)
        {
            snackbar.Add($"Unable to copy: {ex.Message}", Severity.Error);
            return false;
        }
    }
}
