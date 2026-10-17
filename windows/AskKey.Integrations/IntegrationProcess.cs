namespace AskKey.Integrations;

/// <summary>Result of a captured CLI invocation; mirrors the Swift RunResult.</summary>
public readonly record struct RunResult(int Status, byte[] Stdout, byte[] Stderr, bool TimedOut)
{
    public string StdoutText => global::System.Text.Encoding.UTF8.GetString(Stdout);
    public string StderrText => global::System.Text.Encoding.UTF8.GetString(Stderr);
}

internal static class IntegrationProcess
{
    /// <summary>
    /// Codex policy: pre-write stdin, 4 KiB input cap, 2 s budget, stdout only,
    /// 1 MiB overflow is failure, immediate kill.
    /// </summary>
    public static string RunStrict(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        byte[]? standardInput = null)
    {
        try
        {
            var result = global::AskKey.System.RestrictedProcess.Run(
                new global::AskKey.System.RestrictedProcessRequest
                {
                    Executable = executable,
                    Arguments = arguments,
                    Environment = environment,
                    StandardInput = standardInput ?? Array.Empty<byte>(),
                    WriteInputBeforeSpawn = true,
                    MaximumInputBytes = 4096,
                    Timeout = TimeSpan.FromSeconds(2),
                    UsesMonotonicClock = true,
                    CaptureStderr = false,
                    MaximumOutputBytes = 1_048_576,
                    TruncateOutput = false,
                    TerminationGrace = TimeSpan.Zero,
                });
            if (result.TimedOut || result.Status != 0)
            {
                throw CodexUserMCPException.ConnectionFailed("cli");
            }
            return result.StdoutText;
        }
        catch (global::AskKey.System.RestrictedProcessException)
        {
            throw CodexUserMCPException.ConnectionFailed("cli");
        }
    }

    /// <summary>Grok policy: chdir, write stdin after spawn, capture stderr,
    /// truncate at the caller cap, wall clock, TERM grace, timeout returns a result.</summary>
    public static RunResult RunCaptured(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string currentDirectory,
        byte[]? input,
        TimeSpan timeout,
        TimeSpan terminationGrace,
        int maximumBytes,
        Func<bool>? isCancelled = null)
    {
        var result = global::AskKey.System.RestrictedProcess.Run(
            new global::AskKey.System.RestrictedProcessRequest
            {
                Executable = executable,
                Arguments = arguments,
                Environment = environment,
                CurrentDirectory = currentDirectory,
                StandardInput = input,
                WriteInputBeforeSpawn = false,
                Timeout = timeout,
                UsesMonotonicClock = false,
                CaptureStderr = true,
                MaximumOutputBytes = maximumBytes,
                TruncateOutput = true,
                TerminationGrace = terminationGrace,
                IsCancelled = isCancelled,
            });
        return new RunResult(result.Status, result.Stdout, result.Stderr, result.TimedOut);
    }

    /// <summary>ProcessInfo.processInfo.environment counterpart, without the app's
    /// credential-bearing variables where the caller asked for isolation.</summary>
    public static Dictionary<string, string> CurrentEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (global::System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) environment[key] = value;
        }
        return environment;
    }

    /// <summary>Windows names for the POSIX variables the Swift port passed through.</summary>
    public static Dictionary<string, string> PortableEnvironment()
    {
        var source = CurrentEnvironment();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Copy(string from, string to)
        {
            if (source.TryGetValue(from, out var value)) result[to] = value;
        }
        Copy("USERPROFILE", "HOME");
        Copy("USERPROFILE", "USERPROFILE");
        Copy("PATH", "PATH");
        Copy("TEMP", "TMPDIR");
        Copy("TEMP", "TEMP");
        Copy("TMP", "TMP");
        Copy("USERNAME", "USER");
        Copy("USERNAME", "USERNAME");
        Copy("LANG", "LANG");
        foreach (var (key, value) in source)
        {
            if (key.StartsWith("LC_", StringComparison.OrdinalIgnoreCase)) result[key] = value;
        }
        return result;
    }
}

