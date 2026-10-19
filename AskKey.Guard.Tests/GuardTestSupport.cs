using System.Security.Cryptography;
using System.Security.Principal;

namespace AskKey.Guard.Tests;

internal static class GuardTestSupport
{
    public static string CurrentSid
        => WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("current user SID unavailable");

    public static GuardCaller CurrentCaller() => new(
        (uint)Environment.ProcessId,
        CurrentSid,
        Environment.ProcessPath ?? "testhost.exe");

    public static string NewPipeName() => "askkey-guard-test-" + Guid.NewGuid().ToString("N");

    public static byte[] NewProof() => HMACSHA256.HashData(
        RandomNumberGenerator.GetBytes(32),
        "AskKey Guard enroll v1"u8.ToArray());

    public static byte[] Sign(RSA key, byte[] data, RSASignaturePadding padding)
        => key.SignData(data, HashAlgorithmName.SHA256, padding);
}

internal sealed class FixedCallerVerifier : IGuardCallerVerifier
{
    private readonly GuardCaller? _caller;

    public FixedCallerVerifier(GuardCaller? caller) => _caller = caller;

    public GuardCaller? Verify(uint processId)
        => _caller is null ? null : _caller with { ProcessId = processId };
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "askkey-guard-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class GuardTestServer : IDisposable
{
    public GuardTestServer(
        IGuardCallerVerifier? verifier = null,
        TimeProvider? timeProvider = null)
    {
        Directory = new TempDirectory();
        Sid = GuardTestSupport.CurrentSid;
        var pipeName = GuardTestSupport.NewPipeName();
        Store = new GuardStateStore(Directory.Path);
        Server = new GuardServer(
            Store,
            verifier ?? new FixedCallerVerifier(GuardTestSupport.CurrentCaller()),
            pipeName,
            timeProvider);
        Server.Start();
        Client = new GuardClient(pipeName);
    }

    public TempDirectory Directory { get; }
    public string Sid { get; }
    public GuardStateStore Store { get; }
    public GuardServer Server { get; }
    public GuardClient Client { get; }

    public void Dispose()
    {
        Server.Dispose();
        Directory.Dispose();
    }
}
