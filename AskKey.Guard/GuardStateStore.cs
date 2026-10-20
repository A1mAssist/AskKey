using System.Security.Cryptography;
using System.Text;

namespace AskKey.Guard;

/// <summary>
/// DPAPI-protected master key plus one consent enrollment per caller SID.
/// The master is protected under the service account; per-user KEKs are derived,
/// never stored.
/// </summary>
public sealed class GuardStateStore
{
    private const string KekInfo = "AskKey Guard v1";
    private const int KekBytes = 32;
    private const int MasterBytes = 32;
    private const int ProofBytes = 32;

    private readonly object _gate = new();
    private readonly bool _includeAdministrators;
    private byte[]? _master;

    public GuardStateStore(string rootPath, bool includeAdministrators = true)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("root path", nameof(rootPath));
        RootPath = Path.GetFullPath(rootPath);
        _includeAdministrators = includeAdministrators;
    }

    public static GuardStateStore ForCurrentEnvironment(bool development)
        => new(GuardPaths.DataRoot(development), includeAdministrators: !development);

    public string RootPath { get; }

    public bool IsEnrolled(string sid)
    {
        var path = ProofPath(sid);
        return File.Exists(path) && new FileInfo(path).Length == ProofBytes;
    }

    public byte[] DeriveUserKek(string sid)
    {
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(SafeSid(sid)));
        var info = Encoding.UTF8.GetBytes(KekInfo);
        var kek = new byte[KekBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, MasterKey(), kek, salt, info);
        return kek;
    }

    /// <summary>
    /// The first enrollment stores its proof. A later enrollment must present the
    /// same proof in constant time; a missing proof file fail-closes.
    /// </summary>
    public bool CanEnroll(string sid, ReadOnlySpan<byte> proof)
    {
        if (proof.Length != ProofBytes) return false;
        if (!File.Exists(ProofPath(sid))) return true;
        var stored = LoadProof(sid);
        if (stored is null) return false;
        try { return CryptographicOperations.FixedTimeEquals(stored, proof); }
        finally { CryptographicOperations.ZeroMemory(stored); }
    }

    public bool MatchesProof(string sid, ReadOnlySpan<byte> proof)
    {
        if (proof.Length != ProofBytes) return false;
        var stored = LoadProof(sid);
        if (stored is null) return false;
        try { return CryptographicOperations.FixedTimeEquals(stored, proof); }
        finally { CryptographicOperations.ZeroMemory(stored); }
    }

    /// <summary>Removes this SID's enrollment only when the stored vault proof matches.</summary>
    public bool TryUnenroll(string sid, ReadOnlySpan<byte> proof)
    {
        lock (_gate)
        {
            if (!File.Exists(ProofPath(sid))) return true;
            if (!MatchesProof(sid, proof)) return false;
            Directory.Delete(UserDirectory(sid), recursive: true);
            return true;
        }
    }

    /// <summary>Stores the consent enrollment for this SID.</summary>
    public bool TryEnroll(string sid, ReadOnlySpan<byte> proof)
    {
        if (proof.Length != ProofBytes) return false;
        lock (_gate)
        {
            var proofPath = ProofPath(sid);
            if (File.Exists(proofPath))
            {
                var stored = LoadProof(sid);
                if (stored is null) return false;
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(stored, proof)) return false;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(stored);
                }
            }

            var sidPath = UserDirectory(sid);
            EnsureDirectory(sidPath, includeAdministrators: false);
            WriteAtomic(proofPath, proof);
            GuardAcl.SecureFile(proofPath, includeAdministrators: false);
            return true;
        }
    }

    private byte[] MasterKey()
    {
        lock (_gate)
        {
            if (_master is not null) return _master;
            EnsureDirectory(RootPath, _includeAdministrators);
            var masterPath = Path.Combine(RootPath, "master.bin");
            if (File.Exists(masterPath))
            {
                var protectedBytes = File.ReadAllBytes(masterPath);
                var master = ProtectedData.Unprotect(
                    protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                if (master.Length != MasterBytes)
                {
                    CryptographicOperations.ZeroMemory(master);
                    throw new CryptographicException("guard master key has the wrong length");
                }
                _master = master;
            }
            else
            {
                var master = RandomNumberGenerator.GetBytes(MasterBytes);
                var protectedBytes = ProtectedData.Protect(
                    master, optionalEntropy: null, DataProtectionScope.CurrentUser);
                WriteAtomic(masterPath, protectedBytes);
                GuardAcl.SecureFile(masterPath, _includeAdministrators);
                _master = master;
            }
            return _master;
        }
    }

    private string ProofPath(string sid)
        => Path.Combine(UserDirectory(sid), "hello.proof");

    private byte[]? LoadProof(string sid)
    {
        var path = ProofPath(sid);
        if (!File.Exists(path)) return null;
        var proof = File.ReadAllBytes(path);
        return proof.Length == ProofBytes ? proof : null;
    }

    private string UserDirectory(string sid)
        => Path.Combine(RootPath, "users", SafeSid(sid));

    private void EnsureDirectory(string path, bool includeAdministrators)
    {
        Directory.CreateDirectory(path);
        GuardAcl.VerifyTrustedParent(path, includeAdministrators);
        GuardAcl.SecureDirectory(path, includeAdministrators);
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> data)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string SafeSid(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid)
            || sid.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new ArgumentException("invalid SID", nameof(sid));
        }
        return sid;
    }
}
