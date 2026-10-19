using System.IO.Pipes;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Guard;

public sealed class GuardClientException : Exception
{
    public GuardClientException(string message) : base(message) { }
}

/// <summary>One connection per request to the fixed per-machine guard pipe.</summary>
public sealed class GuardClient
{
    private const string LocalServiceSid = "S-1-5-19";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private readonly string _pipeName;
    private readonly bool _development;
    private readonly string? _expectedServerPath;

    public GuardClient(
        string? pipeName = null,
        bool? development = null,
        string? expectedServerPath = null)
    {
        _pipeName = pipeName ?? GuardProtocol.DefaultPipeName;
        if (!BrokerPipeName.IsValid(_pipeName)) throw new ArgumentException("pipe name", nameof(pipeName));
        // Custom pipes exist for tests only; the installed app always uses the fixed
        // production name, which is the one that must verify the service identity.
        _development = development
            ?? GuardProtocol.IsDevelopmentEnvironment
            || !string.Equals(_pipeName, GuardProtocol.PipeName, StringComparison.Ordinal);
        _expectedServerPath = _development
            ? null
            : expectedServerPath ?? ExpectedServicePath();
    }

    public string PipeName => _pipeName;

    public GuardResponse Status() => Send(new GuardRequest { Operation = GuardOperations.Status });

    public GuardResponse Challenge() => Send(new GuardRequest { Operation = GuardOperations.Challenge });

    public GuardResponse Enroll(byte[] publicKey, byte[] nonce, byte[] signature, byte[] proof)
        => Send(new GuardRequest
        {
            Operation = GuardOperations.Enroll,
            PublicKey = publicKey,
            Nonce = nonce,
            Signature = signature,
            Proof = proof,
        });

    public GuardResponse Unlock(byte[] nonce, byte[] signature)
        => Send(new GuardRequest
        {
            Operation = GuardOperations.Unlock,
            Nonce = nonce,
            Signature = signature,
        });

    public GuardResponse Unenroll(byte[] proof)
        => Send(new GuardRequest
        {
            Operation = GuardOperations.Unenroll,
            Proof = proof,
        });

    public GuardResponse Send(GuardRequest request)
    {
        var encoded = GuardJson.EncodeRequest(request);
        try
        {
            if (encoded.Length > GuardProtocol.MaximumFrameBytes)
            {
                throw new GuardClientException("guard request exceeds the frame limit");
            }

            using var pipe = Connect();
            if (!GuardFraming.WriteAsync(pipe, encoded).GetAwaiter().GetResult())
            {
                throw new GuardClientException("AskKey Guard rejected the connection.");
            }
            var frame = GuardFraming.ReadAsync(pipe, ReadTimeout).GetAwaiter().GetResult();
            if (frame.Kind != GuardFrameReadKind.Success || frame.Data is null)
            {
                throw new GuardClientException("AskKey Guard returned a malformed response.");
            }
            try
            {
                return GuardJson.DecodeResponse(frame.Data)
                    ?? throw new GuardClientException("AskKey Guard returned a malformed response.");
            }
            finally
            {
                Array.Clear(frame.Data);
            }
        }
        finally
        {
            Array.Clear(encoded);
        }
    }

    private NamedPipeClientStream Connect()
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
            VerifyServer(pipe);
            return pipe;
        }
        catch (TimeoutException)
        {
            pipe.Dispose();
            throw new GuardClientException("AskKey Guard is not running.");
        }
        catch (IOException)
        {
            pipe.Dispose();
            throw new GuardClientException("AskKey Guard is not running.");
        }
        catch (UnauthorizedAccessException)
        {
            pipe.Dispose();
            throw new GuardClientException("AskKey Guard denied the connection.");
        }
    }

    private void VerifyServer(NamedPipeClientStream pipe)
    {
        if (_development) return;
        if (!GuardProcess.TryGetServerProcessId(pipe, out var processId)
            || !GuardProcess.TryGetServiceProcessId(GuardProtocol.ServiceName, out var serviceProcessId)
            || processId != serviceProcessId)
        {
            pipe.Dispose();
            throw new GuardClientException("AskKey Guard server identity is not trusted.");
        }

        // These queries can be denied for a LocalService process. The SCM PID match
        // is the hard check; use the extra facts when Windows lets us read them.
        var imagePath = GuardProcess.ImagePath(processId);
        var sid = GuardProcess.UserSid(processId);
        if ((imagePath is not null
                && !string.Equals(imagePath, _expectedServerPath, StringComparison.OrdinalIgnoreCase))
            || (sid is not null && !string.Equals(sid, LocalServiceSid, StringComparison.Ordinal)))
        {
            pipe.Dispose();
            throw new GuardClientException("AskKey Guard server identity is not trusted.");
        }
    }

    /// <summary>
    /// The service sits beside Velopack's <c>current</c> payload, not inside it, so
    /// an app update cannot replace a running service binary.
    /// </summary>
    internal static string ExpectedServicePath(string? applicationDirectory = null)
    {
        var application = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory));
        var root = string.Equals(
                Path.GetFileName(application),
                OfficialInstallTopology.PayloadDirectoryName,
                StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(application) ?? application
            : application;
        return Path.Combine(root, "Guard", "AskKey.Guard.Service.exe");
    }
}
