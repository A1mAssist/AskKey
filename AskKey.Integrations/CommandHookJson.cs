using System.Text;
using System.Text.Json;

namespace AskKey.Integrations;

/// <summary>
/// Preserve object member order and original bytes of untouched values while
/// editing the small owned portion of a shared settings document.
/// </summary>
internal sealed class CommandHookJson
{
    private enum Kind
    {
        Object,
        Array,
        Scalar,
    }

    private readonly Kind _kind;
    private List<(string Key, CommandHookJson Node)> _members;
    private List<CommandHookJson> _elements;
    private byte[]? _original;

    private CommandHookJson(Kind kind)
    {
        _kind = kind;
        _members = new();
        _elements = new();
    }

    private CommandHookJson(Kind kind, byte[] original)
    {
        _kind = kind;
        _original = original;
        _members = new();
        _elements = new();
    }

    public static CommandHookJson EmptyObject => new(Kind.Object);

    public static CommandHookJson EmptyArray => new(Kind.Array);

    public byte[] Bytes => _original ?? Render(0).Concat(new[] { (byte)'\n' }).ToArray();

    public static CommandHookJson Object(IEnumerable<(string Key, CommandHookJson Node)> members)
    {
        var node = new CommandHookJson(Kind.Object);
        node._members.AddRange(members);
        return node;
    }

    public static CommandHookJson Array(IEnumerable<CommandHookJson> elements)
    {
        var node = new CommandHookJson(Kind.Array);
        node._elements.AddRange(elements);
        return node;
    }

    private byte[] Render(int depth)
    {
        if (_original is not null) return _original;
        switch (_kind)
        {
            case Kind.Object:
                return Join(
                    _members.Select(member => QuotedKey(member.Key)
                        .Concat(Encoding.UTF8.GetBytes(": "))
                        .Concat(member.Node.Render(depth + 1)).ToArray()),
                    "{", "}", depth);
            case Kind.Array:
                return Join(_elements.Select(element => element.Render(depth + 1)), "[", "]", depth);
            default:
                return global::System.Array.Empty<byte>();
        }
    }

    private static byte[] QuotedKey(string key)
    {
        var encoded = new List<byte> { (byte)'"' };
        foreach (var value in Encoding.UTF8.GetBytes(key))
        {
            switch (value)
            {
                case (byte)'"':
                case (byte)'\\':
                    encoded.Add((byte)'\\');
                    encoded.Add(value);
                    break;
                case < 32:
                    encoded.AddRange(Encoding.UTF8.GetBytes($"\\u{value:x4}"));
                    break;
                default:
                    encoded.Add(value);
                    break;
            }
        }
        encoded.Add((byte)'"');
        return encoded.ToArray();
    }

    private static byte[] Join(IEnumerable<byte[]> input, string open, string close, int depth)
    {
        var values = input.ToArray();
        if (values.Length == 0) return Encoding.UTF8.GetBytes(open + close);
        var result = new List<byte>();
        result.AddRange(Encoding.UTF8.GetBytes(open));
        result.Add((byte)'\n');
        for (var index = 0; index < values.Length; index++)
        {
            result.AddRange(Encoding.UTF8.GetBytes(new string(' ', (depth + 1) * 2)));
            result.AddRange(values[index]);
            if (index < values.Length - 1) result.Add((byte)',');
            result.Add((byte)'\n');
        }
        result.AddRange(Encoding.UTF8.GetBytes(new string(' ', depth * 2)));
        result.AddRange(Encoding.UTF8.GetBytes(close));
        return result.ToArray();
    }

    /// <summary>Newly added groups have no user formatting to preserve; render
    /// their containers at the indentation of the insertion point.</summary>
    public static CommandHookJson Generated(byte[] data)
    {
        var node = Parse(data);
        node.RegenerateContainers();
        return node;
    }

    private void RegenerateContainers()
    {
        switch (_kind)
        {
            case Kind.Object:
                foreach (var member in _members) member.Node.RegenerateContainers();
                break;
            case Kind.Array:
                foreach (var element in _elements) element.RegenerateContainers();
                break;
            default:
                return;
        }
        _original = null;
    }

    public CommandHookJson? Member(string key)
    {
        if (_kind != Kind.Object) return null;
        foreach (var member in _members)
        {
            if (member.Key == key) return member.Node;
        }
        return null;
    }

    public CommandHookJson RequiredMember(string key)
        => Member(key) ?? throw Invalid();

    public void Set(string key, CommandHookJson node)
    {
        if (_kind != Kind.Object) throw Invalid();
        for (var index = 0; index < _members.Count; index++)
        {
            if (_members[index].Key == key)
            {
                _members[index] = (key, node);
                _original = null;
                return;
            }
        }
        _members.Add((key, node));
        _original = null;
    }

    public CommandHookJson Element(int index)
    {
        if (_kind != Kind.Array || index < 0 || index >= _elements.Count) throw Invalid();
        return _elements[index];
    }

    public void SetElement(int index, CommandHookJson node)
    {
        if (_kind != Kind.Array || index < 0 || index >= _elements.Count) throw Invalid();
        _elements[index] = node;
        _original = null;
    }

    public void Append(CommandHookJson node)
    {
        if (_kind != Kind.Array) throw Invalid();
        _elements.Add(node);
        _original = null;
    }

    public void Remove(int index)
    {
        if (_kind != Kind.Array || index < 0 || index >= _elements.Count) throw Invalid();
        _elements.RemoveAt(index);
        _original = null;
    }

    public bool IsEmptyArray => _kind == Kind.Array && _elements.Count == 0;

    public int Count => _kind switch
    {
        Kind.Object => _members.Count,
        Kind.Array => _elements.Count,
        _ => 0,
    };

    public static CommandHookJson Parse(byte[] data)
    {
        // System.Text.Json validates grammar before the order-preserving parser.
        try
        {
            using var _ = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
        var parser = new Parser(data);
        var node = parser.Node(0);
        parser.Space();
        if (parser.Offset != parser.Data.Length) throw Invalid();
        return node;
    }

    public static byte[] Serialize(object value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            Write(writer, value);
        }
        return stream.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case IDictionary<string, object> dictionary:
                writer.WriteStartObject();
                foreach (var (key, child) in dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    Write(writer, child);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable<object> list:
                writer.WriteStartArray();
                foreach (var child in list) Write(writer, child);
                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentException("unsupported JSON value", nameof(value));
        }
    }

    private static CommandDiscoveryHookConfigurationException Invalid()
        => new(CommandDiscoveryHookConfigurationError.InvalidHooksFile);

    private sealed class Parser
    {
        public Parser(byte[] data) => Data = data;

        public byte[] Data { get; }
        public int Offset { get; private set; }

        public void Space()
        {
            while (Offset < Data.Length && Data[Offset] is (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)' ')
            {
                Offset++;
            }
        }

        private (int Start, int End) String()
        {
            var start = Offset;
            if (Offset >= Data.Length || Data[Offset] != (byte)'"') throw Invalid();
            Offset++;
            while (Offset < Data.Length)
            {
                var value = Data[Offset];
                Offset++;
                if (value == (byte)'"') return (start, Offset);
                if (value == (byte)'\\') Offset++;
            }
            throw Invalid();
        }

        public CommandHookJson Node(int depth)
        {
            Space();
            if (Offset >= Data.Length || depth >= 128) throw Invalid();
            var start = Offset;
            CommandHookJson node;
            switch (Data[Offset])
            {
                case (byte)'{':
                {
                    Offset++;
                    Space();
                    var members = new List<(string, CommandHookJson)>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    while (Offset < Data.Length && Data[Offset] != (byte)'}')
                    {
                        var (keyStart, keyEnd) = String();
                        string key;
                        try
                        {
                            key = JsonSerializer.Deserialize<string>(Data.AsSpan(keyStart, keyEnd - keyStart))!;
                        }
                        catch (JsonException)
                        {
                            throw Invalid();
                        }
                        if (!seen.Add(key)) throw Invalid();
                        Space();
                        if (Offset >= Data.Length || Data[Offset] != (byte)':') throw Invalid();
                        Offset++;
                        members.Add((key, Node(depth + 1)));
                        Space();
                        if (Offset < Data.Length && Data[Offset] == (byte)',')
                        {
                            Offset++;
                            Space();
                        }
                        else
                        {
                            break;
                        }
                    }
                    if (Offset >= Data.Length || Data[Offset] != (byte)'}') throw Invalid();
                    Offset++;
                    node = Object(members);
                    break;
                }
                case (byte)'[':
                {
                    Offset++;
                    Space();
                    var elements = new List<CommandHookJson>();
                    while (Offset < Data.Length && Data[Offset] != (byte)']')
                    {
                        elements.Add(Node(depth + 1));
                        Space();
                        if (Offset < Data.Length && Data[Offset] == (byte)',')
                        {
                            Offset++;
                            Space();
                        }
                        else
                        {
                            break;
                        }
                    }
                    if (Offset >= Data.Length || Data[Offset] != (byte)']') throw Invalid();
                    Offset++;
                    node = Array(elements);
                    break;
                }
                case (byte)'"':
                    String();
                    node = new CommandHookJson(Kind.Scalar);
                    break;
                default:
                    while (Offset < Data.Length && Data[Offset] is not ((byte)'\t' or (byte)'\n'
                        or (byte)'\r' or (byte)' ' or (byte)',' or (byte)']' or (byte)'}'))
                    {
                        Offset++;
                    }
                    if (Offset <= start) throw Invalid();
                    node = new CommandHookJson(Kind.Scalar);
                    break;
            }
            return new CommandHookJson(node._kind, Data[start..Offset])
            {
                _members = node._members,
                _elements = node._elements,
            };
        }
    }
}
