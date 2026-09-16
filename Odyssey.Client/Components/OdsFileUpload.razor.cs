using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace Odyssey.Client.Components;

/// <summary>
/// Parameters and state for <see cref="OdsFileUpload"/> — the drag-and-drop upload field (Odyssey
/// Design System · components/FileUpload). The markup, and the reasoning behind the single dropzone
/// and the ready-file list, live in OdsFileUpload.razor.
/// </summary>
public partial class OdsFileUpload
{

    /// <summary>Controlled file list. Omit to run uncontrolled from DefaultFiles.</summary>
    [Parameter] public IReadOnlyList<OdsUploadFile>? Files { get; set; }

    /// <summary>Initial files when uncontrolled.</summary>
    [Parameter] public IReadOnlyList<OdsUploadFile> DefaultFiles { get; set; } = [];

    /// <summary>Fires with the full next array on every add / rename / retype / remove.</summary>
    [Parameter] public EventCallback<IReadOnlyList<OdsUploadFile>> FilesChanged { get; set; }

    /// <summary>Native input accept filter (e.g. "image/*,.pdf").</summary>
    [Parameter] public string? Accept { get; set; }

    /// <summary>Allow selecting / dropping more than one file.</summary>
    [Parameter] public bool Multiple { get; set; } = true;

    /// <summary>
    /// Secondary line under the dropzone label. Leave it unset and pass <see cref="MaxMegabytes"/> to
    /// have the size clause composed; override it for a surface with its own format vocabulary — but
    /// compose the number there too, never type it.
    /// </summary>
    [Parameter] public string? Hint { get; set; }

    /// <summary>
    /// The effective per-file cap, in megabytes, from which the default hint's size clause is composed.
    /// Never write the limit into <see cref="Hint"/> as a literal: the cap is an admin-editable runtime
    /// setting, so a typed number goes stale silently — the user-visible half of the same defect as a
    /// hard-coded pre-check. Where a surface has its own tighter product limit this is
    /// <c>min(surfaceConstant, serverCap)</c>; a surface may tighten the global cap, never override a
    /// lowered one. Null means the caller doesn't know the cap yet, and the hint then omits the size
    /// clause rather than asserting a default that may not be this deployment's.
    /// </summary>
    [Parameter] public int? MaxMegabytes { get; set; }

    private string EffectiveHint =>
        Hint
        ?? (MaxMegabytes is { } max
            ? $"PDF, JPG, PNG · up to {max} MB each · multiple at once"
            : "PDF, JPG, PNG · multiple at once");

    /// <summary>Error message — flips the dropzone to its error state and shows below it.</summary>
    [Parameter] public string? Error { get; set; }

    /// <summary>Horizontal, lower-profile dropzone for tight modals.</summary>
    [Parameter] public bool Compact { get; set; }

    /// <summary>Show the per-row file-kind icon + inline kind picker.</summary>
    [Parameter] public bool ShowKinds { get; set; } = true;

    /// <summary>Override the file-kind registry.</summary>
    [Parameter] public IReadOnlyList<OdsFileKind>? Kinds { get; set; }

    /// <summary>Kind key assigned to each newly-added file. When null, the kind is guessed from the
    /// extension (the generic Statement/Receipt/Document set). Set this when <see cref="Kinds"/> is a
    /// domain registry so new files start on a valid key from it.</summary>
    [Parameter] public string? DefaultKind { get; set; }

    /// <summary>Override the extension→kind guess with a domain vocabulary (tax / insurance / contract…).
    /// When set, each newly-added file's kind is <c>GuessKind(name)</c> (takes precedence over
    /// <see cref="DefaultKind"/> and the built-in extension guess).</summary>
    [Parameter] public Func<string, string>? GuessKind { get; set; }

    /// <summary>Renders an extra editor beneath each file row (e.g. a validity-dates grid). The context
    /// exposes the mutable file plus a <c>Changed</c> callback to re-commit the list after editing.</summary>
    [Parameter] public RenderFragment<OdsUploadFileExtraContext>? RenderFileExtra { get; set; }

    /// <summary>Cap the ready-file list height before it scrolls (CSS length, e.g. "246px").</summary>
    [Parameter] public string? MaxHeight { get; set; }

    [Parameter] public string? Id { get; set; }

    /// <summary>The error's own id, so the dropzone can name it in <c>aria-describedby</c>.</summary>
    private string ErrorId => $"{Id ?? _fallbackId}-err";

    private readonly string _fallbackId = $"ods-up-{Guid.NewGuid():N}";

    private bool _dragging;
    private List<OdsUploadFile> _internal = [];
    private int _uid;

    protected override void OnInitialized() => _internal = [.. DefaultFiles];

    private bool Controlled => Files is not null;

    private IReadOnlyList<OdsUploadFile> Current => Controlled ? Files! : _internal;

    private IReadOnlyList<OdsFileKind> EffectiveKinds => Kinds is { Count: > 0 } ? Kinds : OdsFileUploadDefaults.Kinds;

    // The kind registry projected to the shared OdsTypeSelect option shape (same fields, different type)
    // so the inline picker runs on the same engine as every other type select.
    private IReadOnlyList<OdsTypeOption> EffectiveKindOptions =>
        [.. EffectiveKinds.Select(k => new OdsTypeOption { Key = k.Key, Label = k.Label, Icon = k.Icon, Color = k.Color, Soft = k.Soft })];

    private long TotalBytes => Current.Sum(f => f.SizeBytes ?? 0);

    private OdsFileKind KindFor(string key) =>
        EffectiveKinds.FirstOrDefault(k => k.Key == key) ?? EffectiveKinds[0];

    private Task Commit(List<OdsUploadFile> next)
    {
        if (!Controlled)
            _internal = next;
        return FilesChanged.InvokeAsync(next);
    }

    private static Task OnDropzoneKey(KeyboardEventArgs e, MudFileUpload<IReadOnlyList<IBrowserFile>> upload) =>
        e.Key is "Enter" or " " ? upload.OpenFilePickerAsync() : Task.CompletedTask;

    private Task OnFilesChanged(IReadOnlyList<IBrowserFile> incoming)
    {
        if (incoming is null || incoming.Count == 0)
            return Task.CompletedTask;

        // Multiple=false is a genuine single-file field: a new pick/drop REPLACES the current file
        // rather than accumulating (the old logic ignored the flag and silently piled files up).
        var next = Multiple ? new List<OdsUploadFile>(Current) : [];
        foreach (var f in Multiple ? incoming : incoming.Take(1))
        {
            next.Add(new OdsUploadFile
            {
                Uid = $"ods-up-{++_uid}",
                Name = f.Name,
                Kind = GuessKind?.Invoke(f.Name) ?? DefaultKind ?? OdsFileUploadDefaults.GuessKind(f.Name),
                SizeBytes = f.Size,
                Source = f,
            });
        }
        return Commit(next);
    }

    private Task Rename(OdsUploadFile file, string name) =>
        Commit(Current.Select(f => f.Uid == file.Uid ? With(f, name: name) : f).ToList());

    private Task Retype(OdsUploadFile file, string kind) =>
        Commit(Current.Select(f => f.Uid == file.Uid ? With(f, kind: kind) : f).ToList());

    // Clone a row, carrying the per-file validity metadata through rename / retype so edits survive.
    private static OdsUploadFile With(OdsUploadFile f, string? name = null, string? kind = null) => new()
    {
        Uid = f.Uid,
        Name = name ?? f.Name,
        Kind = kind ?? f.Kind,
        SizeBytes = f.SizeBytes,
        Source = f.Source,
        ValidFrom = f.ValidFrom,
        ValidTo = f.ValidTo,
        IssuedAt = f.IssuedAt,
        IssuedBy = f.IssuedBy,
    };

    private Task Remove(OdsUploadFile file) =>
        Commit(Current.Where(f => f.Uid != file.Uid).ToList());
}
