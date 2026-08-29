using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;

namespace Odyssey.Client.Pages;

public partial class AccountTwoFactorSection
{
    /// <summary>
    /// The account's two-factor status as the page knows it. The page loads it once and re-reads it
    /// from <see cref="StatusChanged"/>, because its header chips and Security overview card show the
    /// same facts — and they must stay right even while this section is filtered out by the search.
    /// </summary>
    /// <param name="Enabled">Whether two-factor is on.</param>
    /// <param name="RecoveryCodesRemaining">How many unused recovery codes are on file.</param>
    /// <param name="SharedKey">The pending base32 authenticator secret Identity handed back.</param>
    public sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesRemaining, string SharedKey);

    /// <summary>The sign-in email — the account label baked into the otpauth:// URI.</summary>
    [Parameter] public string Email { get; set; } = string.Empty;

    /// <summary>Current status, owned by the page.</summary>
    [Parameter, EditorRequired] public TwoFactorStatus Status { get; set; } = new(false, 0, string.Empty);

    /// <summary>Raised after any change so the page can re-render the header and overview.</summary>
    [Parameter] public EventCallback<TwoFactorStatus> StatusChanged { get; set; }

    private enum Phase { Idle, Setup, Codes, Enabled, CodesRegen }

    private static readonly string[] StepLabels = ["Scan", "Verify", "Save codes"];

    private Phase _phase = Phase.Idle;
    private bool _seeded;
    private List<string> _recoveryCodes = [];
    private bool _showKey;
    private string _code = string.Empty;
    private bool _codeError;
    private string? _confirm;
    private string _disablePhrase = string.Empty;
    private bool _codesCopied;
    private bool _busy;

    // The QR is a PNG data URI of the otpauth:// URI, rebuilt whenever the shared key or email moves.
    private string _qrDataUri = string.Empty;
    private string _qrBasis = string.Empty;

    protected override void OnParametersSet()
    {
        // The landing phase follows the status only on the first pass; after that the wizard drives it.
        if (!_seeded)
        {
            _seeded = true;
            _phase = Status.Enabled ? Phase.Enabled : Phase.Idle;
        }

        RefreshQr();
    }

    // The shared key shown for manual entry, grouped into 4-char blocks for readability.
    private string FormattedKey =>
        string.Join(" ", Regex.Split(Status.SharedKey, "(?<=\\G.{4})").Where(s => s.Length > 0));

    private static string BuildAuthenticatorUri(string email, string unformattedKey)
    {
        const string issuer = "Odyssey";
        return $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}"
             + $"?secret={unformattedKey}&issuer={Uri.EscapeDataString(issuer)}&digits=6";
    }

    private static string BuildQrPngDataUri(string content)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCoder.QRCodeGenerator.ECCLevel.Q);
        using var png = new QRCoder.PngByteQRCode(data);
        return $"data:image/png;base64,{Convert.ToBase64String(png.GetGraphic(6))}";
    }

    private void RefreshQr()
    {
        var basis = $"{Email}|{Status.SharedKey}";
        if (basis == _qrBasis)
            return;

        _qrBasis = basis;
        _qrDataUri = string.IsNullOrEmpty(Status.SharedKey) || string.IsNullOrEmpty(Email)
            ? string.Empty
            : BuildQrPngDataUri(BuildAuthenticatorUri(Email, Status.SharedKey));
    }

    private Task PublishAsync(bool enabled, int codesRemaining, string sharedKey)
    {
        Status = new TwoFactorStatus(enabled, codesRemaining, sharedKey);
        RefreshQr();
        return StatusChanged.InvokeAsync(Status);
    }

    // ── Setup wizard ─────────────────────────────────────────────────────────
    private async Task StartSetup()
    {
        if (string.IsNullOrEmpty(Status.SharedKey)
            && await AuthApiClient.GetTwoFactorStatusAsync() is { } status)
        {
            await PublishAsync(status.IsTwoFactorEnabled, status.RecoveryCodesLeft, status.SharedKey ?? string.Empty);
        }

        _phase = Phase.Setup;
        _showKey = false;
        _code = string.Empty;
        _codeError = false;
    }

    private void CancelSetup()
    {
        _phase = Status.Enabled ? Phase.Enabled : Phase.Idle;
        _code = string.Empty;
        _codeError = false;
    }

    private async Task VerifyCode()
    {
        var code = new string(_code.Where(char.IsDigit).ToArray());
        if (code.Length != 6)
        {
            _codeError = true;
            return;
        }

        _busy = true;
        _codeError = false;
        var status = await AuthApiClient.EnableTwoFactorAsync(code);
        _busy = false;

        if (status is null)
        {
            // Verification failed — keep the user on the verify step (spec §11: InvalidCode).
            _codeError = true;
            _code = string.Empty;
            return;
        }

        _code = string.Empty;
        await PublishAsync(true, status.RecoveryCodesLeft, Status.SharedKey);

        // EnableTwoFactorAsync always regenerates the codes (resetRecoveryCodes), so the user
        // is shown a fresh set even when re-enabling after a reset/disable — without that,
        // Identity returns none when old codes are still on file and setup would finish here
        // without ever displaying fallback codes (lock-out risk).
        if (status.RecoveryCodes is { Length: > 0 })
        {
            _recoveryCodes = [.. status.RecoveryCodes];
            _phase = Phase.Codes;
        }
        else
        {
            // Defensive only: 2FA is on but no codes came back. Don't strand the user without
            // a fallback — send them to the recovery-codes action instead of silent success.
            _phase = Phase.Enabled;
            Snackbar.Add("Two-factor is on. Generate recovery codes from the security section below.", Severity.Warning);
        }
    }

    private void FinishEnable() => _phase = Phase.Enabled;

    // ── Recovery codes + danger zone ─────────────────────────────────────────
    private async Task RegenerateCodes()
    {
        _busy = true;
        var status = await AuthApiClient.RegenerateRecoveryCodesAsync();
        _busy = false;
        _confirm = null;

        if (status is null)
        {
            Snackbar.Add("Couldn't generate new recovery codes.", Severity.Error);
            return;
        }

        _recoveryCodes = [.. status.RecoveryCodes ?? []];
        await PublishAsync(Status.Enabled, status.RecoveryCodesLeft, Status.SharedKey);
        _phase = Phase.CodesRegen;
    }

    private async Task ResetKey()
    {
        _confirm = null;
        _busy = true;
        var status = await AuthApiClient.ResetTwoFactorKeyAsync();
        _busy = false;

        if (status is null)
        {
            Snackbar.Add("Couldn't reset the authenticator key.", Severity.Error);
            return;
        }

        // Resetting the key also turns 2FA off; walk the user back through setup.
        _recoveryCodes = [];
        await PublishAsync(false, status.RecoveryCodesLeft, status.SharedKey ?? string.Empty);
        await StartSetup();
    }

    private async Task DisableTwoFactor()
    {
        _busy = true;
        var disabled = await AuthApiClient.DisableTwoFactorAsync();
        _busy = false;
        _confirm = null;
        _disablePhrase = string.Empty;

        if (!disabled)
        {
            Snackbar.Add("Couldn't turn off two-factor authentication.", Severity.Error);
            return;
        }

        _recoveryCodes = [];
        await PublishAsync(false, 0, Status.SharedKey);
        _phase = Phase.Idle;
    }

    private Task CopySharedKey() => Clipboard.CopyAsync(Status.SharedKey, "Setup key copied.");

    private async Task CopyCodes()
    {
        // Only flip the button to "Copied" when the copy actually succeeded; on
        // failure CopyAsync toasts the error and the codes stay on screen.
        if (!await Clipboard.CopyAsync(string.Join("\n", _recoveryCodes), "Recovery codes copied."))
            return;

        _codesCopied = true;
        StateHasChanged();
        await Task.Delay(OdsTiming.ConfirmFlashMs);
        _codesCopied = false;
    }
}
