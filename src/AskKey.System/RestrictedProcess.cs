using System.Text;

namespace AskKey.System;

internal sealed class ByteAccumulator
{
    private readonly MemoryStream _storage = new();

    public ByteAccumulator(int maximumBytes) => MaximumBytes = maximumBytes;

    public int MaximumBytes { get; }

    public byte[] Data => _storage.ToArray();

    public void Append(ReadOnlySpan<byte> data, bool truncate)
    {
        if (data.IsEmpty) return;
        if (_storage.Length >= MaximumBytes)
        {
            if (truncate) return;
            throw new RestrictedProcessException(RestrictedProcessFailureKind.OutputTooLarge);
        }
        var allowed = Math.Min(data.Length, MaximumBytes - (int)_storage.Length);
        if (allowed > 0) _storage.Write(data[..allowed]);
        if (!truncate && allowed < data.Length)
        {
            throw new RestrictedProcessException(RestrictedProcessFailureKind.OutputTooLarge);
        }
    }
}

public enum RestrictedProcessFailureKind
{
    Cancelled,
    InputTooLarge,
    OutputTooLarge,
    SpawnFailed,
    IoFailed,
}

public sealed class RestrictedProcessException : Exception
{
    public RestrictedProcessException(RestrictedProcessFailureKind kind, string? message = null)
        : base(message ?? kind.ToString()) => Kind = kind;

    public RestrictedProcessFailureKind Kind { get; }

    public static RestrictedProcessException SpawnFailed(string message) =>
        new(RestrictedProcessFailureKind.SpawnFailed, message);

    public static RestrictedProcessException IoFailed(string message) =>
        new(RestrictedProcessFailureKind.IoFailed, message);
}

public sealed class RestrictedProcessResult
{
    public required int Status { get; init; }
    public required byte[] Stdout { get; init; }
    public required byte[] Stderr { get; init; }
    public required bool TimedOut { get; init; }

    public string StdoutText => Encoding.UTF8.GetString(Stdout);
    public string StderrText => Encoding.UTF8.GetString(Stderr);
}

public sealed class RestrictedProcessRequest
{
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();
    public string? CurrentDirectory { get; init; }
    public byte[]? StandardInput { get; init; }
    public bool WriteInputBeforeSpawn { get; init; }
    public int? MaximumInputBytes { get; init; }
    public required TimeSpan Timeout { get; init; }
    public bool CaptureStderr { get; init; } = true;
    public required int MaximumOutputBytes { get; init; }
    public bool TruncateOutput { get; init; } = true;
    public TimeSpan TerminationGrace { get; init; }
    public Func<bool>? IsCancelled { get; init; }
}

public sealed class InteractiveProcessRequest
{
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();
    public string? CurrentDirectory { get; init; }
    public required TimeSpan Timeout { get; init; }
    public required int MaximumInputBytes { get; init; }
    public required int MaximumOutputBytes { get; init; }
    public TimeSpan TerminationGrace { get; init; }
    public Func<bool>? IsCancelled { get; init; }
}
