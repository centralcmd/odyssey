using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.Dtos.Application;
using Odyssey.Client.Auth;

namespace Odyssey.Client.Pages;

/// <summary>
/// The /accept-terms interstitial (issue #354 §3, §5). Reached from <c>MainLayout</c>'s gate check at
/// login, or mid-session from <see cref="LegalComplianceHandler"/> intercepting a 451.
/// </summary>
public partial class AcceptTerms : IAsyncDisposable
{
    private enum Phase { Loading, Ready, Error }

    private enum Outcome { Accepted, Declined }

    private static readonly int[] SkeletonWidths = [68, 92, 80, 88, 54];

    private Phase _phase = Phase.Loading;
    private Outcome? _outcome;

    /// <summary>The documents this session still owes, in the order the stepper walks them.</summary>
    private List<LegalDocumentType> _documents = [];

    private readonly HashSet<LegalDocumentType> _responded = [];
    private readonly HashSet<LegalDocumentType> _acknowledged = [];
    private readonly HashSet<LegalDocumentType> _reachedEnd = [];

    private LicenseDocument? _license;
    private TermsOfServiceDocument? _terms;

    private LegalDocumentType? _busy;
    private LegalDocumentType? _declineTarget;
    private bool _declining;
    private bool _staleVersion;
    private string? _respondError;
    private string? _returnUrl;
    private string? _announce;

    private ElementReference _scrollEnd;
    private IJSObjectReference? _js;
    private DotNetObjectReference<AcceptTerms>? _selfRef;
    private LegalDocumentType? _observing;

    private string StepperLabel =>
        $"Step {Math.Min(_documents.Count(IsDone) + 1, Math.Max(_documents.Count, 1))} of {_documents.Count}";

    // _returnUrl is whatever LocalReturnUrl accepted, so it is either null or a rooted path — the only
    // one of which that isn't "somewhere the user left off" is the dashboard itself.
    private string ReturnLabel =>
        _returnUrl is null or "/" ? "the dashboard" : "where you left off";

    /// <summary>The first document still outstanding — the only one rendered at a time.</summary>
    private LegalDocumentType? CurrentDocument =>
        _documents.Cast<LegalDocumentType?>().FirstOrDefault(key => !IsDone(key!.Value));

    private string? ShortLicenseDigest =>
        _license?.Sha256 is { Length: >= 12 } digest ? digest[..12] : null;

    private bool IsDone(LegalDocumentType key) => _responded.Contains(key) || _acknowledged.Contains(key);

    private static string TitleOf(LegalDocumentType key) =>
        key == LegalDocumentType.License ? "Software License" : "Terms of Service";

    private static string IconOf(LegalDocumentType key) =>
        key == LegalDocumentType.License ? "gavel" : "description";

    private static string TitleId(LegalDocumentType key) => $"lg-ttl-{key}";

    private static string HintId(LegalDocumentType key) => $"lg-hint-{key}";

    private string? TextOf(LegalDocumentType key) =>
        key == LegalDocumentType.License ? _license?.Content : _terms?.Content;

    private string StepState(LegalDocumentType key, bool isCurrent)
    {
        if (IsDone(key))
        {
            return _acknowledged.Contains(key) ? "Acknowledged" : "Accepted";
        }

        if (!isCurrent)
        {
            return "Up next";
        }

        return _phase == Phase.Ready && TextOf(key) is not { Length: > 0 } ? "Continue" : "Review now";
    }

    protected override async Task OnInitializedAsync()
    {
        _returnUrl = ReadReturnUrl();

        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        await LoadAsync();
    }

    /// <summary>
    /// Resolve what is outstanding and load the text for it. The status call is authoritative about
    /// what is owed; the document reads are what the user actually responds to, so a failure in either
    /// puts the page in the retry state rather than letting someone accept text that never rendered.
    /// </summary>
    private async Task LoadAsync()
    {
        _phase = Phase.Loading;
        _respondError = null;
        StateHasChanged();

        var status = await Legal.GetStatusAsync();
        if (!status.IsSuccess || status.Value is not { } compliance)
        {
            _phase = Phase.Error;
            return;
        }

        _documents =
        [
            .. new[]
            {
                (Key: LegalDocumentType.License, Outstanding: !compliance.LicenseCompliant),
                (Key: LegalDocumentType.TermsOfService, Outstanding: !compliance.TosCompliant),
            }
            .Where(entry => entry.Outstanding)
            .Select(entry => entry.Key),
        ];

        // Nothing owed — the user reached the page directly, or another tab already responded.
        if (_documents.Count == 0)
        {
            NavigateOnward();
            return;
        }

        var license = await Legal.GetLicenseAsync();
        var terms = await Legal.GetCurrentTermsOfServiceAsync();

        if (!license.IsSuccess || !terms.IsSuccess)
        {
            _phase = Phase.Error;
            return;
        }

        _license = license.Value;
        _terms = terms.Value;
        _phase = Phase.Ready;
    }

    /// <summary>
    /// (Re)point the end-of-text observer at whichever document is on screen. Re-running per render is
    /// deliberate: advancing a step swaps the rendered text, and the sentinel that comes with it is a
    /// different element that has to be observed afresh.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        if (firstRender)
        {
            _selfRef = DotNetObjectReference.Create(this);
            _js = await JS.InvokeAsync<IJSObjectReference>("import", "./js/legal-doc.js");
        }

        if (_js is null || CurrentDocument is not { } key)
        {
            return;
        }

        var readable = _phase == Phase.Ready && TextOf(key) is { Length: > 0 };
        if (!readable || _observing == key)
        {
            return;
        }

        _observing = key;
        await _js.InvokeVoidAsync("observe", ObserverId, _scrollEnd, _selfRef);
    }

    private const string ObserverId = "accept-terms-doc";

    /// <summary>Called from legal-doc.js when the end of the current document scrolls into view.</summary>
    [JSInvokable]
    public Task OnReachedEnd()
    {
        if (CurrentDocument is { } key && _reachedEnd.Add(key))
        {
            return InvokeAsync(StateHasChanged);
        }

        return Task.CompletedTask;
    }

    private async Task AcceptAsync(LegalDocumentType key)
    {
        _busy = key;
        _respondError = null;
        _staleVersion = false;

        var result = await Legal.RespondAsync(new LegalDocumentResponse
        {
            DocumentType = key,
            Accepted = true,
            TosVersionId = key == LegalDocumentType.TermsOfService ? _terms?.Id : null,
        });

        _busy = null;

        if (result.IsSuccess)
        {
            _responded.Add(key);
            await AdvanceAsync();
            return;
        }

        // 409 — the version moved under us. Reload the current text and re-prompt rather than
        // recording consent to something the user never saw.
        if (result.Status == System.Net.HttpStatusCode.Conflict)
        {
            _staleVersion = true;
            _reachedEnd.Remove(key);
            _observing = null;
            await LoadAsync();
            return;
        }

        _respondError = result.Error;
    }

    /// <summary>A step with nothing to accept is acknowledged, not recorded — no response is sent.</summary>
    private async Task AcknowledgeAsync(LegalDocumentType key)
    {
        _acknowledged.Add(key);
        _observing = null;
        await AdvanceAsync();
    }

    /// <summary>
    /// Moving to the next document replaces the whole card in place, with no navigation for
    /// <c>FocusOnNavigate</c> to react to — so without this a screen-reader user is silently left on a
    /// stale reading position with a different document underneath. Announce the new step and put focus
    /// back on the heading, which is what a navigation would have done.
    /// </summary>
    private async Task AnnounceStepAsync(LegalDocumentType key)
    {
        var position = _documents.IndexOf(key) + 1;
        _announce = $"{TitleOf(key)}, step {position} of {_documents.Count}. Review and respond to continue.";
        StateHasChanged();

        try
        {
            await JS.InvokeVoidAsync("odsFocusById", "lg-title");
        }
        catch (Exception)
        {
            // Best-effort focus move; never block the flow on interop.
        }
    }

    /// <summary>
    /// Once every outstanding document is done, refresh the cached claims — the server has already
    /// re-run the claims factory via <c>RefreshSignInAsync</c>, so this is what stops the client's own
    /// gate from bouncing the user straight back here — then forward to where they were going.
    /// </summary>
    private async Task AdvanceAsync()
    {
        _observing = null;

        if (!_documents.All(IsDone))
        {
            StateHasChanged();
            if (CurrentDocument is { } next)
            {
                await AnnounceStepAsync(next);
            }

            return;
        }

        _outcome = Outcome.Accepted;
        StateHasChanged();

        if (AuthStateProvider is CookieAuthenticationStateProvider cookieAuth)
        {
            await cookieAuth.RefreshAsync();
        }

        NavigateOnward();
    }

    private void RequestDecline(LegalDocumentType key) => _declineTarget = key;

    private void CancelDecline() => _declineTarget = null;

    /// <summary>
    /// A decline is recorded and the server ends the session; the client drops its cached auth state so
    /// nothing keeps rendering as signed in, and lands on Login with the reason shown.
    /// </summary>
    private async Task ConfirmDeclineAsync()
    {
        if (_declineTarget is not { } key)
        {
            return;
        }

        _declining = true;
        _respondError = null;

        var result = await Legal.RespondAsync(new LegalDocumentResponse
        {
            DocumentType = key,
            Accepted = false,
            TosVersionId = key == LegalDocumentType.TermsOfService ? _terms?.Id : null,
        });

        _declining = false;
        _declineTarget = null;

        if (!result.IsSuccess)
        {
            _respondError = result.Error;
            return;
        }

        _outcome = Outcome.Declined;
        StateHasChanged();

        if (AuthStateProvider is CookieAuthenticationStateProvider cookieAuth)
        {
            await cookieAuth.RefreshAsync();
        }

        NavigationManager.NavigateTo("/login?reason=legal-declined");
    }

    private void NavigateOnward() =>
        NavigationManager.NavigateTo(_returnUrl ?? "/");

    /// <summary>
    /// Reads <c>returnUrl</c>, validated by <see cref="LocalReturnUrl"/> — no off-site redirect, and
    /// never this page itself (which would loop the gate).
    /// </summary>
    private string? ReadReturnUrl() =>
        LocalReturnUrl.FromQuery(NavigationManager.Uri, LegalComplianceHandler.InterstitialPath);

    public async ValueTask DisposeAsync()
    {
        if (_js is not null)
        {
            try
            {
                await _js.InvokeVoidAsync("unobserve", ObserverId);
                await _js.DisposeAsync();
            }
            catch (Exception)
            {
                // Circuit already gone; nothing to clean up.
            }
        }

        _selfRef?.Dispose();
    }
}
