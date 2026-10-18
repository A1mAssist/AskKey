using Microsoft.UI.Dispatching;
using Microsoft.Win32;
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

    public event Action? PendingChanged;
    public event Action? CredentialsChanged;
    public event Action? AgentAccessChanged;
    public event Action? BrokerStateChanged;

    public string? BrokerFailure { get; private set; }
    public string? BrokerPipeName => _broker?.PipeName;

    public void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            Vault.Unlock();
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
            IsAgentAccessPaused = SafeIsAgentAccessPaused();
            RefreshPendingCount();
            PurgeExpiredCredentials();
        }
        catch (Exception error)
        {
            BrokerFailure = error.Message;
            ShowError(error.Message);
        }

        if (!_preferences.HasCompletedOnboarding
            || !Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase)))
        {
            ShowMainWindow();
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

    public void EraseLocalData(string confirmation, LocalVaultEraseLanguage language)
    {
        if (!ConfirmManagement("Erase the local Ask Key vault")) return;
        if (Vault.IsLocked) Vault.Unlock();
        if (!Vault.HasActiveManagementSession)
        {
            Vault.BeginManagementSession(new ManagementAuthenticator(_ => true));
        }
        Vault.EraseLocalLibrary(confirmation, language, new ManagementAuthenticator(_ => true));
        _expiryReminders?.ClearLedger();
        _preferences.HasCompletedOnboarding = false;
        _preferences.Save();
        Dialogs.ShowInfo("Local Ask Key data was erased. Ask Key will close.");
        Quit();
    }

    public bool ConfirmManagement(string reason)
        => _authentication.Confirm(Loc.T(reason));

    public ManagementAuthenticator ManagementAuthenticator(string reason)
        => new(_ => ConfirmManagement(reason));

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
        Vault.BeginManagementSession(ManagementAuthenticator(reason));
        return new global::AskKey.Vault.ManagementAuthenticator(_ => true);
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
        var authenticator = AuthenticateAndBeginManagement("View the credential change submitted for approval");
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
            ConfirmManagement(purpose == BrokerAuthenticationPurpose.ReadApproval
                ? "Approve this Agent read request"
                : "Approve this Agent write request"));
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
        if (_sessionLocked)
        {
            ShowLockedApprovalReminder();
            return;
        }
        _ = PresentApprovalAsync(pending);
    }

    private void PresentApproval(BrokerPendingApproval pending) => _ = PresentApprovalAsync(pending);

    private async Task PresentApprovalAsync(BrokerPendingApproval pending)
    {
        if (_sessionLocked || _activeApproval is not null) return;
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
        var runtimeDirectory = Path.GetDirectoryName(BrokerConfiguration.ResolvePipeNameFile())!;
        Directory.CreateDirectory(runtimeDirectory);
        var staging = Path.Combine(runtimeDirectory, "file-write-staging");
        _fileWrites = new BrokerFileWriteCoordinator(
            staging,
            Vault.ApprovalRequests,
            () => ConfirmManagement("View the frozen file submitted for approval"),
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
            fileWrite: request => _fileWrites.Handle(request),
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
        _tray = new TrayHost(ShowMainWindow, ShowPendingRequests, ShowSettings, ToggleAgentAccess, Quit);
        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += ShowMainWindow;
        _mainWindow = new MainWindow(this);
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
