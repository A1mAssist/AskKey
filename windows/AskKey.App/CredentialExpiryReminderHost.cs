using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using AskKey.Vault;

namespace AskKeyApp;

/// <summary>Shows seven-day expiry reminders through the tray and remembers delivered expiries.</summary>
internal sealed class CredentialExpiryReminderHost : IDisposable
{
    private static string Title => Loc.T("Ask Key has a credential expiring soon");
    private static string Body => Loc.T("Open Ask Key to review the expiry date.");
    private static string MultipleTitle => Loc.T("Ask Key has credentials expiring soon");
    private static string MultipleBody => Loc.T("Open Ask Key to review the expiry dates.");
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromDays(1);
    private static readonly TimeSpan DeliveryRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly global::AskKey.Vault.Vault _vault;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string, string> _notify;
    private readonly DispatcherTimer _timer;
    private readonly string _ledgerPath;
    private Dictionary<string, ExpiryReminderLedgerEntry> _ledger;
    private bool _started;
    private bool _disposed;
    private bool _reconciling;
    private bool _ledgerDirty;

    public CredentialExpiryReminderHost(global::AskKey.Vault.Vault vault, Dispatcher dispatcher,
        Action<string, string> notify)
    {
        _vault = vault;
        _dispatcher = dispatcher;
        _notify = notify;
        var directory = VaultConfiguration.Default.LocalApplicationDataDirectory;
        Directory.CreateDirectory(directory);
        _ledgerPath = Path.Combine(directory, "expiry-reminders.json");
        _ledger = LoadLedger();
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += OnTimerTick;
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _vault.SnapshotChanged += OnSnapshotChanged;
        Reconcile();
    }

    private void OnSnapshotChanged()
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(Reconcile);
    }

    private void OnTimerTick(object? sender, EventArgs e) => Reconcile();

    private void Reconcile()
    {
        if (_disposed || _reconciling) return;
        _reconciling = true;
        try
        {
            List<CredentialExpirySnapshot> snapshots;
            try
            {
                snapshots = _vault.ListCredentialExpirySnapshots();
            }
            catch (Exception)
            {
                // The vault can be locked while the App is shutting down or erasing.
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var due = new List<CredentialExpirySnapshot>();
            DateTimeOffset? next = null;
            var changed = _ledgerDirty;

            foreach (var snapshot in snapshots)
            {
                seen.Add(snapshot.Id);
                var ledger = _ledger.TryGetValue(snapshot.Id, out var entry) ? entry : null;
                switch (CredentialExpiryReminderPolicy.Work(snapshot, ledger, now))
                {
                    case ExpiryReminderWork.Cancel:
                        changed |= _ledger.Remove(snapshot.Id);
                        break;
                    case ExpiryReminderWork.Deliver:
                        due.Add(snapshot);
                        break;
                    case ExpiryReminderWork.Schedule schedule:
                        next = Earlier(next, schedule.At);
                        break;
                }
            }

            foreach (var id in _ledger.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                _ledger.Remove(id);
                changed = true;
            }

            if (due.Count > 0)
            {
                try
                {
                    _notify(due.Count == 1 ? Title : MultipleTitle,
                        due.Count == 1 ? Body : MultipleBody);
                    foreach (var snapshot in due)
                    {
                        _ledger[snapshot.Id] = new ExpiryReminderLedgerEntry
                        {
                            ExpiresAt = snapshot.ExpiresAt!.Value,
                            Delivered = true,
                        };
                        changed = true;
                    }
                }
                catch (Exception)
                {
                    next = Earlier(next, now + DeliveryRetryDelay);
                }
            }

            if (changed)
            {
                if (SaveLedger()) _ledgerDirty = false;
                else
                {
                    _ledgerDirty = true;
                    next = Earlier(next, now + DeliveryRetryDelay);
                }
            }
            ScheduleNext(next, now);
        }
        finally
        {
            _reconciling = false;
        }
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? left, DateTimeOffset right)
        => left is null || right < left ? right : left;

    private void ScheduleNext(DateTimeOffset? next, DateTimeOffset now)
    {
        _timer.Stop();
        if (next is not { } at) return;
        var delay = at - now;
        if (delay < TimeSpan.FromMilliseconds(1)) delay = TimeSpan.FromMilliseconds(1);
        if (delay > MaximumTimerDelay) delay = MaximumTimerDelay;
        _timer.Interval = delay;
        _timer.Start();
    }

    private Dictionary<string, ExpiryReminderLedgerEntry> LoadLedger()
    {
        try
        {
            if (!File.Exists(_ledgerPath)) return new(StringComparer.Ordinal);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ExpiryReminderLedgerEntry>>(
                File.ReadAllText(_ledgerPath), JsonOptions);
            var result = new Dictionary<string, ExpiryReminderLedgerEntry>(StringComparer.Ordinal);
            if (loaded is not null)
            {
                foreach (var pair in loaded) result[pair.Key] = pair.Value;
            }
            return result;
        }
        catch (Exception)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private bool SaveLedger()
    {
        try
        {
            var temporary = _ledgerPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_ledger, JsonOptions));
            File.Move(temporary, _ledgerPath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void ClearLedger()
    {
        _ledger.Clear();
        try
        {
            if (File.Exists(_ledgerPath)) File.Delete(_ledgerPath);
        }
        catch (Exception)
        {
            // The next start removes orphaned entries once the erased vault is empty.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        if (_started) _vault.SnapshotChanged -= OnSnapshotChanged;
    }
}