using System.Security.Cryptography;
using System.Text;
using AskKey.Broker.Approval;
using AskKey.Broker.Transport;

namespace AskKey.Vault;

/// <summary>
/// Vault façade. Ported from Vault.swift: the store/key are lock-guarded, the
/// management session is an idle deadline, and every credential mutation is
/// serialized against Agent operations through <see cref="AgentAccessGate"/>.
/// </summary>
public sealed partial class Vault : IDisposable
{
    private static readonly object SharedGate = new();
    private static Vault? _shared;
    private static bool _sharedResolved;

    public TimeSpan ManagementSessionIdleLimit { get; set; } = TimeSpan.FromMinutes(5);

    public BrokerRequestRegistry BrokerRequests { get; } = new();
    public BrokerApprovalStateMachine ApprovalRequests { get; }
    public AgentAccessGate AgentAccessGate { get; } = new();
    public FileDeliveryManagerRegistry FileDeliveryManager { get; }
    public bool IsLocked => Key is null;

    internal FrozenAgentTextWriteRegistry AgentTextWrites { get; } = new();
    internal FrozenAgentOrganizationRegistry AgentOrganizations { get; } = new();

    private readonly object _stateLock = new();
    private readonly object _lifecycleLock = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly VaultConfiguration _configuration;
    private readonly IAppKeyStore _keyStore;
    private VaultStore? _store;
    private byte[]? _key;
    private DateTimeOffset? _managementSessionExpiresAt;
    private bool _accessRecordWriteFailed;
    private bool _disposed;

    public static Vault Shared
    {
        get
        {
            lock (SharedGate)
            {
                _shared ??= new Vault();
                _sharedResolved = true;
                return _shared;
            }
        }
    }

    /// <summary>Test/executable seam: supply the shared instance once, before first use.</summary>
    public static void ConfigureShared(Vault vault)
    {
        lock (SharedGate)
        {
            if (_sharedResolved || _shared is not null)
            {
                throw new InvalidOperationException("The shared vault was already resolved.");
            }
            _shared = vault;
        }
    }

    /// <summary>Test seam: build a Vault over an explicit store, key, clock and delivery root.</summary>
    public Vault(
        VaultStore store,
        byte[]? key = null,
        Func<DateTimeOffset>? now = null,
        BrokerApprovalStateMachine? approvalRequests = null,
        string? deliveryRoot = null,
        VaultConfiguration? configuration = null,
        IAppKeyStore? keyStore = null)
    {
        _store = store;
        _key = key;
        _clock = now ?? (() => DateTimeOffset.UtcNow);
        _configuration = configuration ?? VaultConfiguration.Default;
        _keyStore = keyStore ?? DpapiAppKeyStore.ForConfiguration(_configuration);
        ApprovalRequests = approvalRequests ?? new BrokerApprovalStateMachine(clock: _clock);
        FileDeliveryManager = new FileDeliveryManagerRegistry(new FileDeliveryManager(
            deliveryRoot ?? Path.Combine(Path.GetTempPath(), "AskKeyTests", Guid.NewGuid().ToString("N"))));
        ConfigureAgentTextWriteCleanup();
        if (key is not null)
        {
            try
            {
                SynchronizeAgentAccessState();
            }
            catch
            {
                AgentAccessGate.Invalidate();
            }
        }
    }

    private Vault()
    {
        _clock = () => DateTimeOffset.UtcNow;
        _configuration = VaultConfiguration.Default;
        _keyStore = DpapiAppKeyStore.ForConfiguration(_configuration);
        ApprovalRequests = new BrokerApprovalStateMachine(clock: _clock);
        FileDeliveryManager = new FileDeliveryManagerRegistry(() => new FileDeliveryManager(
            Path.Combine(_configuration.DeliveryDirectory, Guid.NewGuid().ToString("N"))));
        ConfigureAgentTextWriteCleanup();
    }

    internal DateTimeOffset CurrentDate => _clock();

    internal byte[]? Key
    {
        get
        {
            lock (_stateLock) return _key;
        }
    }

    internal byte[] RequireKey()
        => Key ?? throw VaultException.Locked();

    internal VaultStore Store
    {
        get
        {
            lock (_stateLock) return _store ?? throw VaultException.Locked();
        }
    }

    internal void NotifySnapshotRelevantChange() => SnapshotChanged?.Invoke();

    /// <summary>Raised after any change that affects the Agent-visible credential snapshot.</summary>
    public event Action? SnapshotChanged;

    // MARK: - Lifecycle

    /// <summary>
    /// Opens the library. In high security mode the caller must supply the passphrase;
    /// a null passphrase fails with <see cref="AppKeyStoreError.PassphraseRequired"/>
    /// instead of silently falling back to a weaker store.
    /// </summary>
    public void Unlock(string? passphrase = null)
    {
        lock (_lifecycleLock)
        {
            RecoverInterruptedLocalErase();
            OpenBootstrappedLibrary(_configuration, passphrase);
        }
    }

    /// <summary>True when a passphrase or Windows Hello guard protects the vault key.</summary>
    public bool IsHighSecurityEnabled
        => IsPassphraseProtected || IsGuardUnlockEnabled;

    /// <summary>
    /// Re-wraps the in-memory vault key with a passphrase and removes the DPAPI copy.
    /// The vault must be unlocked. The wrapped file is written and verified first, so a
    /// failure leaves the existing DPAPI key intact.
    /// </summary>
    public void EnableHighSecurity(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("Passphrase is required.", nameof(passphrase));
        lock (_lifecycleLock)
        {
            var key = RequireKey();
            var directory = _configuration.KeyMaterialDirectory;
            var wrappedWritten = false;
            try
            {
                PassphraseAppKeyStore.Write(directory, PassphraseAppKeyStore.AppFileName, key, passphrase);
                wrappedWritten = true;
                PassphraseAppKeyStore.Verify(directory, key, passphrase);
                _keyStore.DeletePendingKey();
                _keyStore.DeleteAppKey();
                if (File.Exists(Path.Combine(directory, DpapiAppKeyStore.PendingFileName))
                    || File.Exists(Path.Combine(directory, DpapiAppKeyStore.AppFileName)))
                {
                    throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
                }
            }
            catch
            {
                // The wrapped copy must not survive a failed DPAPI removal: otherwise
                // IsHighSecurityEnabled would report a mode whose old key is still usable.
                if (wrappedWritten)
                {
                    try { PassphraseAppKeyStore.Delete(directory); }
                    catch { /* verified below */ }
                    if (File.Exists(Path.Combine(directory, PassphraseAppKeyStore.PendingFileName))
                        || File.Exists(Path.Combine(directory, PassphraseAppKeyStore.AppFileName)))
                    {
                        throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
                    }
                }
                throw;
            }
        }
    }

    /// <summary>
    /// Rewraps the same vault key with a new passphrase. The current passphrase must
    /// verify first; a failed rewrite leaves the existing wrapped file untouched.
    /// </summary>
    public void ChangePassphrase(string currentPassphrase, string newPassphrase)
    {
        if (string.IsNullOrEmpty(currentPassphrase))
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }
        if (string.IsNullOrWhiteSpace(newPassphrase) || newPassphrase.Length < 8)
        {
            throw new ArgumentException("Use at least 8 characters.", nameof(newPassphrase));
        }
        lock (_lifecycleLock)
        {
            if (!IsPassphraseProtected)
            {
                throw new InvalidOperationException("High security passphrase is not enabled.");
            }
            PassphraseAppKeyStore.Rewrite(_configuration.KeyMaterialDirectory, RequireKey(),
                currentPassphrase, newPassphrase);
        }
    }

    /// <summary>
    /// Unwraps with <paramref name="passphrase"/> and restores the DPAPI-protected key,
    /// then deletes the wrapped copy. A wrong passphrase changes nothing.
    /// </summary>
    public void DisableHighSecurity(string passphrase)
    {
        lock (_lifecycleLock)
        {
            var key = RequireKey();
            var directory = _configuration.KeyMaterialDirectory;
            PassphraseAppKeyStore.Verify(directory, key, passphrase);
            _keyStore.DeletePendingKey();
            _keyStore.SavePendingKey(key);
            _keyStore.PromotePendingKey();
            // Remove the guard copy before the passphrase copy: a crash in between
            // leaves the passphrase as the recovery path instead of a silent DPAPI fallback.
            GuardAppKeyStore.Delete(directory);
            PassphraseAppKeyStore.Delete(directory);
        }
    }

    internal void Unlock(VaultConfiguration configuration, IAppKeyStore keyStore)
    {
        lock (_lifecycleLock)
        {
            if (Key is not null && _store is not null) return;
            var paths = new VaultBootstrapPaths(configuration.ApplicationSupportDirectory,
                Path.GetDirectoryName(configuration.ApplicationSupportDirectory));
            var (store, key) = VaultBootstrap.OpenCurrent(paths, keyStore);
            Adopt(store, key);
        }
    }

    /// <summary>Loads only the App-owned current library for background Agent work.</summary>
    public void PrepareAgentRuntime()
        => Unlock();

    internal void PrepareAgentRuntime(VaultConfiguration configuration, IAppKeyStore keyStore)
        => Unlock(configuration, keyStore);

    public void Lock()
    {
        ApprovalRequests.RevokeAllTimedAllowances();
        // Delivered plaintext outliving the locked vault would defeat the lock.
        CleanupRuntimeFileDeliveries();
        byte[]? key;
        lock (_stateLock)
        {
            key = _key;
            _key = null;
            _store?.BindCredentialAuthenticationKey(null);
            _managementSessionExpiresAt = null;
        }
        // Drop the key material from memory instead of leaving it for the GC to move.
        if (key is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
    }

    public bool HasActiveManagementSession
    {
        get
        {
            lock (_stateLock) return IsManagementSessionValidLocked();
        }
    }

    public void SetManagementSessionIdleLimit(TimeSpan limit)
    {
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limit));
        ManagementSessionIdleLimit = limit;
        lock (_stateLock)
        {
            var now = _clock();
            if (_managementSessionExpiresAt is { } expires && now < expires)
            {
                _managementSessionExpiresAt = now + limit;
            }
        }
    }

    public void BeginManagementSession(ManagementAuthenticator authenticator,
        string reason = CredentialManagementCopy.ManageReason)
    {
        AuthorizeManagement(authenticator, reason);
        _ = RequireKey();
        lock (_stateLock) _managementSessionExpiresAt = _clock() + ManagementSessionIdleLimit;
    }

    public void BeginOnboardingManagementSession() => BeginOnboardingCreation();

    /// <summary>Validates first creation without granting list/reveal/settings access.</summary>
    public void BeginOnboardingCreation()
    {
        _ = RequireKey();
        var wasPaused = AgentAccessGate.BeginExclusiveChange();
        try
        {
            if (Store.FetchAllCredentials().Count > 0 || Store.FetchRecycledCredentials().Count > 0)
            {
                throw VaultException.ManagementAuthenticationRequired();
            }
        }
        finally
        {
            AgentAccessGate.EndExclusiveChange(paused: wasPaused);
        }
    }

    public void EndManagementSession()
    {
        lock (_stateLock) _managementSessionExpiresAt = null;
    }

    internal void RequireManagementSession()
    {
        lock (_stateLock)
        {
            if (!IsManagementSessionValidLocked())
            {
                _managementSessionExpiresAt = null;
                throw VaultException.ManagementAuthenticationRequired();
            }
            _managementSessionExpiresAt = _clock() + ManagementSessionIdleLimit;
        }
    }

    private bool IsManagementSessionValidLocked()
        => _managementSessionExpiresAt is { } expires && _clock() < expires;

    internal void AuthorizeManagement(ManagementAuthenticator authenticator, string reason)
    {
        // A valid management session already represents a recent Windows Hello
        // confirmation; the configured idle window is the reuse period. Operations that
        // hand an Agent plaintext or grant a lasting permission still ask again.
        if (!RequiresFreshAuthentication(reason))
        {
            lock (_stateLock)
            {
                if (IsManagementSessionValidLocked()) return;
            }
        }
        if (!authenticator.Confirm(reason)) throw VaultException.ManagementAuthenticationRequired();
    }

    /// <summary>Reasons that must never inherit an open session's approval.</summary>
    public static bool RequiresFreshAuthentication(string reason)
        => reason is CredentialManagementCopy.RevealReason
            or CredentialManagementCopy.FrozenWriteRevealReason
            or CredentialManagementCopy.AgentWriteApprovalReason
            or CredentialManagementCopy.AllowPermissionReason;

    /// <summary>True when this edit grants Allow to a credential that does not already have it.</summary>
    private static bool GrantsAllowed(CredentialRecord? existing, CredentialPermission permission)
        => permission == CredentialPermission.Allowed
            && (!CredentialPermissionExtensions.TryParse(existing?.Permission ?? "", out var current)
                || current != CredentialPermission.Allowed);

    internal bool AccessRecordWriteFailed
    {
        get
        {
            lock (_stateLock) return _accessRecordWriteFailed;
        }
        set
        {
            lock (_stateLock) _accessRecordWriteFailed = value;
        }
    }

    internal void SetAccessRecordWriteFailed(bool failed) => AccessRecordWriteFailed = failed;

    private void OpenBootstrappedLibrary(VaultConfiguration configuration, string? passphrase = null)
    {
        if (Key is not null && _store is not null) return;
        var paths = new VaultBootstrapPaths(configuration.ApplicationSupportDirectory,
            Path.GetDirectoryName(configuration.ApplicationSupportDirectory));
        var highSecurity = PassphraseAppKeyStore.IsEnabled(configuration.KeyMaterialDirectory);
        var dpapiStore = ReferenceEquals(configuration, _configuration)
            ? _keyStore
            : DpapiAppKeyStore.ForConfiguration(configuration);
        var store = dpapiStore;
        if (highSecurity)
        {
            if (string.IsNullOrEmpty(passphrase)) throw new AppKeyStoreException(AppKeyStoreError.PassphraseRequired);
            store = new PassphraseAppKeyStore(configuration.KeyMaterialDirectory,
                new FixedPassphraseProvider(passphrase));
        }
        try
        {
            var (vaultStore, key) = VaultBootstrap.OpenCurrent(paths, store);
            Adopt(vaultStore, key);
            if (highSecurity) RemoveLeftoverDpapiCopies(configuration, dpapiStore);
        }
        finally
        {
            (store as PassphraseAppKeyStore)?.ClearSecret();
        }
    }

    /// <summary>
    /// A leftover DPAPI slot defeats high security: it is the old unwrapped key that any
    /// process running as this user can still decrypt. A proof-of-possession unlock (the
    /// passphrase or the guard) removes it; a leftover is a hard failure, never a downgrade.
    /// </summary>
    private void RemoveLeftoverDpapiCopies(VaultConfiguration configuration, IAppKeyStore dpapiStore)
    {
        try
        {
            dpapiStore.DeletePendingKey();
            dpapiStore.DeleteAppKey();
        }
        catch
        {
            Lock();
            throw;
        }
        if (File.Exists(Path.Combine(configuration.KeyMaterialDirectory, DpapiAppKeyStore.PendingFileName))
            || File.Exists(Path.Combine(configuration.KeyMaterialDirectory, DpapiAppKeyStore.AppFileName)))
        {
            Lock();
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }
    }

    internal VaultConfiguration Configuration => _configuration;

    internal VaultBootstrapPaths BootstrapPaths => new(_configuration.ApplicationSupportDirectory,
        Path.GetDirectoryName(_configuration.ApplicationSupportDirectory));

    internal IAppKeyStore AppKeyStore => _keyStore;

    private void Adopt(VaultStore store, byte[] key)
    {
        VaultStore? previous;
        lock (_stateLock)
        {
            previous = _store;
            _store = store;
            _key = key;
            store.BindCredentialAuthenticationKey(key);
        }
        if (previous is not null && !ReferenceEquals(previous, store)) previous.Dispose();
        try
        {
            SynchronizeAgentAccessState();
        }
        catch
        {
            Lock();
            throw;
        }
    }

    internal void CloseStoreForLocalErase()
    {
        VaultStore? current;
        lock (_stateLock)
        {
            current = _store;
            _store = null;
        }
        current?.Dispose();
    }

    internal void FinishLocalErase()
    {
        lock (_stateLock)
        {
            _key = null;
            _managementSessionExpiresAt = null;
            _store = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ApprovalRequests.Dispose();
        FileDeliveryManager.Dispose();
        lock (_stateLock)
        {
            _store?.Dispose();
            _store = null;
            _key = null;
        }
    }

    // MARK: - Helpers

    internal static string Timestamp(DateTimeOffset value) => CredentialRecord.Timestamp(value);

    internal static string Sha256Hex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal DateTimeOffset ParseExpiry(string value)
        => CredentialRecord.ParseTimestamp(value, "Credential expiry is not a valid timestamp.");

    internal string? OptionalEnvironmentVariable(string? raw)
        => CredentialFieldValidation.OptionalEnvironmentVariable(raw);
}
