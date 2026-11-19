using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Tests;

/// <summary>
/// The bUnit harness for the calendar <c>.ics</c> surfaces (issue #517): a mocked <see cref="ICalendarApiClient"/>, a
/// scripted <see cref="FakeNativeTransfer"/>, the import limits, a recording snackbar, and a host that renders the
/// dialog and popover providers beside the component under test.
/// </summary>
internal sealed class CalendarIcsTestContext : IAsyncDisposable
{
    public static readonly Guid CalendarId = Guid.Parse("0b5c3b52-4c1f-4f62-9c1c-6f2f6f0c0a01");

    public static readonly ExistingCalendar Personal = new() { CalendarId = CalendarId, Name = "Personal", Color = "#0369A1" };

    public static readonly string[] AllCalendarClaims =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate,
        PermissionClaims.CalendarDelete,
    ];

    public CalendarIcsTestContext(params string[] claims)
    {
        Ctx = PageTestContext.Create(claims.Length == 0 ? AllCalendarClaims : claims);
        Calendar.Setup(c => c.ExportUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://api.test/api/calendars/{id}/ics");
        Calendar.Setup(c => c.ExportEventUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://api.test/api/calendar-events/{id}/ics");
        Calendar.Setup(c => c.ExportPatternUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://api.test/api/recurrence-patterns/{id}/ics");
        Calendar.Setup(c => c.ExportAggregateUrl(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<string?>()))
            .Returns("http://api.test/api/calendar-events/ics");
        Calendar.Setup(c => c.ExportAggregateUrl(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns("http://api.test/api/calendar-events/ics");
        Calendar.Setup(c => c.ImportUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://api.test/api/calendars/{id}/ics");
        Calendar.Setup(c => c.ListCalendarsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingCalendar>>.Success([Personal], HttpStatusCode.OK));
        Ctx.Services.AddSingleton(Calendar.Object);
        // The viewer's zone is fixed, so a window or a conversion never depends on the machine running the tests.
        Ctx.Services.AddSingleton(_ => new ViewerTimeZone(ViewerZone));
        Ctx.Services.AddSingleton<INativeTransfer>(Transfer);
        Limits.Setup(l => l.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => CurrentLimits);
        Ctx.Services.AddSingleton(Limits.Object);
        Snackbar.Setup(s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()))
            .Callback((string message, Severity severity, Action<SnackbarOptions>? configure, string? key) =>
            {
                var options = new SnackbarOptions(severity, new SnackbarConfiguration());
                configure?.Invoke(options);
                Toasts.Add(new Toast(message, severity, options.RequireInteraction is true, options.VisibleStateDuration is int duration ? duration : 0, key));
            })
            .Returns((Snackbar?)null);
        Ctx.Services.AddSingleton(Snackbar.Object);
    }

    /// <summary>The viewer's browser zone the harness registers (issue #520); a test may construct with another.</summary>
    public string? ViewerZone { get; init; } = "UTC";

    public BunitContext Ctx { get; }

    public Mock<ICalendarApiClient> Calendar { get; } = new() { DefaultValueProvider = new PageTestContext.EmptySuccess() };

    public FakeNativeTransfer Transfer { get; } = new();

    public Mock<IImportLimitsCache> Limits { get; } = new();

    /// <summary>What <see cref="Limits"/> returns; a 2 GB calendar import cap unless a test says otherwise.</summary>
    public ImportLimitsDto CurrentLimits { get; set; } = ImportLimitsCache.Fallback with { CalendarIcsMaxImportMegabytes = 2048 };

    public Mock<ISnackbar> Snackbar { get; } = new();

    public List<Toast> Toasts { get; } = [];

    public sealed record Toast(string Message, Severity Severity, bool RequireInteraction, int Duration, string? Key);

    /// <summary>Renders the providers and the component; the component is found with <c>FindComponent</c>.</summary>
    public IRenderedComponent<Bunit.Rendering.ContainerFragment> Render<TComponent>(IReadOnlyDictionary<string, object?>? parameters = null)
        where TComponent : IComponent =>
        Ctx.Render(b =>
        {
            b.OpenComponent<MudDialogProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudPopoverProvider>(10);
            b.CloseComponent();
            b.OpenComponent<TComponent>(20);
            foreach (var (name, value) in parameters ?? new Dictionary<string, object?>())
            {
                b.AddComponentParameter(30, name, value);
            }

            b.CloseComponent();
        });

    public static CalendarIcsExportSummary Counts(int occurrences, int series = 0, int events = -1) =>
        new() { OccurrenceCount = occurrences, SeriesCount = series, EventCount = events < 0 ? occurrences : events };

    public static ApiResult<T> Ok<T>(T value) => ApiResult<T>.Success(value, HttpStatusCode.OK);

    public static ApiResult<T> Fail<T>(HttpStatusCode status, string detail = "Refused by the server.", string? code = null,
        IDictionary<string, object>? extensions = null) =>
        ApiResult<T>.Failure(status, new ApiProblem
        {
            Status = (int)status, Title = "Problem", Detail = detail, Code = code,
            Extensions = extensions?.ToDictionary(e => e.Key, e => JsonSerializer.SerializeToElement(e.Value)),
        });

    public ValueTask DisposeAsync() => Ctx.DisposeAsync();
}

/// <summary>
/// A scripted <see cref="INativeTransfer"/>: records every download and upload, holds an upload open until the test
/// sends its body (<see cref="CompleteUpload"/>) and answers it (<see cref="Respond"/>), and makes
/// <see cref="AbortAsync"/> end it as the browser would — aborted, with whatever the body had reached.
/// </summary>
internal sealed class FakeNativeTransfer : INativeTransfer
{
    private TaskCompletionSource<object?>? pending;
    private Action? onUploadComplete;
    private bool bodySent;

    /// <summary>Whether the browser holds the picked file; false drives the typed fallback.</summary>
    public bool Holds { get; set; } = true;

    public List<string> Downloads { get; } = [];

    public List<(string Url, IReadOnlyList<KeyValuePair<string, string>>? Fields)> Uploads { get; } = [];

    public int AbortCalls { get; private set; }

    public int ForgetCalls { get; private set; }

    public bool UploadInFlight => pending is { Task.IsCompleted: false };

    public ValueTask WatchAsync(string containerId) => ValueTask.CompletedTask;

    public Task<bool> HoldsAsync(string containerId, IBrowserFile file, CancellationToken ct = default) => Task.FromResult(Holds);

    public Task<ApiResult<T>?> TryUploadAsync<T>(string containerId, IBrowserFile file, string url, string? contentType, CancellationToken ct = default) =>
        throw new NotSupportedException("The calendar import uses the overload with fields and a callback.");

    public async Task<NativeUploadOutcome<T>?> TryUploadAsync<T>(
        string containerId, IBrowserFile file, string url, string? contentType,
        IReadOnlyList<KeyValuePair<string, string>>? fields, Action? onUploadComplete, CancellationToken ct = default)
    {
        if (!Holds)
        {
            return null;
        }

        Uploads.Add((url, fields));
        this.onUploadComplete = onUploadComplete;
        bodySent = false;
        pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (NativeUploadOutcome<T>?)await pending.Task;
    }

    /// <summary>The browser has sent the whole body: the dialog hears <c>onUploadComplete</c>.</summary>
    public void CompleteUpload()
    {
        bodySent = true;
        onUploadComplete?.Invoke();
    }

    /// <summary>The response arrives.</summary>
    public void Respond(ApiResult<IcsImportResult> result, bool jsonBody = true) =>
        pending!.TrySetResult(new NativeUploadOutcome<IcsImportResult>(result, Aborted: false, UploadComplete: bodySent, JsonBody: jsonBody));

    public ValueTask AbortAsync(string containerId)
    {
        AbortCalls++;
        pending?.TrySetResult(new NativeUploadOutcome<IcsImportResult>(
            ApiResult<IcsImportResult>.Failure(new HttpRequestException("The upload was stopped.")),
            Aborted: true, UploadComplete: bodySent, JsonBody: false));
        return ValueTask.CompletedTask;
    }

    public ValueTask ForgetAsync(string containerId)
    {
        ForgetCalls++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DownloadAsync(string url)
    {
        Downloads.Add(url);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A host that re-runs its content on demand, so a test can change a dialog's parameters (its Open flag).</summary>
internal sealed class RefreshHost : ComponentBase
{
    [Parameter] public RenderFragment? ChildContent { get; set; }

    public Task RefreshAsync() => InvokeAsync(StateHasChanged);

    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
        builder.AddContent(0, ChildContent);
}
