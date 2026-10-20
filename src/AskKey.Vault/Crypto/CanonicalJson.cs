using System.Globalization;
using System.Text;

namespace AskKey.Vault;

/// <summary>
/// Swift's JSONEncoder(.sortedKeys, .withoutEscapingSlashes) for CredentialRecord:
/// snake_case keys sorted ordinally, nil optionals omitted, Data as base64, UTF-8 text unescaped.
/// </summary>
internal static class CanonicalJson
{
    public static byte[] CredentialRecord(CredentialRecord record)
    {
        var fields = new SortedDictionary<string, Action<StringBuilder>>(StringComparer.Ordinal)
        {
            ["id"] = b => String(b, record.Id),
            ["name_index"] = b => Data(b, record.NameIndex),
            ["encrypted_display_name"] = b => Data(b, record.EncryptedDisplayName),
            ["encrypted_payload"] = b => Data(b, record.EncryptedPayload),
            ["encrypted_usage_instructions"] = b => Data(b, record.EncryptedUsageInstructions),
            ["encrypted_private_notes"] = b => Data(b, record.EncryptedPrivateNotes),
            ["payload_kind"] = b => String(b, record.PayloadKind),
            ["permission"] = b => String(b, record.Permission),
            ["created_at"] = b => String(b, record.CreatedAt),
            ["updated_at"] = b => String(b, record.UpdatedAt),
        };
        AddOptional(fields, "encrypted_group_name", record.EncryptedGroupName, Data);
        AddOptional(fields, "encrypted_environment_variable", record.EncryptedEnvironmentVariable, Data);
        AddOptional(fields, "expires_at", record.ExpiresAt, String);
        AddOptional(fields, "encrypted_original_filename", record.EncryptedOriginalFilename, Data);
        AddOptional(fields, "byte_size", record.ByteSize, Integer);
        AddOptional(fields, "content_digest", record.ContentDigest, Data);
        AddOptional(fields, "deleted_at", record.DeletedAt, String);

        var builder = new StringBuilder();
        builder.Append('{');
        var first = true;
        foreach (var (name, write) in fields)
        {
            if (!first) builder.Append(',');
            first = false;
            String(builder, name);
            builder.Append(':');
            write(builder);
        }
        builder.Append('}');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void AddOptional<T>(IDictionary<string, Action<StringBuilder>> fields, string name, T? value,
        Action<StringBuilder, T> write) where T : class
    {
        if (value is not null) fields[name] = b => write(b, value);
    }

    private static void AddOptional(IDictionary<string, Action<StringBuilder>> fields, string name, int? value,
        Action<StringBuilder, int> write)
    {
        if (value is { } present) fields[name] = b => write(b, present);
    }

    private static void Data(StringBuilder builder, byte[] value) => String(builder, Convert.ToBase64String(value));

    private static void Integer(StringBuilder builder, int value) => builder.Append(value.ToString(CultureInfo.InvariantCulture));

    private static void String(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (rune.Value < 0x20)
                    {
                        builder.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(rune.ToString());
                    }
                    break;
            }
        }
        builder.Append('"');
    }
}
