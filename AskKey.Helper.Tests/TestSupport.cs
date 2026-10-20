using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AskKey.Broker.Protocol;
using AskKey.Broker.Transport;

namespace AskKey.Helper.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = global::System.IO.Path.Combine(
            global::System.IO.Path.GetTempPath(),
            "askkey-helper-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts)
        => global::System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class BrokerHarness : IDisposable
{
    private readonly string _directory;

    public BrokerHarness(BrokerRequestHandler handler)
    {
        _directory = global::System.IO.Path.Combine(
            global::System.IO.Path.GetTempPath(),
            "askkey-helper-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        PipeName = BrokerPipeName.Create();
        Server = new BrokerSocketServer(PipeName, _directory, handler, _ => { });
        Server.Start();
    }

    public string PipeName { get; }
    public BrokerSocketServer Server { get; }
    public string PipeFile => global::System.IO.Path.Combine(_directory, BrokerRuntimeDirectory.PipeNameFileName);
    // The testhost is not AskKey.exe, so the production server-identity gate is bypassed.
    public BrokerSocketClient Client => new(PipeName, BrokerServerIdentity.AlwaysTrusted);

    public void Dispose()
    {
        Server.Dispose();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class TestHandlers
{
    public static BrokerRequestHandler Catalog(
        IReadOnlyList<BrokerCatalogItem>? items = null,
        IReadOnlyList<string>? groups = null)
        => new(
            catalog: _ => items ?? Array.Empty<BrokerCatalogItem>(),
            requestStatus: (_, _) => null,
            catalogGroups: _ => groups ?? Array.Empty<string>());

    public static BrokerRequestHandler Exited(
        int code,
        Action<BrokerRuntimeChannel>? write = null)
        => new(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: (_, _) => null,
            textRun: (_, channel, _) =>
            {
                write?.Invoke(channel);
                return BrokerTextRunResult.Exited(code);
            });

    public static BrokerRequestHandler OutcomeUnknown()
        => new(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: (_, _) => null,
            textRun: (_, _, _) => BrokerTextRunResult.OutcomeUnknown);
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class HelperProcess
{
    private static readonly object RuntimeLock = new();
    private static string? _sharedRuntime;

    public static ProcessResult Run(
        IEnumerable<string> arguments,
        BrokerHarness? broker = null,
        string? workingDirectory = null,
        string? standardInput = null,
        int timeoutMilliseconds = 15000)
    {
        var runtime = SharedRuntime();
        var startInfo = new ProcessStartInfo
        {
            FileName = global::System.IO.Path.Combine(runtime, "askkey.exe"),
            WorkingDirectory = workingDirectory ?? runtime,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Remove("ASKKEY_DEBUG_RUN_DIRECTORY");
        if (broker is null)
        {
            startInfo.Environment.Remove("ASKKEY_BROKER_PIPE");
        }
        else
        {
            startInfo.Environment["ASKKEY_BROKER_PIPE"] = broker.PipeFile;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        if (standardInput is null)
        {
            process.StandardInput.Close();
        }
        else
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("helper process did not exit");
        }
        return new ProcessResult(
            process.ExitCode,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());
    }

    public static string SharedRuntime()
    {
        lock (RuntimeLock)
        {
            if (_sharedRuntime is not null) return _sharedRuntime;
            var source = global::System.IO.Path.GetDirectoryName(typeof(AskKey.Helper.Program).Assembly.Location)
                ?? throw new InvalidOperationException("helper assembly directory unavailable");
            var runtime = global::System.IO.Path.Combine(
                global::System.IO.Path.GetTempPath(),
                "askkey-helper-tests-runtime",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runtime);
            foreach (var file in Directory.GetFiles(source))
            {
                var name = global::System.IO.Path.GetFileName(file);
                if (string.Equals(name, "AskKey.exe", StringComparison.Ordinal)) continue;
                File.Copy(file, global::System.IO.Path.Combine(runtime, name));
            }
            _sharedRuntime = runtime;
            return runtime;
        }
    }

    public static bool IsPrivateFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        FileSecurity security = info.GetAccessControl();
        if (!security.AreAccessRulesProtected) return false;
        var current = BrokerAcl.CurrentUser;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!rule.IdentityReference.Equals(current)) return false;
        }
        return true;
    }
}

