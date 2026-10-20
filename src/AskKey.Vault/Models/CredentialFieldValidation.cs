using System.Text;
using System.Text.RegularExpressions;

namespace AskKey.Vault;

public static class CredentialFieldValidation
{
    public const int MaximumFieldBytes = 4 * 1024;

    public static void UsageInstructions(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaximumFieldBytes)
        {
            throw new VaultException(VaultErrorCode.DatabaseError,
                "Usage instructions must be at most 4096 bytes.", "usageInstructionsTooLong");
        }
    }

    public static void EnvironmentVariable(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaximumFieldBytes)
        {
            throw new VaultException(VaultErrorCode.DatabaseError,
                "Environment variable mappings must be at most 4096 bytes.", "environmentVariableTooLong");
        }
        VaultNameRules.RequireSecretName(value);
    }

    public static string? OptionalEnvironmentVariable(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        EnvironmentVariable(trimmed);
        return trimmed;
    }
}

public static partial class VaultNameRules
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretNamePattern();

    public static void RequireSecretName(string name)
    {
        if (!IsSecretName(name)) throw VaultException.InvalidSecretName(name);
    }

    public static bool IsSecretName(string name)
        => Encoding.UTF8.GetByteCount(name) <= CredentialFieldValidation.MaximumFieldBytes
            && SecretNamePattern().IsMatch(name);
}


