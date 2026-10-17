using System.Globalization;
using System.Text;

namespace AskKey.Vault;

public enum CredentialPermission
{
    Allowed,
    Ask,
    Hidden,
}

public enum CredentialPayloadKind
{
    Text,
    File,
    Bundle,
}

public static class CredentialPermissionExtensions
{
    public static string WireValue(this CredentialPermission permission) => permission switch
    {
        CredentialPermission.Allowed => "allowed",
        CredentialPermission.Ask => "ask",
        _ => "hidden",
    };

    public static string ManagementLabel(this CredentialPermission permission) => permission switch
    {
        CredentialPermission.Allowed => CredentialManagementCopy.Allowed,
        CredentialPermission.Ask => CredentialManagementCopy.Ask,
        _ => CredentialManagementCopy.Hidden,
    };

    public static bool TryParse(string value, out CredentialPermission permission)
    {
        switch (value)
        {
            case "allowed": permission = CredentialPermission.Allowed; return true;
            case "ask": permission = CredentialPermission.Ask; return true;
            case "hidden": permission = CredentialPermission.Hidden; return true;
            default: permission = CredentialPermission.Ask; return false;
        }
    }
}

public static class CredentialPayloadKindExtensions
{
    public static string WireValue(this CredentialPayloadKind kind) => kind switch
    {
        CredentialPayloadKind.Text => "text",
        CredentialPayloadKind.File => "file",
        _ => "bundle",
    };

    public static bool TryParse(string value, out CredentialPayloadKind kind)
    {
        switch (value)
        {
            case "text": kind = CredentialPayloadKind.Text; return true;
            case "file": kind = CredentialPayloadKind.File; return true;
            case "bundle": kind = CredentialPayloadKind.Bundle; return true;
            default: kind = CredentialPayloadKind.Text; return false;
        }
    }
}

public static class CredentialManagementCopy
{
    public const string Credential = "Credential";
    public const string CredentialGroup = "Credential group";
    public const string Ungrouped = "Ungrouped";
    public const string Ask = "Ask";
    public const string Allowed = "Allow";
    public const string Hidden = "Hidden";
    public const string ManageReason = "Confirm credential management";
    public const string RevealReason = "Reveal credential value";
    public const string PauseReason = "Pause Agent access";
    public const string ResumeReason = "Resume Agent access";
    public const string UsageInstructions = "Usage instructions";
    public const string PrivateNotes = "Private notes";
    public const string EnvironmentVariable = "Environment variable mapping";
    public const string Expires = "Expires";
    public const string Text = "Text";
    public const string File = "File";
    public const string OriginalFilename = "Original filename";
    public const string FileSize = "Size";
    public const string ContentDigest = "Digest";
    public const string ChooseFile = "Choose file";
    public const string ReplaceFile = "Replace file";
}

public sealed class ManagementAuthenticator
{
    private readonly Func<string, bool> _confirm;

    public ManagementAuthenticator(Func<string, bool> confirm) => _confirm = confirm;

    public bool Confirm(string reason) => _confirm(reason);

    public static ManagementAuthenticator Allow { get; } = new(_ => true);
    public static ManagementAuthenticator Deny { get; } = new(_ => false);
}

public sealed class TextCredentialInput
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string UsageInstructions { get; init; } = "";
    public string PrivateNotes { get; init; } = "";
    public string? GroupName { get; init; }
    public string? EnvironmentVariable { get; init; }
    public CredentialPermission Permission { get; init; } = CredentialPermission.Ask;
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed class ManagedTextCredential
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Value { get; init; }
    public required string UsageInstructions { get; init; }
    public string? PrivateNotes { get; init; }
    public string? GroupName { get; init; }
    public string? EnvironmentVariable { get; init; }
    public CredentialPermission Permission { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public CredentialPayloadKind PayloadKind { get; init; }
    public string? OriginalFilename { get; init; }
    public int? ByteSize { get; init; }
    public string? ContentDigest { get; init; }
    public byte[]? FileBytes { get; init; }
    public IReadOnlyList<ManagedCredentialComponent> Components { get; init; } = Array.Empty<ManagedCredentialComponent>();
    public DateTimeOffset? DeletedAt { get; init; }

    public bool IsUngrouped => GroupName is null;
}

public sealed class ManagedCredentialComponent
{
    public required string Name { get; init; }
    public CredentialPayloadKind Kind { get; init; }
    public CredentialComponentValue? Value { get; init; }
    public required CredentialComponentDelivery Delivery { get; init; }
    public bool Masked { get; init; } = true;
}

public abstract record CredentialComponentValue
{
    public sealed record Text(string Value) : CredentialComponentValue;
    public sealed record File(string Filename, byte[] Bytes) : CredentialComponentValue;

    public CredentialPayloadKind PayloadKind => this is File ? CredentialPayloadKind.File : CredentialPayloadKind.Text;
}

public enum CredentialDeliveryKind
{
    EnvironmentVariable,
    TemporaryFile,
    None,
}

public sealed record CredentialComponentDelivery(CredentialDeliveryKind Kind, string? Variable)
{
    public static CredentialComponentDelivery EnvironmentVariable(string name) => new(CredentialDeliveryKind.EnvironmentVariable, name);
    public static CredentialComponentDelivery TemporaryFile(string name) => new(CredentialDeliveryKind.TemporaryFile, name);
    public static CredentialComponentDelivery None { get; } = new(CredentialDeliveryKind.None, null);

    public string? EnvironmentVariableName => Kind == CredentialDeliveryKind.None ? null : Variable;
}

public sealed class CredentialComponentInput
{
    public string Name { get; init; } = "";
    public CredentialComponentValue Value { get; init; } = new CredentialComponentValue.Text("");
    public CredentialComponentDelivery Delivery { get; init; } = CredentialComponentDelivery.None;
    public bool Masked { get; init; } = true;

    public CredentialComponentInput() { }

    public CredentialComponentInput(string name, CredentialComponentValue value, bool masked = true)
        : this(name, value, DefaultDelivery(name, value), masked) { }

    public CredentialComponentInput(string name, CredentialComponentValue value, CredentialComponentDelivery delivery,
        bool masked = true)
    {
        Name = name;
        Value = value;
        Delivery = delivery;
        Masked = masked;
    }

    private static CredentialComponentDelivery DefaultDelivery(string name, CredentialComponentValue value)
        => value is CredentialComponentValue.File
            ? CredentialComponentDelivery.TemporaryFile(name)
            : CredentialComponentDelivery.EnvironmentVariable(name);
}

public sealed class BundleCredentialInput
{
    public required string Name { get; init; }
    public required IReadOnlyList<CredentialComponentInput> Components { get; init; }
    public string UsageInstructions { get; init; } = "";
    public string PrivateNotes { get; init; } = "";
    public string? GroupName { get; init; }
    public CredentialPermission Permission { get; init; } = CredentialPermission.Ask;
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed class FileCredentialInput
{
    public required string Name { get; init; }
    public FileImport.FrozenFile? Snapshot { get; init; }
    public string UsageInstructions { get; init; } = "";
    public string PrivateNotes { get; init; } = "";
    public string? GroupName { get; init; }
    public string? EnvironmentVariable { get; init; }
    public CredentialPermission Permission { get; init; } = CredentialPermission.Ask;
    public DateTimeOffset? ExpiresAt { get; init; }
}

public static class CredentialBundleValidator
{
    public const int MaximumComponents = 64;
    public const int MaximumMaterialBytes = 5 * 1024 * 1024;

    public static IReadOnlyList<CredentialComponentInput> ValidatedComponents(IReadOnlyList<CredentialComponentInput> components)
    {
        if (components.Count is < 1 or > MaximumComponents)
        {
            throw VaultException.Database("A credential must contain between 1 and 64 components.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var environmentNames = new HashSet<string>(StringComparer.Ordinal);
        var materialBytes = 0;
        var result = new List<CredentialComponentInput>(components.Count);
        foreach (var component in components)
        {
            var name = CredentialName.DisplayName(component.Name);
            if (!names.Add(CredentialName.Normalized(name)))
            {
                throw VaultException.Database("Credential component names must be unique.");
            }
            CredentialComponentValue value;
            int byteCount;
            switch (component.Value)
            {
                case CredentialComponentValue.Text text:
                    value = text;
                    byteCount = Encoding.UTF8.GetByteCount(text.Value);
                    break;
                case CredentialComponentValue.File file:
                    var frozen = new FileImport.FrozenFile(file.Filename, file.Bytes);
                    value = new CredentialComponentValue.File(frozen.OriginalFilename, frozen.Bytes);
                    byteCount = file.Bytes.Length;
                    break;
                default:
                    throw VaultException.Database("Credential has an unknown payload kind.");
            }
            if (byteCount > MaximumMaterialBytes - materialBytes)
            {
                throw VaultException.Database("Credential material exceeds the 5 MiB size limit.");
            }
            materialBytes += byteCount;
            if (component.Delivery.EnvironmentVariableName is { } variable)
            {
                CredentialFieldValidation.EnvironmentVariable(variable);
                if (!environmentNames.Add(variable))
                {
                    throw VaultException.Database("Credential delivery mappings must use unique environment variables.");
                }
            }
            if (component.Delivery.Kind == CredentialDeliveryKind.EnvironmentVariable
                && value is CredentialComponentValue.File)
            {
                throw VaultException.Database("File components must use a temporary file mapping or remain undelivered.");
            }
            result.Add(new CredentialComponentInput(name, value, component.Delivery, component.Masked));
        }
        return result;
    }
}

public static class CredentialName
{
    public const int MaximumLength = 255;

    public static string DisplayName(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0
            || GraphemeCount(trimmed) > MaximumLength
            || Encoding.UTF8.GetByteCount(trimmed) > BrokerLimitsFieldBytes
            || trimmed.Any(char.IsControl))
        {
            throw VaultException.InvalidCredentialName(raw);
        }
        return trimmed;
    }

    public static string Normalized(string displayName)
        => displayName.Normalize(NormalizationForm.FormC).ToLowerInvariant().Normalize(NormalizationForm.FormC);

    public static string? OptionalDisplayName(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : DisplayName(trimmed);
    }

    /// <summary>Swift counts Character (grapheme clusters); the limit is not UTF-16 length.</summary>
    private static int GraphemeCount(string value)
    {
        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext()) count++;
        return count;
    }

    internal const int BrokerLimitsFieldBytes = 4 * 1024;
}

public static class CredentialIndex
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("AskKey vNext name index v1");

    public static byte[] Hash(string normalizedName, ReadOnlySpan<byte> vaultKey)
    {
        var indexKey = System.Security.Cryptography.HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, vaultKey.ToArray(), 32,
            Array.Empty<byte>(), Info);
        using var hmac = new System.Security.Cryptography.HMACSHA256(indexKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(normalizedName));
    }
}



/// <summary>Caller-provided display context. Never an authorization input.</summary>
public sealed record BrokerCallerClaim(string? Name = null, string? Path = null, string? Signature = null);

public enum AgentCredentialOperation
{
    Read,
    Modify,
    Delete,
}

public enum AgentCredentialAuthorization
{
    Allowed,
    RequiresApproval,
}
