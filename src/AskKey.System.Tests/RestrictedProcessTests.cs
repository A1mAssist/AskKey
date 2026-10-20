using System.Diagnostics;
using System.Text;
using AskKey.System;

namespace AskKey.System.Tests;

public sealed class RestrictedProcessTests
{
    /// <summary>Echo helper: the fixture is built once so stdin/stdout behavior is deterministic.</summary>
    private static readonly Lazy<string> EchoHelper = new(BuildEchoHelper);

    private static readonly string Cmd = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private static readonly Dictionary<string, string> PathEnvironment = new()
    {
        ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? @"C:\Windows\System32",
    };

    [Fact]
    public void SuccessCapturesStdout()
    {
        var result = RestrictedProcess.Run(new RestrictedProcessRequest
        {
            Executable = Cmd,
            Arguments = new[] { "/c", "echo askkey-ok" },
            Environment = PathEnvironment,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOutputBytes = 4_096,
        });
        Assert.Equal(0, result.Status);
        Assert.False(result.TimedOut);
        Assert.Equal("askkey-ok\r\n", result.StdoutText);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public void NonzeroExitReturnsStatus()
    {
        var result = RestrictedProcess.Run(new RestrictedProcessRequest
        {
            Executable = Cmd,
            Arguments = new[] { "/c", "exit 1" },
            Environment = PathEnvironment,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOutputBytes = 4_096,
        });
        Assert.Equal(1, result.Status);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void TimeoutKillsAndLeavesUnrelatedProcess()
    {
        using var control = Process.Start(new ProcessStartInfo
        {
            FileName = Cmd,
            Arguments = "/c ping -n 9 127.0.0.1 >nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var result = RestrictedProcess.Run(new RestrictedProcessRequest
            {
                Executable = Cmd,
                Arguments = new[] { "/c", "ping -n 9 127.0.0.1 >nul" },
                Environment = PathEnvironment,
                Timeout = TimeSpan.FromMilliseconds(300),
                MaximumOutputBytes = 4_096,
                TerminationGrace = TimeSpan.FromMilliseconds(50),
            });
            Assert.True(result.TimedOut);
            Assert.False(control.HasExited);
        }
        finally
        {
            try { control.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void CancelStopsTheOwnedProcess()
    {
        var cancelled = new LockedFlag();
        _ = Task.Run(async () =>
        {
            await Task.Delay(80);
            cancelled.Value = true;
        });
        var started = DateTime.UtcNow;
        var exception = Assert.Throws<RestrictedProcessException>(() => RestrictedProcess.Run(
            new RestrictedProcessRequest
            {
                Executable = Cmd,
                Arguments = new[] { "/c", "ping -n 9 127.0.0.1 >nul" },
                Environment = PathEnvironment,
                Timeout = TimeSpan.FromSeconds(4),
                MaximumOutputBytes = 4_096,
                IsCancelled = () => cancelled.Value,
            }));
        Assert.Equal(RestrictedProcessFailureKind.Cancelled, exception.Kind);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AmbientCancelIsHonored()
    {
        var flag = new LockedFlag();
        _ = Task.Run(async () =>
        {
            await Task.Delay(80);
            flag.Value = true;
        });
        var exception = Assert.Throws<RestrictedProcessException>(
            () => RestrictedProcessCancellation.WithValue(
                () => flag.Value,
                () => RestrictedProcess.Run(new RestrictedProcessRequest
                {
                    Executable = Cmd,
                    Arguments = new[] { "/c", "ping -n 9 127.0.0.1 >nul" },
                    Environment = PathEnvironment,
                    Timeout = TimeSpan.FromSeconds(4),
                    MaximumOutputBytes = 4_096,
                })));
        Assert.Equal(RestrictedProcessFailureKind.Cancelled, exception.Kind);
    }

    [Fact]
    public void IgnoringTermDescendantIsReaped()
    {
        var directory = MakeDirectory();
        try
        {
            var pidFile = Path.Combine(directory, "child.pid");
            var result = RestrictedProcess.Run(new RestrictedProcessRequest
            {
                Executable = Cmd,
                Arguments = new[]
                {
                    "/c",
                    $"start /b cmd /c ping -n 9 127.0.0.1 >nul & echo %processid% > \"{pidFile}\" & ping -n 9 127.0.0.1 >nul",
                },
                Environment = PathEnvironment,
                Timeout = TimeSpan.FromMilliseconds(600),
                MaximumOutputBytes = 4_096,
                TerminationGrace = TimeSpan.FromMilliseconds(100),
            });
            Assert.True(result.TimedOut);
            if (File.Exists(pidFile))
            {
                var child = int.Parse(File.ReadAllText(pidFile).Trim());
                AssertProcessGone(child);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void StderrIsCapturedOrDiscardedByFlag()
    {
        var captured = RestrictedProcess.Run(new RestrictedProcessRequest
        {
            Executable = Cmd,
            Arguments = new[] { "/c", "echo out& echo err 1>&2" },
            Environment = PathEnvironment,
            Timeout = TimeSpan.FromSeconds(5),
            CaptureStderr = true,
            MaximumOutputBytes = 4_096,
        });
        Assert.Equal("out\r\n", captured.StdoutText);
        Assert.Equal("err \r\n", captured.StderrText);

        var discarded = RestrictedProcess.Run(new RestrictedProcessRequest
        {
            Executable = Cmd,
            Arguments = new[] { "/c", "echo out& echo err 1>&2" },
            Environment = PathEnvironment,
            Timeout = TimeSpan.FromSeconds(5),
            CaptureStderr = false,
            MaximumOutputBytes = 4_096,
        });
        Assert.Equal("out\r\n", discarded.StdoutText);
        Assert.Empty(discarded.Stderr);
    }

    [Fact]
    public void OutputCapTruncatesOrThrows()
    {
        var truncated = RestrictedProcess.Run(new RestrictedProcessRequest
        {
            Executable = Cmd,
            Arguments = new[] { "/c", "for /l %i in (1,1,200) do @echo 0123456789" },
            Environment = PathEnvironment,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOutputBytes = 100,
            TruncateOutput = true,
        });
        Assert.Equal(100, truncated.Stdout.Length);
        Assert.False(truncated.TimedOut);

        var exception = Assert.Throws<RestrictedProcessException>(() => RestrictedProcess.Run(
            new RestrictedProcessRequest
            {
                Executable = Cmd,
                Arguments = new[] { "/c", "for /l %i in (1,1,200) do @echo 0123456789" },
                Environment = PathEnvironment,
                Timeout = TimeSpan.FromSeconds(5),
                MaximumOutputBytes = 100,
                TruncateOutput = false,
            }));
        Assert.Equal(RestrictedProcessFailureKind.OutputTooLarge, exception.Kind);
    }

    [Fact]
    public void InputIsWrittenAndEarlyExitIsTolerated()
    {
        var echoed = RestrictedProcess.Run(new RestrictedProcessRequest
        {
            Executable = EchoHelper.Value,
            Arguments = Array.Empty<string>(),
            Environment = PathEnvironment,
            StandardInput = Encoding.UTF8.GetBytes("prewrite\n"),
            WriteInputBeforeSpawn = true,
            MaximumInputBytes = 4_096,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOutputBytes = 4_096,
        });
        Assert.Contains("prewrite", echoed.StdoutText);

        var after = RestrictedProcess.Run(new RestrictedProcessRequest
        {
            Executable = EchoHelper.Value,
            Arguments = Array.Empty<string>(),
            Environment = PathEnvironment,
            StandardInput = Encoding.UTF8.GetBytes("postwrite\n"),
            WriteInputBeforeSpawn = false,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOutputBytes = 4_096,
        });
        Assert.Contains("postwrite", after.StdoutText);

        var early = RestrictedProcess.Run(new RestrictedProcessRequest
        {
            Executable = Cmd,
            Arguments = new[] { "/c", "exit 23" },
            Environment = PathEnvironment,
            StandardInput = new byte[1_048_576],
            WriteInputBeforeSpawn = false,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOutputBytes = 4_096,
        });
        Assert.Equal(23, early.Status);
        Assert.False(early.TimedOut);

        var exception = Assert.Throws<RestrictedProcessException>(() => RestrictedProcess.Run(
            new RestrictedProcessRequest
            {
                Executable = EchoHelper.Value,
                Arguments = Array.Empty<string>(),
                Environment = PathEnvironment,
                StandardInput = new byte[5_000],
                WriteInputBeforeSpawn = true,
                MaximumInputBytes = 4_096,
                Timeout = TimeSpan.FromSeconds(5),
                MaximumOutputBytes = 4_096,
            }));
        Assert.Equal(RestrictedProcessFailureKind.InputTooLarge, exception.Kind);
    }

    [Fact]
    public void EnvironmentAndWorkingDirectory()
    {
        var directory = MakeDirectory();
        try
        {
            var result = RestrictedProcess.Run(new RestrictedProcessRequest
            {
                Executable = Cmd,
                Arguments = new[] { "/c", "cd" },
                Environment = new Dictionary<string, string>
                {
                    ["ASKKEY_RP"] = "marker",
                    ["PATH"] = PathEnvironment["PATH"],
                },
                CurrentDirectory = directory,
                Timeout = TimeSpan.FromSeconds(5),
                MaximumOutputBytes = 4_096,
            });
            var stdout = result.StdoutText;
            Assert.Contains(new DirectoryInfo(directory).Name, stdout);
            Assert.Contains(new DirectoryInfo(directory).Name, stdout);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void MissingExecutableAndDirectoryFailWithoutKillingOthers()
    {
        using var control = Process.Start(new ProcessStartInfo
        {
            FileName = Cmd,
            Arguments = "/c ping -n 9 127.0.0.1 >nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var missing = Assert.Throws<RestrictedProcessException>(() => RestrictedProcess.Run(
                new RestrictedProcessRequest
                {
                    Executable = @"C:\no\such\askkey-restricted-process.exe",
                    Environment = PathEnvironment,
                    Timeout = TimeSpan.FromSeconds(1),
                    MaximumOutputBytes = 128,
                }));
            Assert.Equal(RestrictedProcessFailureKind.SpawnFailed, missing.Kind);

            var badDirectory = Assert.Throws<RestrictedProcessException>(() => RestrictedProcess.Run(
                new RestrictedProcessRequest
                {
                    Executable = EchoHelper.Value,
                    Arguments = Array.Empty<string>(),
                    Environment = PathEnvironment,
                    CurrentDirectory = @"C:\no\such\askkey-cwd",
                    Timeout = TimeSpan.FromSeconds(1),
                    MaximumOutputBytes = 128,
                }));
            Assert.Equal(RestrictedProcessFailureKind.SpawnFailed, badDirectory.Kind);
            Assert.False(control.HasExited);
        }
        finally
        {
            try { control.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// The timeout must not depend on a wall clock, so it cannot be stretched or
    /// collapsed by a system time change such as a UTC midnight crossing.
    /// </summary>
    [Fact]
    public void TimeoutUsesAMonotonicClockAndIsUnaffectedByWallClockChanges()
    {
        var result = RestrictedProcess.Run(new RestrictedProcessRequest
        {
            Executable = Cmd,
            Arguments = new[] { "/c", "ping -n 30 127.0.0.1 >nul" },
            Environment = PathEnvironment,
            Timeout = TimeSpan.FromMilliseconds(300),
            MaximumOutputBytes = 4_096,
            TerminationGrace = TimeSpan.FromMilliseconds(50),
        });
        Assert.True(result.TimedOut);
    }

    [Fact]
    public void AProcessThatFinishesInsideTheBudgetIsNotReportedAsTimedOut()
    {
        // The wall-clock implementation could subtract a negative elapsed span after a
        // backwards time adjustment and time out a process that already finished.
        var result = RestrictedProcess.Run(new RestrictedProcessRequest
        {
            Executable = Cmd,
            Arguments = new[] { "/c", "exit 0" },
            Environment = PathEnvironment,
            Timeout = TimeSpan.FromSeconds(30),
            MaximumOutputBytes = 4_096,
        });
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.Status);
    }

    [Fact]
    public void InteractiveSessionSpeaksLineDelimitedJson()
    {
        using var session = InteractiveSession.Start(new InteractiveProcessRequest
        {
            Executable = EchoHelper.Value,
            Arguments = Array.Empty<string>(),
            Environment = PathEnvironment,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumInputBytes = 4_096,
            MaximumOutputBytes = 4_096,
        });
        session.WriteLine("{\"id\":1}");
        Assert.Equal("{\"id\":1}", session.ReadLineText());
    }

    [Fact]
    public void InteractiveSessionEnforcesInputLimit()
    {
        using var session = InteractiveSession.Start(new InteractiveProcessRequest
        {
            Executable = EchoHelper.Value,
            Arguments = Array.Empty<string>(),
            Environment = PathEnvironment,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumInputBytes = 8,
            MaximumOutputBytes = 4_096,
        });
        var exception = Assert.Throws<InteractiveProcessException>(
            () => session.WriteLine(new string('x', 64)));
        Assert.Equal(InteractiveFailureKind.InputTooLarge, exception.Kind);
    }

    private static string BuildEchoHelper()
    {
        var directory = Path.Combine(Path.GetTempPath(), "askkey-echo-helper");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "echohelper.exe");
        var source = Path.Combine(directory, "Program.cs");
        var project = Path.Combine(directory, "echohelper.csproj");
        File.WriteAllText(source, "internal static class Program { private static void Main() { System.Console.InputEncoding = System.Text.Encoding.UTF8; string? line; while ((line = System.Console.ReadLine()) != null) { System.Console.Out.Write(line); System.Console.Out.Write('\\n'); System.Console.Out.Flush(); } } }", new global::System.Text.UTF8Encoding(false));
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><AssemblyName>echohelper</AssemblyName><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Program.cs\" /></ItemGroup></Project>", new global::System.Text.UTF8Encoding(false));
        var build = global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{project}\" -c Release -o \"{directory}\" --nologo -v q",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        build.WaitForExit();
        if (build.ExitCode != 0 || !File.Exists(executable))
        {
            throw new InvalidOperationException("failed to build the echo helper fixture");
        }
        return executable;
    }
    private static string MakeDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"askkey-rp-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void AssertProcessGone(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return;
            }
            catch (ArgumentException)
            {
                return;
            }
            Thread.Sleep(20);
        }
        Assert.Fail($"process {pid} should be gone");
    }
}

internal sealed class LockedFlag
{
    private readonly object _lock = new();
    private bool _value;

    public bool Value
    {
        get { lock (_lock) return _value; }
        set { lock (_lock) _value = value; }
    }
}