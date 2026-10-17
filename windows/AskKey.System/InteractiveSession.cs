using System.Diagnostics;
using System.Text;

namespace AskKey.System;

public enum InteractiveFailureKind
{
    Cancelled,
    TimedOut,
    InputTooLarge,
    OutputTooLarge,
    ProcessExited,
    SpawnFailed,
    IoFailed,
}

public sealed class InteractiveProcessException : Exception
{
    public InteractiveProcessException(InteractiveFailureKind kind, string? message = null, int exitCode = 0)
        : base(message ?? kind.ToString())
    {
        Kind = kind;
        ExitCode = exitCode;
    }

    public InteractiveFailureKind Kind { get; }
    public int ExitCode { get; }
}

/// <summary>
/// Line-oriented JSONL transport for a private child process. Callers own one
/// session on one thread; the whole child tree lives in a Job Object.
/// </summary>
public sealed class InteractiveSession : IDisposable
{
    private readonly Process _process;
    private readonly IntPtr _job;
    private readonly TimeSpan _timeout;
    private readonly int _maximumInputBytes;
    private readonly int _maximumOutputBytes;
    private readonly TimeSpan _terminationGrace;
    private readonly Func<bool>? _isCancelled;
    private readonly object _closeLock = new();
    private readonly List<byte> _pendingOutput = new();
    private int _totalOutputBytes;
    private TimeSpan? _activeDeadline;
    private bool _closed;
    private Task<int>? _pendingRead;

    internal InteractiveSession(
        Process process,
        IntPtr job,
        TimeSpan timeout,
        int maximumInputBytes,
        int maximumOutputBytes,
        TimeSpan terminationGrace,
        Func<bool>? isCancelled)
    {
        _process = process;
        _job = job;
        _timeout = timeout;
        _maximumInputBytes = maximumInputBytes;
        _maximumOutputBytes = maximumOutputBytes;
        _terminationGrace = terminationGrace;
        _isCancelled = isCancelled;
    }

    public static InteractiveSession Start(InteractiveProcessRequest request)
    {
        if (request.MaximumInputBytes <= 0 || request.MaximumOutputBytes <= 0)
        {
            throw new InteractiveProcessException(
                InteractiveFailureKind.IoFailed, "input and output limits must be positive");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var (key, value) in request.Environment) startInfo.Environment[key] = value;
        if (request.CurrentDirectory is { } directory) startInfo.WorkingDirectory = directory;

        var process = new Process { StartInfo = startInfo };
        var job = WindowsJob.Create();
        try
        {
            if (!process.Start())
            {
                throw new InteractiveProcessException(
                    InteractiveFailureKind.SpawnFailed, $"failed to start {request.Executable}");
            }
            WindowsJob.Assign(job, process);
            RuntimeOperationEvents.Publish(RuntimeOperation.Cli);
            // stderr is discarded, matching the Swift interactive spawn topology.
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            return new InteractiveSession(
                process,
                job,
                request.Timeout,
                request.MaximumInputBytes,
                request.MaximumOutputBytes,
                request.TerminationGrace,
                request.IsCancelled);
        }
        catch (global::System.ComponentModel.Win32Exception exception)
        {
            WindowsJob.Close(job);
            process.Dispose();
            throw new InteractiveProcessException(
                InteractiveFailureKind.SpawnFailed, exception.Message);
        }
        catch
        {
            WindowsJob.Close(job);
            process.Dispose();
            throw;
        }
    }

    public void WriteLine(byte[] data)
    {
        EnsureOpen();
        var line = new byte[data.Length + (data.Length > 0 && data[^1] == 0x0A ? 0 : 1)];
        data.CopyTo(line, 0);
        if (line.Length > data.Length) line[^1] = 0x0A;
        if (line.Length > _maximumInputBytes)
        {
            CloseAfterFailure();
            throw new InteractiveProcessException(InteractiveFailureKind.InputTooLarge);
        }
        _activeDeadline = Clock() + _timeout;
        try
        {
            _process.StandardInput.BaseStream.Write(line);
            _process.StandardInput.BaseStream.Flush();
        }
        catch (IOException exception)
        {
            CloseAfterFailure();
            throw new InteractiveProcessException(InteractiveFailureKind.IoFailed, exception.Message);
        }
    }

    public void WriteLine(string text) => WriteLine(Encoding.UTF8.GetBytes(text));

    public byte[] ReadLine()
    {
        EnsureOpen();
        var deadline = _activeDeadline ?? Clock() + _timeout;
        var buffer = new byte[16 * 1024];
        while (true)
        {
            CheckCancellation(deadline);
            var newline = _pendingOutput.IndexOf(0x0A);
            if (newline >= 0)
            {
                var line = _pendingOutput.GetRange(0, newline).ToArray();
                _pendingOutput.RemoveRange(0, newline + 1);
                if (line.Length > 0 && line[^1] == 0x0D) line = line[..^1];
                return line;
            }
            if (_pendingOutput.Count > _maximumOutputBytes)
            {
                CloseAfterFailure();
                throw new InteractiveProcessException(InteractiveFailureKind.OutputTooLarge);
            }

            var count = ReadWithDeadline(buffer, deadline);
            if (count > 0)
            {
                _totalOutputBytes += count;
                if (_totalOutputBytes > _maximumOutputBytes)
                {
                    CloseAfterFailure();
                    throw new InteractiveProcessException(InteractiveFailureKind.OutputTooLarge);
                }
                _pendingOutput.AddRange(buffer.AsSpan(0, count).ToArray());
                if (_pendingOutput.Count > _maximumOutputBytes)
                {
                    CloseAfterFailure();
                    throw new InteractiveProcessException(InteractiveFailureKind.OutputTooLarge);
                }
                continue;
            }
            if (count == 0)
            {
                var status = _process.HasExited ? _process.ExitCode : 0;
                CloseAfterFailure();
                throw new InteractiveProcessException(
                    InteractiveFailureKind.ProcessExited, "process exited", status);
            }
        }
    }

    public string ReadLineText() => Encoding.UTF8.GetString(ReadLine());

    public void Close()
    {
        lock (_closeLock)
        {
            if (_closed) return;
            _closed = true;
        }
        WindowsJob.Terminate(_job, 0);
        if (!_process.WaitForExit(_terminationGrace))
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.WaitForExit();
        }
        WindowsJob.Close(_job);
        _process.Dispose();
    }

    public void Dispose() => Close();

    private int ReadWithDeadline(byte[] buffer, TimeSpan deadline)
    {
        // One pending read at a time: a timed-out Wait must not leave a second
        // ReadAsync racing the first for the same stream.
        while (true)
        {
            CheckCancellation(deadline);
            if (_pendingRead is null)
            {
                _pendingRead = _process.StandardOutput.BaseStream.ReadAsync(buffer).AsTask();
            }
            var remaining = deadline - Clock();
            if (remaining <= TimeSpan.Zero)
            {
                CloseAfterFailure();
                throw new InteractiveProcessException(InteractiveFailureKind.TimedOut);
            }
            var slice = TimeSpan.FromMilliseconds(Math.Min(50, Math.Max(1, remaining.TotalMilliseconds)));
            if (!_pendingRead.Wait(slice)) continue;
            var completed = _pendingRead;
            _pendingRead = null;
            return completed.Result;
        }
    }

    private void EnsureOpen()
    {
        lock (_closeLock)
        {
            if (_closed) throw new InteractiveProcessException(InteractiveFailureKind.IoFailed, "closed");
        }
    }

    private void CheckCancellation(TimeSpan deadline)
    {
        if (_isCancelled?.Invoke() == true || RestrictedProcessCancellation.Current?.Invoke() == true)
        {
            CloseAfterFailure();
            throw new InteractiveProcessException(InteractiveFailureKind.Cancelled);
        }
        if (Clock() >= deadline)
        {
            CloseAfterFailure();
            throw new InteractiveProcessException(InteractiveFailureKind.TimedOut);
        }
    }

    private void CloseAfterFailure() => Close();

    private static TimeSpan Clock() => TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
}