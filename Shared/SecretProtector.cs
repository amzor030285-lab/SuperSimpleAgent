using System;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Protects secrets kept in the settings INI with Windows DPAPI (XP and newer).
/// A protected value is stored as "dpapi:&lt;base64&gt;". A value without the prefix is
/// left alone, so existing plaintext configs keep working. A protected value that can no
/// longer be decrypted (different Windows user, reset domain password, reinstalled OS -
/// the master key is gone then) is returned unchanged instead of throwing, so the key is
/// visible in the options UI and can simply be typed in again.
/// </summary>
public static class SecretProtector
{
    public const string Prefix = "dpapi:";

    public static bool IsProtected(string stored)
    {
        return !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string Protect(string plaintext)
    {
        if (plaintext == null || plaintext.Length == 0 || IsProtected(plaintext))
            return plaintext ?? string.Empty;

        byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(cipher);
    }

    public static string Unprotect(string stored)
    {
        if (!IsProtected(stored))
            return stored ?? string.Empty;

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(stored.Substring(Prefix.Length)), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            return stored;
        }
    }

    public static bool IsSecretKey(string configKey)
    {
        return string.Equals(configKey, "apikey", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configKey, "embeddingsapikey", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configKey, "firecrawlapikey", StringComparison.OrdinalIgnoreCase);
    }
}
