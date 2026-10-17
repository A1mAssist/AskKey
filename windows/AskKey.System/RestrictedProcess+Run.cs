using System.Diagnostics;

namespace AskKey.System;

public static class RestrictedProcess
{
    public static void Run(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        int maximumOutputBytes)
    {
        _ = Run(new RestrictedProcessRequest
        {
            Executable = executable,
            Arguments = arguments,
            Environment = environment,
            Timeout = timeout,
            MaximumOutputBytes = maximumOutputBytes,
        });
    }

    public static RestrictedProcessResult Run(RestrictedProcessRequest request)
    {
        if (request.MaximumInputBytes is { } limit
            && request.StandardInput is { } input
            && input.Length > limit)
        {
            throw new RestrictedProcessException(RestrictedProcessFailureKind.InputTooLarge);
        }
        if (request.MaximumOutputBytes < 0)
        {
            throw new RestrictedProcessException(
                RestrictedProcessFailureKind.IoFailed, "maximumOutputBytes must not be negative");
        }

        var stdout = new ByteAccumulator(request.MaximumOutputBytes);
        var stderr = new ByteAccumulator(request.MaximumOutputBytes);
        var startInfo = BuildStartInfo(request);
        using var process = new Process { StartInfo = startInfo };
        var job = WindowsJob.Create();
        try
        {
            if (!process.Start())
            {
                throw RestrictedProcessException.SpawnFailed(
                    $"failed to start {request.Executable}");
            }
            WindowsJob.Assign(job, process);
            RuntimeOperationEvents.Publish(RuntimeOperation.Cli);

            var stdoutTask = DrainAsync(process.StandardOutput.BaseStream, stdout, request.TruncateOutput);
            var stderrTask = request.CaptureStderr
                ? DrainAsync(process.StandardError.BaseStream, stderr, request.TruncateOutput)
                : Task.CompletedTask;

            if (request.StandardInput is { } standardInput)
            {
                WriteInput(process, standardInput, request.WriteInputBeforeSpawn);
            }
            else
            {
                process.StandardInput.Close();
            }

            var start = Clock(request.UsesMonotonicClock);
            var timedOut = false;
            while (true)
            {
                if (IsCancelled(request))
                {
                    WindowsJob.Terminate(job, 1);
                    process.WaitForExit();
                    throw new RestrictedProcessException(RestrictedProcessFailureKind.Cancelled);
                }
                if (process.WaitForExit(5)) break;
                if (Clock(request.UsesMonotonicClock) - start < request.Timeout) continue;
                timedOut = true;
                WindowsJob.Terminate(job, 1);
                if (request.TerminationGrace > TimeSpan.Zero)
                {
                    process.WaitForExit(request.TerminationGrace);
                }
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                }
                process.WaitForExit();
                break;
            }

            // The job owns any descendant the target left behind.
            WindowsJob.Terminate(job, 0);
            try
            {
                Task.WaitAll(new[] { stdoutTask, stderrTask });
            }
            catch (AggregateException exception) when (exception.InnerExceptions.Count == 1)
            {
                // Swift surfaces the bare Failure; unwrap the single drain error.
                global::System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(exception.InnerExceptions[0]).Throw();
            }
            return new RestrictedProcessResult
            {
                Status = timedOut ? 0 : process.ExitCode,
                Stdout = stdout.Data,
                Stderr = stderr.Data,
                TimedOut = timedOut,
            };
        }
        catch (global::System.ComponentModel.Win32Exception exception)
        {
            throw RestrictedProcessException.SpawnFailed(exception.Message);
        }
        finally
        {
            WindowsJob.Close(job);
        }
    }

    private static ProcessStartInfo BuildStartInfo(RestrictedProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = request.CaptureStderr,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var (key, value) in request.Environment) startInfo.Environment[key] = value;
        if (request.CurrentDirectory is { } directory) startInfo.WorkingDirectory = directory;
        return startInfo;
    }

    private static void WriteInput(Process process, byte[] input, bool beforeSpawn)
    {
        // writeInputBeforeSpawn has no separate meaning on Windows: the child cannot
        // read until the pipe is written, so both modes write here. A child that closes
        // stdin early surfaces as a broken pipe, which the Swift port also tolerates.
        _ = beforeSpawn;
        try
        {
            process.StandardInput.BaseStream.Write(input);
            process.StandardInput.BaseStream.Flush();
        }
        catch (IOException)
        {
            // A child may close stdin early; preserve its output and exit status.
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }
    }

    private static async Task DrainAsync(Stream stream, ByteAccumulator accumulator, bool truncate)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (count <= 0) return;
            accumulator.Append(buffer.AsSpan(0, count), truncate);
        }
    }

    private static bool IsCancelled(RestrictedProcessRequest request)
        => request.IsCancelled?.Invoke() == true
            || RestrictedProcessCancellation.Current?.Invoke() == true;

    private static TimeSpan Clock(bool monotonic)
        => monotonic ? TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond) : DateTime.UtcNow.TimeOfDay;
}
