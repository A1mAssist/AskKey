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

    public void Unlock()
    {
        lock (_lifecycleLock)
        {
            RecoverInterruptedLocalErase();
            OpenBootstrappedLibrary(_configuration);
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
        lock (_stateLock)
        {
            _key = null;
            _store?.BindCredentialAuthenticationKey(null);
            _managementSessionExpiresAt = null;
        }
    }

    public bool HasActiveManagementSession
    {
        get
        {
            lock (_stateLock) return IsManagementSessionValidLocked();
        }
    }

    public void BeginManagementSession(ManagementAuthenticator authenticator)
    {
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
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

    internal static void AuthorizeManagement(ManagementAuthenticator authenticator, string reason)
    {
        if (!authenticator.Confirm(reason)) throw VaultException.ManagementAuthenticationRequired();
    }

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

    private void OpenBootstrappedLibrary(VaultConfiguration configuration)
    {
        if (Key is not null && _store is not null) return;
        var paths = new VaultBootstrapPaths(configuration.ApplicationSupportDirectory,
            Path.GetDirectoryName(configuration.ApplicationSupportDirectory));
        var (store, key) = VaultBootstrap.OpenCurrent(paths,
            ReferenceEquals(configuration, _configuration) ? _keyStore : DpapiAppKeyStore.ForConfiguration(configuration));
        Adopt(store, key);
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
