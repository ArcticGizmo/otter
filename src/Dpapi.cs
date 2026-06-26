using System.Security.Cryptography;
using System.Text;

namespace Otter;

/// <summary>
/// Encrypts secrets at rest with Windows DPAPI (<see cref="DataProtectionScope.CurrentUser"/>), binding
/// the ciphertext to the current Windows account. A <c>config.json</c> copied to another machine — or
/// read by another user on this one — yields only ciphertext that can't be opened. This does NOT defend
/// against malware already running as the user (it can call DPAPI itself); it removes the file-theft and
/// plaintext-on-disk exposure, which is the bulk of the realistic risk for a backend-less desktop app.
/// </summary>
static class Dpapi
{
    // On-disk marker so we can tell DPAPI ciphertext (this scheme) apart from a legacy plaintext token.
    // Stored form: "DPAPI:" + base64(CryptProtectData(utf8(plaintext), CurrentUser)).
    const string Prefix = "DPAPI:";

    /// <summary>Encrypts <paramref name="plaintext"/> for the current user. Empty in → empty out.</summary>
    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(cipher);
    }

    /// <summary>
    /// Resolves an on-disk token value to its plaintext. Sets <paramref name="needsResave"/> when the
    /// stored form should be rewritten: a legacy plaintext value (kept, to be encrypted on next save) or
    /// ciphertext we can't open (dropped, so the app falls back to "Not connected" and the dead value is
    /// cleared). An empty value is "no token" — neither plaintext nor in need of a rewrite.
    /// </summary>
    public static string Resolve(string? stored, out bool needsResave)
    {
        needsResave = false;
        if (string.IsNullOrEmpty(stored)) return "";

        if (stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try
            {
                var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
                var bytes  = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                // Ciphertext we can't decrypt — config copied from another user/machine, or corrupt.
                // Drop it; rewriting clears the unusable value.
                needsResave = true;
                return "";
            }
        }

        // No marker → legacy plaintext token. Keep it; it'll be encrypted on the re-save.
        needsResave = true;
        return stored;
    }
}
