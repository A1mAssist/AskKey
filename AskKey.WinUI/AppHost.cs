using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using AskKey.Guard;
using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;
using AskKey.Vault;

namespace AskKeyApp;

/// <summary>Owns the App process: vault lifecycle, Broker, approval queue, tray and hotkey.</summary>
public sealed class AppHost : IDisposable
{
    public static AppHost Current { get; private set; } = null!;

    private readonly DispatcherQueue _dispatcher;
    private readonly Preferences _preferences = new();
    private readonly ManagementPrompt _authentication = new();
    private MainWindow? _mainWindow;
    private TrayHost? _tray;
    private GlobalHotkey? _hotkey;
    private CredentialExpiryReminderHost? _expiryReminders;
    private BrokerSocketServer? _broker;
    private BrokerFileWriteCoordinator? _fileWrites;
    private BrokerTextRuntime? _textRuntime;
    private SessionSwitchEventHandler? _sessionSwitchHandler;
    private ApprovalWindow? _activeApproval;
    private bool _sessionLocked;
    private bool _lockedApprovalReminderPosted;
    private bool _disposed;
    private bool _started;
    private int _pendingCount;

    public AppHost(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Current = this;
        Loc.LanguageChanged += OnLanguageChanged;
    }

    public global::AskKey.Vault.Vault Vault { get; } = global::AskKey.Vault.Vault.Shared;
    public Preferences Preferences => _preferences;
    public int PendingCount => _pendingCount;
    public bool IsAgentAccessPaused { get; private set; }

    /// <summary>True when high security mode prevented the automatic unlock at startup.</summary>
    public bool IsLockedAtStartup { get; private set; }

    public event Action? PendingChanged;
    public event Action? CredentialsChanged;
    public event Action? AgentAccessChanged;
    public event Action? BrokerStateChanged;
    public event Action? SettingsChanged;

    public string? BrokerFailure { get; private set; }
    public string? BrokerPipeName => _broker?.PipeName;

    public void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            try
            {
                Vault.Unlock();
            }
            catch (AppKeyStoreException error) when (error.Error is AppKeyStoreError.PassphraseRequired
                                                      or AppKeyStoreError.PassphraseCancelled)
            {
                // High security mode: start the tray and Broker but leave the vault locked
                // until the user enters the passphrase. Agent requests fail closed until then.
                IsLockedAtStartup = true;
            }
            Vault.SetManagementSessionIdleLimit(TimeSpan.FromSeconds(_preferences.SessionTimeoutSeconds));
            Vault.UpdateDefaultTimedAllowanceMinutes(_preferences.DefaultTimedAllowanceMinutes);
            Vault.ApprovalRequests.SetReadAuthenticationEnabled(_preferences.ReadApprovalAuthenticationEnabled);
            ConfigureApprovals();
            StartBroker();
            StartTrayAndHotkey();
            StartSessionPrivacyMonitor();
            _expiryReminders = new CredentialExpiryReminderHost(
                Vault, _dispatcher, (title, text) => _tray?.ShowNotification(title, text, ShowCredentials));
            _expiryReminders.Start();
            IsAgentAccessPaused = IsLockedAtStartup || SafeIsAgentAccessPaused();
            _tray?.UpdatePaused(IsAgentAccessPaused);
            RefreshPendingCount();
            PurgeExpiredCredentials();
        }
        catch (Exception error)
        {
            BrokerFailure = error.Message;
            ShowError(error.Message);
        }

        var background = Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        var showWindow = !_preferences.HasCompletedOnboarding || !background;
        if (showWindow) ShowMainWindow();
        if (!IsLockedAtStartup) return;
        if (showWindow)
        {
            // High security mode: the vault is locked until the passphrase arrives, so ask
            // before the user starts browsing. Cancel leaves it locked and the page offers
            // the same Unlock button.
            _ = _dispatcher.TryEnqueue(async () => await UnlockAsync());
        }
        else
        {
            // A background start must not pop a dialog, but the user should know why
            // Agent requests fail until they open the app.
            _tray?.ShowNotification("Ask Key is locked",
                "Open Ask Key and enter your passphrase to unlock.", ShowMainWindow);
        }
    }

    public void ShowMainWindow()
    {
        _mainWindow ??= new MainWindow(this);
        _mainWindow.ShowFromTray();
    }

    public void ShowPendingRequests()
    {
        ShowMainWindow();
        _mainWindow?.ShowActivity(ActivitySection.Pending);
    }

    public void ShowSettings()
    {
        ShowMainWindow();
        _mainWindow?.ShowSettings();
    }

    public void ShowCredentials()
    {
        ShowMainWindow();
        _mainWindow?.ShowCredentials();
    }

    public void ShowAgentAccess()
    {
        ShowMainWindow();
        _mainWindow?.ShowAgentAccess();
    }

    public void CompleteOnboarding(bool launchAtLogin)
    {
        _preferences.HasCompletedOnboarding = true;
        _preferences.LaunchAtLogin = launchAtLogin;
        _preferences.Save();
        try
        {
            StartupRegistration.Apply(launchAtLogin);
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    public void HideMainWindow() => _mainWindow?.HideToTray();

    public void Quit()
    {
        _mainWindow?.AllowClose();
        App.Current.Shutdown();
    }

    public async Task EraseLocalDataAsync(string confirmation, LocalVaultEraseLanguage language)
    {
        // Erasing is irreversible and must never inherit an open management session's
        // approval, so this asks Windows Hello even while a session is valid.
        if (!_authentication.Confirm(Loc.T("Erase the local Ask Key vault"))) return;
        if (!await UnlockAsync()) return;
        if (!Vault.HasActiveManagementSession)
        {
            Vault.BeginManagementSession(new ManagementAuthenticator(_ => true));
        }
        var helloKeyName = TryGetGuardHelloKeyName();
        var unenrolled = await TryUnenrollGuardAsync();
        if (!string.IsNullOrEmpty(helloKeyName)) await HelloKeyManager.DeleteAsync(helloKeyName);
        Vault.DisableGuardUnlock();
        Vault.EraseLocalLibrary(confirmation, language, new ManagementAuthenticator(_ => true));
        _expiryReminders?.ClearLedger();
        _preferences.HasCompletedOnboarding = false;
        _preferences.Save();
        // Erase replaces the vault key, so an enrollment the service still holds can never
        // match a new one. Say so instead of leaving the user to find it out later.
        Dialogs.ShowInfo(unenrolled || helloKeyName is null or ""
            ? "Local Ask Key data was erased. Ask Key will close."
            : Loc.F("Local Ask Key data was erased, but the Ask Key Guard service still holds "
                + "this account's Windows Hello enrollment. Windows Hello unlock cannot be "
                + "enabled again on this PC until an administrator removes {0}.",
                GuardPaths.DataRoot(GuardProtocol.IsDevelopmentEnvironment)
                    + "\\users\\" + (CurrentUserSid() ?? "<your account>")));
        Quit();
    }

    public bool ConfirmManagement(string reason)
        => !ManagementAuthenticationPolicy.RequiresConfirmation(Vault.HasActiveManagementSession, reason)
            || _authentication.Confirm(Loc.T(reason));

    public ManagementAuthenticator ManagementAuthenticator(string _)
        => ManagementAuthenticationPolicy.From(
            reason => _authentication.Confirm(Loc.T(reason)),
            () => Vault.HasActiveManagementSession);

    public bool EnsureManagementSession(string reason)
    {
        try
        {
            if (Vault.HasActiveManagementSession) return true;
            AuthenticateAndBeginManagement(reason);
            return Vault.HasActiveManagementSession;
        }
        catch (Exception error)
        {
            ShowError(error.Message);
            return false;
        }
    }

    public ManagementAuthenticator AuthenticateAndBeginManagement(string reason)
    {
        if (Vault.IsLocked) Vault.Unlock();
        var authenticator = ManagementAuthenticator(reason);
        Vault.BeginManagementSession(authenticator, reason);
        return authenticator;
    }

    /// <summary>
    /// Opens the vault with the guard service when enrolled, then falls back to the
    /// passphrase. A failed Hello attempt leaves the vault locked unless the user
    /// completes the passphrase fallback.
    /// </summary>
    public async Task<bool> UnlockAsync()
    {
        if (!Vault.IsLocked) return true;
        if (await TryGuardUnlockAsync()) return true;

        string? passphrase = null;
        if (Vault.IsHighSecurityEnabled)
        {
            if (!Vault.IsPassphraseProtected)
            {
                ShowError("Windows Hello could not unlock Ask Key, and no passphrase copy is available.");
                return false;
            }
            passphrase = await Dialogs.PasswordAsync(_mainWindow, "Unlock Ask Key", "Enter your passphrase");
            if (passphrase is null) return false;
        }
        try
        {
            await Task.Run(() => Vault.Unlock(passphrase));
            // The passphrase is this launch's strong factor; open the management
            // window without immediately asking Hello a second time.
            if (Vault.IsHighSecurityEnabled)
                Vault.BeginManagementSession(new global::AskKey.Vault.ManagementAuthenticator(_ => true));
            IsLockedAtStartup = false;
            IsAgentAccessPaused = SafeIsAgentAccessPaused();
            _tray?.UpdatePaused(IsAgentAccessPaused);
            NotifyCredentialsChanged();
            RefreshPendingCount();
            AgentAccessChanged?.Invoke();
            return true;
        }
        catch (Exception error)
        {
            ShowError(error.Message);
            return false;
        }
    }

    /// <summary>Hello-unlocked path. Any failure or cancel falls through to the passphrase.</summary>
    private async Task<bool> TryGuardUnlockAsync()
    {
        if (!Vault.IsGuardUnlockEnabled) return false;
        var keyName = TryGetGuardHelloKeyName();
        if (string.IsNullOrEmpty(keyName)) return false;
        try
        {
            var guard = new GuardClient();
            var challenge = await Task.Run(guard.Challenge);
            if (!challenge.Ok || challenge.Nonce is null) return false;
            var signed = await HelloKeyManager.SignAsync(keyName, challenge.Nonce);
            if (!signed.Ok || signed.Signature is null) return false;
            var unlocked = await Task.Run(() => guard.Unlock(challenge.Nonce, signed.Signature));
            if (!unlocked.Ok || unlocked.Kek is null) return false;
            try
            {
                Vault.UnlockWithGuard(unlocked.Kek);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(unlocked.Kek);
            }
            Vault.BeginManagementSession(new global::AskKey.Vault.ManagementAuthenticator(_ => true));
            IsLockedAtStartup = false;
            IsAgentAccessPaused = SafeIsAgentAccessPaused();
            _tray?.UpdatePaused(IsAgentAccessPaused);
            NotifyCredentialsChanged();
            RefreshPendingCount();
            AgentAccessChanged?.Invoke();
            return true;
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
            return false;
        }
    }

    /// <summary>High security is confirmed with the passphrase; Hello is only the optional startup factor.</summary>
    public async Task EnableHighSecurityAsync()
    {
        if (Vault.IsHighSecurityEnabled) return;
        var passphrase = await Dialogs.PasswordAsync(_mainWindow, "Set a passphrase",
            "Use at least 8 characters", "Continue");
        if (passphrase is null) return;
        if (passphrase.Length < 8)
        {
            ShowError("Use at least 8 characters.");
            return;
        }
        var confirm = await Dialogs.PasswordAsync(_mainWindow, "Confirm the passphrase", "Enter it again", "Save");
        if (confirm is null) return;
        if (!string.Equals(passphrase, confirm, StringComparison.Ordinal))
        {
            ShowError("The passphrases do not match.");
            return;
        }
        try
        {
            await Task.Run(() => Vault.EnableHighSecurity(passphrase));
            var hello = await EnableGuardUnlockAsync();
            Dialogs.ShowInfo(hello.Ok
                ? "High security mode is on. Windows Hello unlocks Ask Key at startup; the passphrase remains the recovery."
                : "High security mode is on. Enter the passphrase at startup. Windows Hello unlock was not enabled.");
            SettingsChanged?.Invoke();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    public async Task DisableHighSecurityAsync()
    {
        if (!Vault.IsHighSecurityEnabled) return;
        var passphrase = await Dialogs.PasswordAsync(_mainWindow, "Confirm your passphrase",
            "Enter your passphrase", "Turn off");
        if (passphrase is null) return;
        var helloKeyName = TryGetGuardHelloKeyName();
        try
        {
            await Task.Run(() => Vault.DisableHighSecurity(passphrase));
            if (!string.IsNullOrEmpty(helloKeyName)) await TryUnenrollGuardAsync();
            Vault.DisableGuardUnlock();
            if (!string.IsNullOrEmpty(helloKeyName)) await HelloKeyManager.DeleteAsync(helloKeyName);
            Dialogs.ShowInfo("High security mode is off. Ask Key unlocks with Windows DPAPI again.");
            SettingsChanged?.Invoke();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    /// <summary>The SID the guard service files this account's enrollment under.</summary>
    private static string? CurrentUserSid()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? TryGetGuardHelloKeyName()
    {
        try { return Vault.GuardHelloKeyName; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Removes this account's guard enrollment before the vault key disappears.
    /// A failure here is not cosmetic: the service keeps the old vault-key proof,
    /// and erase replaces the vault key, so the stored proof could never match a
    /// new enrollment again. Callers that erase the vault must surface the failure.
    /// </summary>
    private async Task<bool> TryUnenrollGuardAsync()
    {
        if (Vault.IsLocked) return false;
        try
        {
            var guard = new GuardClient();
            var proof = Vault.CreateGuardEnrollmentProof();
            try
            {
                var response = await Task.Run(() => guard.Unenroll(proof));
                return response.Ok
                    || string.Equals(response.Error, GuardErrors.NotEnrolled, StringComparison.Ordinal);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(proof);
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
            return false;
        }
    }

    /// <summary>
    /// Enrolls a Hello key with the guard service and writes the guard copy of the
    /// vault key. High security stays enabled with the passphrase if this fails.
    /// </summary>
    public async Task<(bool Ok, string? Error)> EnableGuardUnlockAsync()
    {
        if (!Vault.IsHighSecurityEnabled) return (false, "Turn on high security mode first.");
        if (Vault.IsLocked) return (false, "Unlock Ask Key first.");
        if (!await HelloKeyManager.IsSupportedAsync())
            return (false, "Windows Hello is not available for this account.");

        var guard = new GuardClient();
        try
        {
            var status = await Task.Run(guard.Status);
            if (!status.Ok)
                return (false, "Ask Key Guard is not available. Install the per-machine package to use Windows Hello unlock.");
        }
        catch (Exception error)
        {
            return (false, error.Message);
        }

        var oldKeyName = TryGetGuardHelloKeyName();
        var keyName = "AskKey.Hello." + Guid.NewGuid().ToString("N");
        var keepKey = false;
        try
        {
            var created = await HelloKeyManager.CreateAsync(keyName);
            if (!created.Ok || created.PublicKey is null)
                return (false, created.Error ?? "Windows Hello is not available for this account.");
            var challenge = await Task.Run(guard.Challenge);
            if (!challenge.Ok || challenge.Nonce is null)
                return (false, "Ask Key Guard did not issue a challenge.");
            var signed = await HelloKeyManager.SignAsync(keyName, challenge.Nonce);
            if (!signed.Ok || signed.Signature is null)
                return (false, signed.Error ?? "Windows Hello signing failed.");
            var proof = Vault.CreateGuardEnrollmentProof();
            GuardResponse enrolled;
            try
            {
                enrolled = await Task.Run(() => guard.Enroll(
                    created.PublicKey, challenge.Nonce, signed.Signature, proof));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(proof);
            }
            if (!enrolled.Ok || enrolled.Kek is null)
                return (false, "Ask Key Guard rejected the enrollment.");
            try
            {
                Vault.EnableGuardUnlock(enrolled.Kek, keyName);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(enrolled.Kek);
            }
            keepKey = true;
            if (!string.IsNullOrEmpty(oldKeyName)
                && !string.Equals(oldKeyName, keyName, StringComparison.Ordinal))
            {
                await HelloKeyManager.DeleteAsync(oldKeyName);
            }
            SettingsChanged?.Invoke();
            return (true, null);
        }
        catch (Exception error)
        {
            return (false, error.Message);
        }
        finally
        {
            if (!keepKey) await HelloKeyManager.DeleteAsync(keyName);
        }
    }

    /// <summary>Removes the guard copy and its Hello key; the passphrase copy stays.</summary>
    public async Task DisableGuardUnlockAsync()
    {
        var keyName = TryGetGuardHelloKeyName();
        await TryUnenrollGuardAsync();
        Vault.DisableGuardUnlock();
        if (!string.IsNullOrEmpty(keyName)) await HelloKeyManager.DeleteAsync(keyName);
        SettingsChanged?.Invoke();
    }

    public void ToggleAgentAccess()
    {
        try
        {
            if (SafeIsAgentAccessPaused())
            {
                Vault.ResumeAgentAccess(ManagementAuthenticator(CredentialManagementCopy.ResumeReason));
                IsAgentAccessPaused = false;
            }
            else
            {
                Vault.PauseAgentAccess(ManagementAuthenticator(CredentialManagementCopy.PauseReason));
                IsAgentAccessPaused = true;
            }
            _tray?.UpdatePaused(IsAgentAccessPaused);
            AgentAccessChanged?.Invoke();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    public void RefreshPendingCount()
    {
        var count = Vault.ApprovalRequests.PendingRequests().Count;
        _pendingCount = count;
        if (count == 0) _lockedApprovalReminderPosted = false;
        _tray?.UpdatePendingCount(count);
        PendingChanged?.Invoke();
        if (count == 0) return;
        if (_sessionLocked)
        {
            ShowLockedApprovalReminder();
            return;
        }
        PresentNextApproval();
    }

    public BrokerCredentialWriteSummary? FrozenWriteSummary(BrokerPendingApproval pending)
    {
        try
        {
            return Vault.FrozenAgentWriteSummary(pending.Request.OperationId,
                pending.RequestId, pending.Capability);
        }
        catch (BrokerApprovalException)
        {
            return null;
        }
        catch (VaultException)
        {
            return null;
        }
    }

    public BrokerOrganizationSummary? FrozenOrganizationSummary(BrokerPendingApproval pending)
    {
        try
        {
            return Vault.FrozenAgentOrganizationSummary(pending.Request.OperationId,
                pending.RequestId, pending.Capability);
        }
        catch (BrokerApprovalException)
        {
            return null;
        }
        catch (VaultException)
        {
            return null;
        }
    }

    public BrokerFrozenFile RevealFileWrite(BrokerPendingApproval pending)
        => (_fileWrites ?? throw new BrokerFileWriteException(BrokerFileWriteError.RequestNotFound))
            .Reveal(pending.RequestId);

    public FrozenCredentialWrite RevealCredentialWrite(BrokerPendingApproval pending)
    {
        var authenticator = AuthenticateAndBeginManagement(CredentialManagementCopy.FrozenWriteRevealReason);
        return Vault.RevealFrozenCredentialWrite(pending.Request.OperationId,
            pending.RequestId, pending.Capability, authenticator);
    }

    public void NotifyCredentialsChanged()
    {
        CredentialsChanged?.Invoke();
        _mainWindow?.RefreshAll();
    }

    public void ShowError(string message) => Dialogs.ShowError(message);

    public void ApplyLanguage(string mode)
    {
        _preferences.LanguageMode = mode;
        _preferences.Save();
        Loc.SetMode(mode);
    }

    public void ApplyAppearance(string mode)
    {
        _preferences.AppearanceMode = mode;
        _preferences.Save();
        ThemeManager.Apply(mode, _mainWindow);
        _tray?.ApplyTheme(ThemeManager.IsDark);
    }

    private void OnLanguageChanged()
    {
        _mainWindow?.RefreshAll();
        _tray?.ApplyLanguage();
    }

    public void RetryBroker()
    {
        try
        {
            _broker?.Dispose();
            _broker = null;
            _fileWrites?.Dispose();
            _fileWrites = null;
            StartBroker();
            BrokerStateChanged?.Invoke();
        }
        catch (Exception error)
        {
            BrokerFailure = error.Message;
            ShowError(error.Message);
        }
    }

    public IReadOnlyList<BrokerPendingApproval> PendingApprovals()
        => Vault.ApprovalRequests.PendingRequests();

    public BrokerFileWriteSummary? FileWriteSummary(string requestId)
    {
        try
        {
            return _fileWrites?.Summary(requestId);
        }
        catch (BrokerFileWriteException)
        {
            return null;
        }
        catch (BrokerApprovalException)
        {
            return null;
        }
    }

    public void Decide(BrokerPendingApproval pending, BrokerApprovalDecision decision)
    {
        _ = Task.Run(() =>
        {
            try
            {
                Vault.ApprovalRequests.Decide(pending.RequestId, pending.Capability, decision);
                if (decision.Kind != BrokerApprovalDecisionKind.Deny
                    && FileWriteSummary(pending.RequestId) is { } summary)
                {
                    _fileWrites!.Commit(pending.RequestId, pending.Capability, summary.Digest);
                }
            }
            catch (BrokerApprovalException error) when (error.Error == BrokerApprovalError.AuthenticationFailed)
            {
                _dispatcher.TryEnqueue(() => PresentApproval(pending));
                return;
            }
            catch (Exception error)
            {
                _dispatcher.TryEnqueue(() => ShowError(error.Message));
            }
            _dispatcher.TryEnqueue(() =>
            {
                RefreshPendingCount();
                NotifyCredentialsChanged();
            });
        });
    }

    private void ConfigureApprovals()
    {
        Vault.ApprovalRequests.ConfigureAuthentication(purpose =>
            _authentication.Confirm(Loc.T(purpose == BrokerAuthenticationPurpose.ReadApproval
                ? "Approve this Agent read request"
                : global::AskKey.Vault.CredentialManagementCopy.AgentWriteApprovalReason)));
        Vault.ApprovalRequests.ConfigureObservers(
            _ => _dispatcher.TryEnqueue(() =>
            {
                if (_sessionLocked && _pendingCount > 0) ShowLockedApprovalReminder();
            }),
            _ => _dispatcher.TryEnqueue(RefreshPendingCount));
        Vault.ApprovalRequests.AddOperationStateChanged((_, _, _) =>
            _dispatcher.TryEnqueue(() =>
            {
                RefreshPendingCount();
                NotifyCredentialsChanged();
            }));
    }

    private async void PresentNextApproval()
    {
        var pending = Vault.ApprovalRequests.PendingRequests().FirstOrDefault();
        if (pending is not null) await PresentApprovalAsync(pending);
    }

    public void ReviewApproval(BrokerPendingApproval pending)
    {
        if (_sessionLocked || Vault.IsLocked)
        {
            ShowLockedApprovalReminder();
            return;
        }
        _ = PresentApprovalAsync(pending);
    }

    private void PresentApproval(BrokerPendingApproval pending) => _ = PresentApprovalAsync(pending);

    private async Task PresentApprovalAsync(BrokerPendingApproval pending)
    {
        if (_sessionLocked || Vault.IsLocked || _activeApproval is not null) return;
        _mainWindow ??= new MainWindow(this);
        var window = new ApprovalWindow(this, pending);
        WindowHelpers.SetOwner(window, _mainWindow);
        _activeApproval = window;
        try
        {
            await window.ShowAsync();
        }
        finally
        {
            _activeApproval = null;
        }
    }

    private void StartSessionPrivacyMonitor()
    {
        _sessionSwitchHandler = (_, args) => _dispatcher.TryEnqueue(() => ApplySessionSwitch(args.Reason));
        SystemEvents.SessionSwitch += _sessionSwitchHandler;
    }

    private void ApplySessionSwitch(SessionSwitchReason reason)
    {
        if (SessionPrivacyPolicy.HidesDetailedApprovals(reason))
        {
            var changed = !_sessionLocked;
            _sessionLocked = true;
            // The management window is user-presence bound; a screen lock or
            // disconnect ends it even though the vault key stays resident for the
            // Broker. The next management action must confirm again.
            Vault.EndManagementSession();
            if (changed) _activeApproval?.DismissForPrivacy();
            RefreshPendingCount();
            return;
        }

        if (!SessionPrivacyPolicy.RestoresDetailedApprovals(reason) || !_sessionLocked) return;
        _sessionLocked = false;
        _lockedApprovalReminderPosted = false;
        RefreshPendingCount();
    }

    private void ShowLockedApprovalReminder()
    {
        if (_lockedApprovalReminderPosted) return;
        _lockedApprovalReminderPosted = true;
        _tray?.ShowNotification(
            Loc.T("Ask Key needs your attention"),
            Loc.T("Open Ask Key to review pending requests."),
            ShowPendingRequests);
    }

    private void StartBroker()
    {
        // Outside development the host and helper must sit in an official install root
        // with the expected layout; a helper dropped next to a rewritten host fails here.
        _ = AppPaths.RequireOfficialHelperPath();
        var runtimeDirectory = Path.GetDirectoryName(BrokerConfiguration.ResolvePipeNameFile())!;
        Directory.CreateDirectory(runtimeDirectory);
        var staging = Path.Combine(runtimeDirectory, "file-write-staging");
        _fileWrites = new BrokerFileWriteCoordinator(
            staging,
            Vault.ApprovalRequests,
            () => _authentication.Confirm(Loc.T(
                global::AskKey.Vault.CredentialManagementCopy.FrozenWriteRevealReason)),
            file => Vault.CommitAgentFileWrite(file),
            (credentialId, previousDigest, request) =>
                Vault.SubmitFileWriteApprovalIfCurrent(credentialId, previousDigest, request),
            name => Vault.NormalizeAgentCreateCredentialName(name),
            credentialId => Vault.BrokerFileContentDigest(credentialId),
            completedFileCommit: (requestId, capability, digest) =>
                Vault.CompletedAgentFileWrite(requestId, capability, digest));
        _textRuntime = new BrokerTextRuntime((request, cancellation) =>
            MapProvider(() => Vault.BrokerTextCredentials(request, cancellation)));
        var handler = new BrokerRequestHandler(
            catalog: cancellation =>
            {
                try
                {
                    var catalog = Vault.BrokerCredentialCatalog(cancellation);
                    Vault.RecordCredentialAccess(CredentialAccessEvent.Create(
                        DateTimeOffset.UtcNow, null, CredentialAccessOperation.Catalog,
                        CredentialAccessResult.Allowed, null, null));
                    return catalog;
                }
                catch (VaultException error) when (error.Code == VaultErrorCode.AgentAccessPaused)
                {
                    Vault.RecordCredentialAccess(CredentialAccessEvent.Create(
                        DateTimeOffset.UtcNow, null, CredentialAccessOperation.Catalog,
                        CredentialAccessResult.Denied, null, null));
                    throw new BrokerProviderException(BrokerProviderError.AgentAccessPaused);
                }
            },
            requestStatus: (requestId, capability) =>
            {
                if (Vault.CommittedAgentFileWriteStatus(requestId, capability) is { } completed)
                    return completed;
                try
                {
                    return Vault.ApprovalRequests.Status(requestId, capability);
                }
                catch (BrokerApprovalException error) when (error.Error == BrokerApprovalError.RequestNotFound)
                {
                    return Vault.BrokerRequests.Status(requestId, capability);
                }
            },
            catalogGroups: cancellation => Vault.BrokerCredentialGroups(cancellation),
            cancelRequest: (requestId, capability) =>
            {
                try
                {
                    return Vault.ApprovalRequests.Cancel(requestId, capability);
                }
                catch (BrokerApprovalException error) when (error.Error == BrokerApprovalError.RequestNotFound)
                {
                    return Vault.BrokerRequests.Cancel(requestId, capability);
                }
            },
            textRun: (request, channel, cancellation) => _textRuntime.Run(request, channel, cancellation),
            fileWrite: request =>
            {
                if (Vault.IsLocked)
                    throw new BrokerFileWriteException(BrokerFileWriteError.AuthenticationFailed);
                return _fileWrites.Handle(request);
            },
            submitTextWrite: (request, cancellation) =>
            {
                cancellation.Check();
                return MapProvider(() => Vault.RequestAgentTextWrite(request,
                    reference => _fileWrites.ResolveComponent(reference, request.OperationId)));
            },
            commitTextWrite: (request, requestId, capability) =>
                MapProvider(() => Vault.CommitAgentTextWrite(request, requestId, capability)),
            cancelTextWrite: (operationId, requestId, capability) =>
                MapProvider(() => Vault.CancelAgentTextWrite(operationId, requestId, capability)));

        var pipeName = "askkey-" + Guid.NewGuid().ToString("N");
        _broker = new BrokerSocketServer(pipeName, runtimeDirectory, handler, _ => { });
        _broker.Start();
        BrokerFailure = null;
        BrokerStateChanged?.Invoke();
    }

    private static T MapProvider<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (VaultException error)
        {
            throw new BrokerProviderException(error.Code switch
            {
                VaultErrorCode.AgentAccessPaused => BrokerProviderError.AgentAccessPaused,
                VaultErrorCode.CredentialUnavailable or VaultErrorCode.CredentialNotFound
                    => BrokerProviderError.RequestRejected,
                VaultErrorCode.CredentialNameConflict or VaultErrorCode.InvalidCredentialName
                    => BrokerProviderError.InvalidRequest,
                _ => BrokerProviderError.RequestRejected,
            });
        }
        catch (BrokerApprovalException error)
        {
            throw new BrokerProviderException(error.Error switch
            {
                BrokerApprovalError.CapacityReached => BrokerProviderError.ResourceExhausted,
                BrokerApprovalError.RequestNotFound => BrokerProviderError.RequestNotFound,
                BrokerApprovalError.AgentAccessPaused => BrokerProviderError.AgentAccessPaused,
                _ => BrokerProviderError.InvalidRequest,
            });
        }
    }

    private void StartTrayAndHotkey()
    {
        _mainWindow = new MainWindow(this);
        _tray = new TrayHost(WindowHelpers.Handle(_mainWindow),
            ShowMainWindow, ShowPendingRequests, ShowSettings, ToggleAgentAccess, Quit);
        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += ShowMainWindow;
        var option = HotkeyOption.FromId(_preferences.HotkeyShortcut);
        if (!_hotkey.Register(option) && option != HotkeyOption.Disabled)
        {
            _mainWindow.HotkeyUnavailable();
        }
    }

    public void ApplyHotkey(string id)
    {
        _preferences.HotkeyShortcut = id;
        _preferences.Save();
        _hotkey?.Register(HotkeyOption.FromId(id));
    }

    private bool SafeIsAgentAccessPaused()
    {
        try
        {
            return Vault.IsAgentAccessPaused();
        }
        catch
        {
            return false;
        }
    }

    private void PurgeExpiredCredentials()
    {
        try
        {
            if (!Vault.IsLocked) Vault.PurgeExpiredRecycledCredentials();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loc.LanguageChanged -= OnLanguageChanged;
        if (_sessionSwitchHandler is not null)
        {
            SystemEvents.SessionSwitch -= _sessionSwitchHandler;
            _sessionSwitchHandler = null;
        }
        _expiryReminders?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _fileWrites?.Dispose();
        _broker?.Dispose();
        _mainWindow?.AllowClose();
        _mainWindow?.Close();
        Vault.Dispose();
    }
}
