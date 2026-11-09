using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance.Instruments;

/// <summary>"Delete all prices…": the count, the typed confirmation and the delete. Markup in the <c>.razor</c>.</summary>
public partial class PurgeInstrumentPricesDialog
{
    /// <summary>The <c>409</c> code for a stale confirmed count (#446 §9); its <c>actualCount</c> extension is the new one.</summary>
    internal const string CountMismatchCode = "purge-count-mismatch";

    /// <summary>The <c>409</c> code while the price sync holds the job's run guard.</summary>
    internal const string AlreadyRunningCode = Odyssey.Dtos.Application.JobRunConflictCodes.AlreadyRunning;

    internal const string AlreadyRunningMessage = "A price sync is running. Try again in a minute.";
    internal const string ForbiddenMessage = "You don't have permission to delete these prices.";
    internal const string TimeoutMessage = "The server didn't answer. Nothing was deleted unless the list says so — reload to check.";
    internal const string NothingToDelete = "There is nothing to delete for this scope.";

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    [Parameter, EditorRequired] public ExistingInstrument Instrument { get; set; } = default!;

    /// <summary>Whether to offer the Fetched and All scopes (<c>instrument-prices.delete</c> + <c>system-settings.update</c>).</summary>
    [Parameter] public bool CanPurgeFetched { get; set; }

    /// <summary>Where focus returns on close: the instrument record's header.</summary>
    [Parameter] public string? ReturnFocusToId { get; set; }

    /// <summary>Raised with the number deleted; the dialog has closed.</summary>
    [Parameter] public EventCallback<int> OnPurged { get; set; }

    internal enum CountPhase { Counting, Counted, Failed }

    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];
    private InstrumentPricePurgeScope _scope = InstrumentPricePurgeScope.Manual;
    private CountPhase _phase = CountPhase.Counting;
    private int? _count;
    private string _typed = string.Empty;
    private int? _changedTo;
    private string? _error;
    private bool _timedOut;
    private bool _deleting;
    private bool _focusField;
    private int _fieldKey;
    private int _countVersion;
    private bool _wasOpen;
    private OdsLiveAnnouncer? _assertive;

    private string CountId => $"ins-purge-count-{_uid}";

    private string FieldId => $"ins-purge-confirm-{_uid}";

    private RenderFragment TitleFragment => b => b.AddContent(0, $"Delete prices for {Instrument.Symbol}?");

    private IReadOnlyList<OdsRadioOption> ScopeOptions => ScopeOptionsFor(CanPurgeFetched);

    internal static IReadOnlyList<OdsRadioOption> ScopeOptionsFor(bool canPurgeFetched) => canPurgeFetched
        ?
        [
            new(nameof(InstrumentPricePurgeScope.Manual), "Manual prices") { Description = "Prices entered by hand." },
            new(nameof(InstrumentPricePurgeScope.Fetched), "Fetched prices")
            {
                Description = "Prices fetched from price sources. Fetching resumes unless the sources are turned off.",
            },
            new(nameof(InstrumentPricePurgeScope.All), "All prices"),
        ]
        : [new(nameof(InstrumentPricePurgeScope.Manual), "Manual prices") { Description = "Prices entered by hand." }];

    private string CountText => _phase switch
    {
        CountPhase.Counting => "Counting…",
        CountPhase.Counted when _count == 0 => NothingToDelete,
        CountPhase.Counted when _count is { } n => $"This deletes {n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? "price" : "prices")}.",
        _ => string.Empty,
    };

    internal static string ChangedMessage(int count) =>
        $"The number of prices changed to {count.ToString("N0", CultureInfo.CurrentCulture)}. Type the new number to confirm.";

    /// <summary>
    /// Whether the typed text confirms <paramref name="count"/>: trimmed, parsed as an integer in the invariant
    /// culture, and equal. Paste and surrounding spaces are fine; a group separator is not.
    /// </summary>
    internal static bool Confirms(string? typed, int? count) =>
        count is > 0 and var n
        && int.TryParse(typed?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value == n;

    private bool Matches => Confirms(_typed, _count);

    private Dictionary<string, object> FieldAttributes => _deleting
        ? new() { ["readonly"] = true, ["aria-readonly"] = "true" }
        : [];

    protected override async Task OnParametersSetAsync()
    {
        var opening = Open && !_wasOpen;
        _wasOpen = Open;
        if (opening)
            await CountAsync();
    }

    /// <summary>The scope's count: <c>GET /api/instrument-prices</c> filtered, one row, read off <c>TotalCount</c>.</summary>
    private async Task CountAsync()
    {
        var version = ++_countVersion;
        _phase = CountPhase.Counting;
        _count = null;
        _typed = string.Empty;
        _changedTo = null;
        _error = null;
        _timedOut = false;
        StateHasChanged();

        var result = await Prices.ListAsync(1, 1, FilterFor(Instrument.InstrumentId, _scope));
        if (version != _countVersion)
            return;

        if (result.IsSuccess && result.Value is { } page)
        {
            _count = page.TotalCount;
            _phase = CountPhase.Counted;
        }
        else
        {
            _phase = CountPhase.Failed;
        }

        StateHasChanged();
    }

    internal static InstrumentPricesFilter FilterFor(Guid instrumentId, InstrumentPricePurgeScope scope) => new()
    {
        InstrumentIds = [instrumentId],
        Sources = scope switch
        {
            InstrumentPricePurgeScope.Manual => [InstrumentPriceSource.Manual],
            InstrumentPricePurgeScope.Fetched => [InstrumentPriceSource.Fetched],
            _ => null,
        },
    };

    private async Task OnScopeChanged(string value)
    {
        if (_deleting || !Enum.TryParse<InstrumentPricePurgeScope>(value, out var scope) || scope == _scope)
            return;
        _scope = scope;
        await CountAsync();
    }

    private void OnTyped(string? value) => _typed = value ?? string.Empty;

    private async Task DeleteAsync()
    {
        if (_deleting || !Matches || _count is not { } expected)
            return;

        _deleting = true;
        _error = null;
        _timedOut = false;
        _changedTo = null;
        StateHasChanged();

        var result = await Instruments.PurgePricesAsync(Instrument.InstrumentId, _scope, expected);
        _deleting = false;

        if (result.IsSuccess)
        {
            await OpenChanged.InvokeAsync(false);
            await OnPurged.InvokeAsync(result.Value?.Deleted ?? expected);
            return;
        }

        var code = result.Problem?.Code;
        if (string.Equals(code, CountMismatchCode, StringComparison.Ordinal)
            && result.Problem!.TryGetExtensionValue<int>("actualCount", out var actual))
        {
            _count = actual;
            _phase = CountPhase.Counted;
            _typed = string.Empty;
            if (actual > 0)
            {
                _changedTo = actual;
                _fieldKey++;
                _focusField = true;
                _ = _assertive?.Announce(ChangedMessage(actual));
            }
            else
            {
                _ = _assertive?.Announce(NothingToDelete);
            }
        }
        else if (string.Equals(code, AlreadyRunningCode, StringComparison.Ordinal))
        {
            _error = AlreadyRunningMessage;
        }
        else if (result.Status == HttpStatusCode.Forbidden)
        {
            _error = ForbiddenMessage;
        }
        else if (result.Status is 0 or HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
        {
            _error = TimeoutMessage;
            _timedOut = true;
        }
        else
        {
            _error = string.IsNullOrWhiteSpace(result.Error) ? "The prices couldn’t be deleted." : result.Error;
        }

        StateHasChanged();
    }

    private Task CloseAsync() => OpenChanged.InvokeAsync(false);

    private void Reload() => Navigation.NavigateTo(Navigation.Uri, forceLoad: true);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusField)
            return;
        _focusField = false;
        try { await Js.InvokeVoidAsync("odsFocusById", FieldId); }
        catch (JSException) { }
        catch (InvalidOperationException) { /* no JS runtime under bUnit */ }
    }
}
