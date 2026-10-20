using System.Text;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Helper;

internal static class Program
{
    internal static int Main(string[] args)
    {
        try
        {
            return Dispatch(args);
        }
        catch (HelperUsageException)
        {
            HelperIO.WriteError(HelperCopy.Usage);
            return 1;
        }
        catch (HelperApprovalRequiredException)
        {
            HelperIO.WriteError(HelperCopy.ApprovalRequired);
            return 1;
        }
        catch (HelperWaitException error)
        {
            HelperIO.WriteError(error.Message);
            return 1;
        }
        catch (Exception error)
        {
            HelperIO.WriteError(error.Message);
            return 1;
        }
    }

    private static int Dispatch(string[] args)
    {
        if (args.Length == 0) throw new HelperUsageException();
        switch (args[0])
        {
            case "hook":
                return RunHook(args);
            case "mcp":
                if (args.Length != 1) throw new HelperUsageException();
                McpServer.Run();
                return 0;
            case "open":
                if (args.Length != 1) throw new HelperUsageException();
                OpenHost();
                return 0;
            case "run":
                return CliRunner.Run(args);
            case "health":
            case "version":
            case "status":
                if (args.Length != 1) throw new HelperUsageException();
                return PrintStatus(args[0]);
            default:
                throw new HelperUsageException();
        }
    }

    private static int RunHook(string[] args)
    {
        if (args.Length != 2) throw new HelperUsageException();
        var client = args[1];
        if (client == "capabilities")
        {
            HelperIO.WriteJson(new global::System.Text.Json.Nodes.JsonObject
            {
                ["protocolVersion"] = 1,
                ["clients"] = new global::System.Text.Json.Nodes.JsonArray("cursor", "grok", "claude", "codex"),
            });
            return 0;
        }
        if (client is not ("cursor" or "grok" or "claude" or "codex")) throw new HelperUsageException();
        CommandDiscoveryHook.Run(client);
        return 0;
    }

    private static int PrintStatus(string command)
    {
        var client = BrokerSocketClient.FromConfiguration();
        var response = client.Send(new BrokerRequest
        {
            Version = BrokerProtocolVersion.Current,
            Method = command == "status" ? "version" : command,
        });
        HelperIO.WriteBytes(BrokerJson.Encode(response), newline: true);
        return 0;
    }

    private static void OpenHost()
    {
        var helper = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        helper = Path.GetFullPath(helper);
        var host = HelperHostApplication.HostPath(helper);
        if (host is null
            || !File.Exists(host)
            || string.Equals(Path.GetFullPath(host), helper, StringComparison.OrdinalIgnoreCase))
        {
            throw HelperHostApplicationException.Unavailable;
        }
#if DEBUG
        HelperHostApplication.OpenHost(helper, isDevelopmentBuild: true);
#else
        HelperHostApplication.OpenHost(helper, isDevelopmentBuild: false);
#endif
    }
}

internal static class HelperIO
{
    private static Stream _out = Console.OpenStandardOutput();
    private static Stream _error = Console.OpenStandardError();

    public static Stream StandardOutput => _out;
    public static Stream StandardError => _error;

    internal static void UseStreams(Stream output, Stream error)
    {
        _out = output;
        _error = error;
    }

    internal static void ResetStreams()
    {
        _out = Console.OpenStandardOutput();
        _error = Console.OpenStandardError();
    }

    public static void WriteJson(global::System.Text.Json.Nodes.JsonNode node)
        => WriteBytes(global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node), newline: true);

    public static void WriteBytes(byte[] bytes, bool newline)
    {
        _out.Write(bytes);
        if (newline) _out.WriteByte((byte)'\n');
        _out.Flush();
    }

    public static void WriteError(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        _error.Write(bytes);
        _error.Flush();
    }
}

internal sealed class HelperUsageException : Exception;

internal sealed class HelperApprovalRequiredException : Exception;

internal sealed class HelperWaitException : Exception
{
    public HelperWaitException(string message) : base(message) { }
}

internal static class HelperCopy
{
    public const string Usage =
        "Usage: askkey health | version | status | mcp | open | run --credential <name> [--credential <name> ...] [--wait-for-approval] [--operation-id <id>] [--caller-name <name>] [--caller-purpose <purpose>] -- <command> [args...]";

    public const string ApprovalRequired =
        "Ask Key approval is required. Retry with the same --operation-id after approval.";

    public const string OutcomeUnknown =
        "Ask Key could not determine whether the target started; it was not retried.";

    public const string Waiting =
        "Ask Key is waiting for approval. Keep this process running; it will continue after approval.";

    public const string WaitStoppedFormat =
        "Ask Key approval wait stopped ({0}); the command was not resumed.";

    public const string WaitExpired =
        "Ask Key approval wait expired; the command was not resumed.";

    public const string WaitInvalid =
        "Ask Key returned an invalid approval response; the command was not resumed.";

    public const string OpenFailed = "Ask Key could not open the host application.";
}
