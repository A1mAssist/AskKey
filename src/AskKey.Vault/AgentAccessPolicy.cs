using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace AskKey.Vault;

/// <summary>
/// Agent-access and security-setting policy, authenticated with a key derived from the
/// vault key under its own label. Pause state and the settings that decide how much
/// authentication an Agent request gets cannot live in the user-writable database or in
/// settings.json: either could be edited to bypass the confirmation the UI requires.
///
/// A missing, unreadable or unverifiable policy reads as paused. Only a first launch on a
/// brand-new library writes defaults without one.
/// </summary>
public sealed class AgentAccessPolicy
{
    /// <summary>Bumped when the record layout changes so old records fail closed.</summary>
    public const int CurrentVersion = 1;

    public const int FallbackTimedAllowanceMinutes = 30;
    public const int DefaultSessionTimeoutSeconds = 300;

    // Settings offers 5/15/30/60 minutes. Anything longer is not a value the UI can
    // produce, so a hand-edited record falls back to the strict default instead.
    public const int MaximumTimedAllowanceMinutes = 60;
    public const int MaximumSessionTimeoutSeconds = 60 * 60;

    public const string ConfigKey = "agent_access_policy";
    private static readonly byte[] PolicyInfo = Encoding.UTF8.GetBytes("AskKey agent access policy v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>True when the record was authenticated and every value was in range.</summary>
    public bool AgentAccessPaused { get; set; }
    public bool ReadApprovalAuthenticationEnabled { get; set; } = true;
    public bool TimedAllowanceEnabled { get; set; } = true;
    public int DefaultTimedAllowanceMinutes { get; set; } = FallbackTimedAllowanceMinutes;
    public int SessionTimeoutSeconds { get; set; } = DefaultSessionTimeoutSeconds;

    public static AgentAccessPolicy StrictDefaults() => new()
    {
        // Failing closed means an unverifiable record pauses Agent access rather than
        // granting it. Everything else falls back to the most protective default.
        AgentAccessPaused = true,
        ReadApprovalAuthenticationEnabled = true,
        TimedAllowanceEnabled = true,
        DefaultTimedAllowanceMinutes = 5,
        SessionTimeoutSeconds = 5 * 60,
    };

    public static AgentAccessPolicy NewLibraryDefaults() => new()
    {
        AgentAccessPaused = false,
        ReadApprovalAuthenticationEnabled = true,
        TimedAllowanceEnabled = true,
        DefaultTimedAllowanceMinutes = FallbackTimedAllowanceMinutes,
        SessionTimeoutSeconds = DefaultSessionTimeoutSeconds,
    };

    /// <summary>
    /// Reads the policy for a store, or the strict defaults when there is none that
    /// authenticates. <paramref name="isNewLibrary"/> lets the very first launch write
    /// defaults without a previous record; everything else must already have one.
    /// </summary>
    public static AgentAccessPolicy Load(
        Func<string, string?> readConfig,
        Action<string, string?> writeConfig,
        ReadOnlySpan<byte> vaultKey,
        bool isNewLibrary)
    {
        var stored = readConfig(ConfigKey);
        if (stored is null)
        {
            // No record yet. Only a brand-new library may proceed on defaults, and only
            // by writing the record that makes the next load verifiable.
            if (!isNewLibrary) return StrictDefaults();
            var created = NewLibraryDefaults();
            TryWrite(created, writeConfig, vaultKey);
            return created;
        }

        try
        {
            var plaintext = VaultCrypto.Decrypt(Convert.FromBase64String(stored), PolicyKey(vaultKey));
            var policy = JsonSerializer.Deserialize<AgentAccessPolicy>(plaintext, JsonOptions);
            if (policy is null) return StrictDefaults();
            if (!IsInRange(policy)) return StrictDefaults();
            return policy;
        }
        // VaultCrypto.Decrypt wraps any AEAD failure in VaultException, so that is what a
        // tampered or foreign-key record surfaces as.
        catch (Exception e) when (e is VaultException or FormatException
                                      or System.Text.Json.JsonException or CryptographicException
                                      or AuthenticationTagMismatchException or ArgumentException)
        {
            // Tampered, truncated or written under a different vault key.
            return StrictDefaults();
        }
    }

    /// <summary>Encrypts and stores the policy under its own derived key and label.</summary>
    public static bool TryWrite(AgentAccessPolicy policy, Action<string, string?> writeConfig,
        ReadOnlySpan<byte> vaultKey)
    {
        try
        {
            var plaintext = JsonSerializer.Serialize(policy, JsonOptions);
            writeConfig(ConfigKey, Convert.ToBase64String(VaultCrypto.Encrypt(plaintext, PolicyKey(vaultKey))));
            return true;
        }
        catch (Exception e) when (e is CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }

    private static byte[] PolicyKey(ReadOnlySpan<byte> vaultKey)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultKey.ToArray(), 32, Array.Empty<byte>(), PolicyInfo);

    private static bool IsInRange(AgentAccessPolicy policy)
        => policy.DefaultTimedAllowanceMinutes is >= 1 and <= MaximumTimedAllowanceMinutes
            && policy.SessionTimeoutSeconds is >= 60 and <= MaximumSessionTimeoutSeconds;

    public AgentAccessPolicy Copy() => new()
    {
        AgentAccessPaused = AgentAccessPaused,
        ReadApprovalAuthenticationEnabled = ReadApprovalAuthenticationEnabled,
        TimedAllowanceEnabled = TimedAllowanceEnabled,
        DefaultTimedAllowanceMinutes = DefaultTimedAllowanceMinutes,
        SessionTimeoutSeconds = SessionTimeoutSeconds,
    };
}