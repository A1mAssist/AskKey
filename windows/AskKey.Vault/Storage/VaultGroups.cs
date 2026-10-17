using System.Text.Json;

namespace AskKey.Vault;

public static class VaultGroups
{
    public const string ConfigKey = "credential_groups";

    public static IReadOnlyList<string> StoredGroups(string? value, byte[] key)
    {
        if (value is null) return Array.Empty<string>();
        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw VaultException.Database("Credential groups are invalid.", exception);
        }
        var plaintext = VaultCrypto.DecryptData(encrypted, key);
        try
        {
            return JsonSerializer.Deserialize<List<string>>(plaintext) ?? new List<string>();
        }
        catch (JsonException exception)
        {
            throw VaultException.Database("Credential groups are invalid.", exception);
        }
    }
}
