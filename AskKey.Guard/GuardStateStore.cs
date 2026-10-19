using System.Security.Cryptography;
using System.Text;

namespace AskKey.Guard;

/// <summary>
/// DPAPI-protected master key plus one DER SubjectPublicKeyInfo per caller SID.
/// The master is protected under the service account; per-user KEKs are derived,
/// never stored.
/// </summary>
public sealed class GuardStateStore
{
    private const string KekInfo = "AskKey Guard v1";
    private const int KekBytes = 32;
    private const int MasterBytes = 32;

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
        var path = PublicKeyPath(sid);
        return File.Exists(path) && new FileInfo(path).Length > 0;
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
    /// The first enrollment stores its proof. Replacing an existing public key
    /// requires the same proof in constant time; a missing proof file fail-closes.
    /// </summary>
    public bool CanEnroll(string sid, ReadOnlySpan<byte> proof)
    {
        if (proof.Length != 32) return false;
        if (!File.Exists(PublicKeyPath(sid))) return true;
        var stored = LoadProof(sid);
        if (stored is null) return false;
        try { return CryptographicOperations.FixedTimeEquals(stored, proof); }
        finally { CryptographicOperations.ZeroMemory(stored); }
    }

    public bool MatchesProof(string sid, ReadOnlySpan<byte> proof)
    {
        if (proof.Length != 32) return false;
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
            if (!File.Exists(PublicKeyPath(sid))) return true;
            if (!MatchesProof(sid, proof)) return false;
            Directory.Delete(UserDirectory(sid), recursive: true);
            return true;
        }
    }

    public bool TryEnroll(string sid, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> proof)
    {
        if (publicKey.IsEmpty || proof.Length != 32) return false;
        lock (_gate)
        {
            var keyPath = PublicKeyPath(sid);
            if (File.Exists(keyPath))
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
            // Proof first: if the public key write fails, a retry with the same
            // proof is still accepted against the old key.
            var proofPath = Path.Combine(sidPath, "hello.proof");
            WriteAtomic(proofPath, proof);
            GuardAcl.SecureFile(proofPath, includeAdministrators: false);
            WriteAtomic(keyPath, publicKey);
            GuardAcl.SecureFile(keyPath, includeAdministrators: false);
            return true;
        }
    }

    public byte[]? LoadPublicKey(string sid)
    {
        var path = PublicKeyPath(sid);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
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

    private string PublicKeyPath(string sid)
        => Path.Combine(UserDirectory(sid), "hello.pub");

    private byte[]? LoadProof(string sid)
    {
        var path = Path.Combine(UserDirectory(sid), "hello.proof");
        if (!File.Exists(path)) return null;
        var proof = File.ReadAllBytes(path);
        return proof.Length == 32 ? proof : null;
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
