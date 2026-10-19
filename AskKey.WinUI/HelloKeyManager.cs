using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace AskKeyApp;

/// <summary>
/// Thin wrapper over the Windows Hello key credential used by the guard service.
/// The private key stays in the Hello container; the app only receives the public
/// key and per-challenge signatures.
/// </summary>
internal static class HelloKeyManager
{
    public static async Task<bool> IsSupportedAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch { return false; }
    }

    public static async Task<(bool Ok, string? Error, byte[]? PublicKey)> CreateAsync(string name)
    {
        try
        {
            var result = await KeyCredentialManager.RequestCreateAsync(
                name, KeyCredentialCreationOption.ReplaceExisting);
            if (result.Status != KeyCredentialStatus.Success)
                return (false, MessageFor(result.Status), null);
            var publicKey = result.Credential.RetrievePublicKey();
            CryptographicBuffer.CopyToByteArray(publicKey, out var bytes);
            return bytes is { Length: > 0 }
                ? (true, null, bytes)
                : (false, "Windows Hello returned an empty public key.", null);
        }
        catch (Exception error)
        {
            return (false, error.Message, null);
        }
    }

    public static async Task<(bool Ok, string? Error, byte[]? Signature)> SignAsync(
        string name, byte[] challenge)
    {
        try
        {
            var opened = await KeyCredentialManager.OpenAsync(name);
            if (opened.Status != KeyCredentialStatus.Success)
                return (false, MessageFor(opened.Status), null);
            var input = CryptographicBuffer.CreateFromByteArray(challenge);
            var result = await opened.Credential.RequestSignAsync(input);
            if (result.Status != KeyCredentialStatus.Success)
                return (false, MessageFor(result.Status), null);
            CryptographicBuffer.CopyToByteArray(result.Result, out var signature);
            return signature is { Length: > 0 }
                ? (true, null, signature)
                : (false, "Windows Hello returned an empty signature.", null);
        }
        catch (Exception error)
        {
            return (false, error.Message, null);
        }
    }

    public static async Task DeleteAsync(string name)
    {
        try { await KeyCredentialManager.DeleteAsync(name); }
        catch { }
    }

    private static string MessageFor(KeyCredentialStatus status) => status switch
    {
        KeyCredentialStatus.UserCanceled => "Windows Hello was canceled.",
        KeyCredentialStatus.NotFound => "The Windows Hello key for Ask Key was not found. Re-enable Windows Hello unlock.",
        KeyCredentialStatus.UserPrefersPassword => "Windows Hello is not available for this account.",
        KeyCredentialStatus.CredentialAlreadyExists => "The Windows Hello key already exists.",
        _ => $"Windows Hello failed: {status}.",
    };
}
