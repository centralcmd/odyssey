using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Odyssey.Context.Secrets;

/// <summary>
/// The protect/unprotect seam over ASP.NET Core Data Protection (issue #444 §5), usable from both
/// <c>Odyssey.Api</c> and <c>Odyssey.MigrationService</c>.
///
/// <para>
/// <strong>Purposes are per key.</strong> The sub-purpose means ciphertext lifted from one key's row
/// and written into another's — by anyone with direct database write access — fails to unprotect
/// instead of silently taking effect. Cheap, and it closes a real substitution path.
/// </para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/> under <paramref name="key"/>'s sub-purpose.</summary>
    string Protect(string key, string plaintext);

    /// <summary>
    /// Decrypts, or returns <see langword="null"/> when the payload cannot be unprotected. Never
    /// throws on row content: the exception carries no information a caller may act on, and for some
    /// providers its message embeds payload fragments.
    /// </summary>
    string? Unprotect(string key, string ciphertext);

    /// <summary>
    /// Answers "would this unprotect?" <strong>without ever materialising the plaintext as a managed
    /// <see cref="string"/></strong> (§11). The admin status endpoint probes every stored credential
    /// on each page load, and a string-returning probe would make that page a periodic amplifier of
    /// plaintext-in-heap — unpinned, non-zeroable and subject to GC copying. The byte buffer this uses
    /// instead is zeroed before it returns.
    /// </summary>
    bool CanUnprotect(string key, string ciphertext);
}

/// <inheritdoc cref="ISecretProtector" />
public sealed class SecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    /// <summary>
    /// The root purpose. Versioned, and paired with <c>SystemSettingSecret.ProtectionScheme</c>: a
    /// change here is a change of scheme, so stored rows stay identifiable rather than silently
    /// unreadable.
    /// </summary>
    public const string RootPurpose = "Odyssey.SystemSettings.Secret.v1";

    public string Protect(string key, string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return Base64Url.EncodeToString(Protector(key).Protect(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string? Unprotect(string key, string ciphertext)
    {
        if (!TryDecode(ciphertext, out var payload))
        {
            return null;
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = Protector(key).Unprotect(payload);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public bool CanUnprotect(string key, string ciphertext)
    {
        if (!TryDecode(ciphertext, out var payload))
        {
            return false;
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = Protector(key).Unprotect(payload);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private IDataProtector Protector(string key) => provider.CreateProtector(RootPurpose, key);

    private static bool TryDecode(string ciphertext, out byte[] payload)
    {
        payload = [];
        if (string.IsNullOrEmpty(ciphertext))
        {
            return false;
        }

        try
        {
            payload = Base64Url.DecodeFromChars(ciphertext);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
