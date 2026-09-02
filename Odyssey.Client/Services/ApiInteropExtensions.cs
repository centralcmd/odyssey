using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Dtos;

namespace Odyssey.Client.Services;

/// <summary>
/// The seam between the Blazor UI and the host-agnostic <c>Odyssey.ApiClient</c> library: adapts a
/// browser file into the library's transport shape, and turns a library result into a snackbar.
/// </summary>
public static class ApiInteropExtensions
{
    /// <summary>
    /// Wraps an <see cref="IBrowserFile"/> as an <see cref="ApiUpload"/> the library can post.
    /// <paramref name="maxSizeBytes"/> is the cap handed to <see cref="IBrowserFile.OpenReadStream"/>,
    /// which throws if the file exceeds it — so the cap stays the caller's decision, per endpoint.
    /// </summary>
    public static ApiUpload ToApiUpload(this IBrowserFile file, long maxSizeBytes) =>
        new(file.Name,
            file.ContentType,
            file.Size,
            () => file.OpenReadStream(maxSizeBytes));

    /// <summary>
    /// Returns the value on success; on failure toasts "{failureLead}: {detail}" and returns
    /// <c>null</c>. The default path for a page that just needs "do it, or tell me why not".
    /// </summary>
    public static T? OrToast<T>(this ApiResult<T> result, ISnackbar snackbar, string failureLead)
        where T : class
    {
        if (result.IsSuccess)
            return result.Value;

        snackbar.Add($"{failureLead}: {result.Error}", Severity.Error);
        return null;
    }

    /// <summary>
    /// Returns the value on success; on failure toasts "Unable to load {what}: {detail}" and returns
    /// <paramref name="fallback"/>. The "Unable to load …" wording is the app-wide convention for a
    /// failed read, so every surface reads the same way to the user.
    /// </summary>
    public static T ValueOrToast<T>(this ApiResult<T> result, ISnackbar snackbar, string what, T fallback)
    {
        if (!result.IsSuccess)
            snackbar.Add($"Unable to load {what}: {result.Error}", Severity.Error);

        return result.ValueOr(fallback);
    }

    /// <summary>Convenience for the common "load a list, fall back to empty" case.</summary>
    public static List<T> ItemsOrToast<T>(this ApiResult<List<T>> result, ISnackbar snackbar, string what) =>
        result.ValueOrToast(snackbar, what, []);

    /// <summary>
    /// Adapts a paged transport result into the page-facing <see cref="PagedLoad{T}"/>, toasting
    /// "Unable to load {what}: {detail}" on failure — the pairing every server-paginated list needs so
    /// Empty (a success with no rows) is never confused with Error.
    /// </summary>
    /// <remarks>
    /// This exists because that pairing is easy to half-do: a call site that unwraps with
    /// <c>result.Value?.Items ?? []</c> renders an empty list and says nothing. That regression was
    /// found on three Photos surfaces during the typed-client migration, which is why the pairing is a
    /// helper rather than something each page writes. Prefer this over hand-rolling the check.
    /// </remarks>
    public static PagedLoad<T> PagedOrToast<T>(this ApiResult<PagedResult<T>> result, ISnackbar snackbar, string what)
    {
        if (!result.IsSuccess)
            snackbar.Add($"Unable to load {what}: {result.Error}", Severity.Error);

        return PagedLoad<T>.From(result);
    }

    /// <summary>
    /// The rows of a paged result, toasting on failure. For callers that want just the items from a
    /// paginated endpoint and have no Empty-vs-Error state to drive.
    /// </summary>
    public static IReadOnlyList<T> PagedItemsOrToast<T>(this ApiResult<PagedResult<T>> result, ISnackbar snackbar, string what) =>
        result.PagedOrToast(snackbar, what).Items;

    /// <summary>
    /// Maps a write result onto the snackbar — success toast if given, otherwise
    /// "{failureLead}: {detail}" — and returns whether it succeeded. The app-wide shape for a write
    /// whose outcome the user should see.
    /// </summary>
    public static bool Toast(this ApiResult result, ISnackbar snackbar, string failureLead, string? successMessage = null)
    {
        if (result.IsSuccess)
        {
            if (!string.IsNullOrEmpty(successMessage))
                snackbar.Add(successMessage, Severity.Success);

            return true;
        }

        snackbar.Add($"{failureLead}: {result.Error}", Severity.Error);
        return false;
    }

    /// <summary>
    /// Routes a failed write to the form fields that caused it, falling back to a toast when the
    /// server named none.
    ///
    /// <para>
    /// The reusable half of <c>ApiProblem.Errors</c> → form mapping (issue #27 §11): a dialog whose
    /// only field errors are client-side pre-checks has no way to show a server rejection on the
    /// control responsible, so every failure becomes a toast the user then has to translate back into
    /// a field. <paramref name="assign"/> is called once per field the server named, with the JSON
    /// property name and the first message; anything it does not recognise (and any failure with no
    /// field errors at all) is toasted instead, so a rejection can never vanish.
    /// </para>
    /// </summary>
    /// <returns>True on success — the same contract <see cref="Toast(ApiResult, ISnackbar, string, string?)"/> has.</returns>
    public static bool ToastOrFields(
        this ApiResult result,
        ISnackbar snackbar,
        string failureLead,
        Func<string, string, bool> assign,
        string? successMessage = null)
    {
        if (result.IsSuccess)
        {
            if (!string.IsNullOrEmpty(successMessage))
                snackbar.Add(successMessage, Severity.Success);

            return true;
        }

        // Every message the form could not place is toasted — NOT "toast only when nothing was
        // placed". A response naming two fields where the caller recognises one would otherwise
        // report the recognised one on its control and drop the other entirely: no toast, no field
        // marking, no trace. That is the exact vanishing this method's contract rules out.
        var unassigned = new List<string>();
        var errors = result.Problem?.Errors ?? new Dictionary<string, string[]>();
        foreach (var (field, messages) in errors)
        {
            if (messages.Length == 0)
            {
                continue;
            }

            if (!assign(field, messages[0]))
            {
                unassigned.Add(messages[0]);
            }
        }

        if (unassigned.Count > 0)
        {
            snackbar.Add($"{failureLead}: {string.Join(" ", unassigned)}", Severity.Error);
        }
        else if (errors.Count == 0)
        {
            // No field errors at all — the failure has only its top-level detail to report.
            snackbar.Add($"{failureLead}: {result.Error}", Severity.Error);
        }

        return false;
    }
}
