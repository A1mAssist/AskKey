namespace AskKey.Broker.Transport;

/// <summary>Server-side pipe-name generation: a per-process random suffix keeps the
/// name unguessable while the runtime directory stores the exact value.</summary>
public static class BrokerPipeName
{
    public const string Prefix = "askkey-broker-";

    public static string Create() => Prefix + global::System.Convert.ToHexStringLower(
        global::System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    /// <summary>Named pipes are limited to 256 characters on the local machine.</summary>
    public static bool IsValid(string name)
        => name.Length is > 0 and <= 200
            && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
