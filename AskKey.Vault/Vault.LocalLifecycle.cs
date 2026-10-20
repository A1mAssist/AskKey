using System.Text;
using System.Text.Json;

namespace AskKey.Vault;

public enum LocalVaultEraseLanguage
{
    SimplifiedChinese,
    English,
}

public static class LocalVaultEraseLanguageExtensions
{
    public static string ConfirmationText(this LocalVaultEraseLanguage language) => language switch
    {
        LocalVaultEraseLanguage.SimplifiedChinese => "抹除",
        _ => "ERASE",
    };
}

public enum LocalVaultEraseState
{
    Confirmed,
    OperationsQuiesced,
    DeliveriesCleared,
    BackupsStopped,
    DataDeleted,
    KeyDeleted,
}

public interface ILocalVaultEraseJournalStore
{
    LocalVaultEraseState? Load();
    void Save(LocalVaultEraseState state);
    void Clear();
}

/// <summary>
/// Port of FileLocalVaultEraseJournalStore. The journal is private, regular-file
/// only, and saved through a flushed temporary file before replacement.
/// </summary>
public sealed class FileLocalVaultEraseJournalStore : ILocalVaultEraseJournalStore
{
    private readonly string _path;

    public FileLocalVaultEraseJournalStore(string path) => _path = path;

    public string Path => _path;

    public LocalVaultEraseState? Load()
    {
        if (!File.Exists(_path))
        {
            if (Directory.Exists(_path)) throw Invalid();
            return null;
        }
        FileInfo info;
        try
        {
            info = new FileInfo(_path);
            if ((info.Attributes & FileAttributes.Directory) != 0 || info.LinkTarget is not null)
            {
                throw Invalid();
            }
        }
        catch (LocalVaultEraseException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Invalid();
        }

        string? raw;
        try
        {
            raw = JsonSerializer.Deserialize<string>(File.ReadAllText(_path, Encoding.UTF8));
        }
        catch (Exception)
        {
            throw Invalid();
        }
        return raw switch
        {
            "confirmed" => LocalVaultEraseState.Confirmed,
            "operationsQuiesced" => LocalVaultEraseState.OperationsQuiesced,
            "deliveriesCleared" => LocalVaultEraseState.DeliveriesCleared,
            "backupsStopped" => LocalVaultEraseState.BackupsStopped,
            "dataDeleted" => LocalVaultEraseState.DataDeleted,
            "keyDeleted" => LocalVaultEraseState.KeyDeleted,
            _ => throw Invalid(),
        };
    }

    public void Save(LocalVaultEraseState state)
    {
        if (Directory.Exists(_path)) throw Invalid();
        var directory = System.IO.Path.GetDirectoryName(_path)
            ?? throw VaultException.Database("The local erase journal path is invalid.");
        PrivateFileSystem.CreatePrivateDirectory(directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = PrivateFileSystem.CreatePrivateFile(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(WireValue(state)));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            PrivateFileSystem.ApplyFile(_path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear()
    {
        if (Directory.Exists(_path)) throw Invalid();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static string WireValue(LocalVaultEraseState state) => state switch
    {
        LocalVaultEraseState.Confirmed => "confirmed",
        LocalVaultEraseState.OperationsQuiesced => "operationsQuiesced",
        LocalVaultEraseState.DeliveriesCleared => "deliveriesCleared",
        LocalVaultEraseState.BackupsStopped => "backupsStopped",
        LocalVaultEraseState.DataDeleted => "dataDeleted",
        _ => "keyDeleted",
    };

    private static LocalVaultEraseException Invalid()
        => new(LocalVaultEraseError.InvalidJournal);
}

/// <summary>In-memory journal used by tests and synthetic lifecycle harnesses.</summary>
public sealed class MemoryLocalVaultEraseJournalStore : ILocalVaultEraseJournalStore
{
    private LocalVaultEraseState? _state;

    public LocalVaultEraseState? State
    {
        get => _state;
        set => _state = value;
    }

    public LocalVaultEraseState? Load() => _state;
    public void Save(LocalVaultEraseState state) => _state = state;
    public void Clear() => _state = null;
}

public sealed partial class Vault
{
    private readonly object _localEraseLock = new();

    public void EraseLocalLibrary(string confirmation, LocalVaultEraseLanguage language,
        ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        if (!authenticator.Confirm("Erase the local Ask Key vault"))
        {
            throw new LocalVaultEraseException(LocalVaultEraseError.AuthenticationRequired);
        }
        if (confirmation != language.ConfirmationText())
        {
            throw new LocalVaultEraseException(LocalVaultEraseError.ConfirmationMismatch);
        }

        lock (_localEraseLock)
        {
            var journal = LocalEraseJournal;
            if (journal.Load() is null) journal.Save(LocalVaultEraseState.Confirmed);
            RecoverLocalEraseForward(journal);
        }
    }

    /// <summary>
    /// Erases the local library without the vault key. A forgotten passphrase leaves no
    /// key to authenticate with, and the ciphertext is unrecoverable; the typed
    /// confirmation is the gate.
    /// </summary>
    public void ResetLocalLibrary(string confirmation, LocalVaultEraseLanguage language)
    {
        if (confirmation != language.ConfirmationText())
        {
            throw new LocalVaultEraseException(LocalVaultEraseError.ConfirmationMismatch);
        }

        lock (_localEraseLock)
        {
            var journal = LocalEraseJournal;
            if (journal.Load() is null) journal.Save(LocalVaultEraseState.Confirmed);
            RecoverLocalEraseForward(journal);
        }
    }

    public void RecoverInterruptedLocalErase()
    {
        lock (_localEraseLock)
        {
            var journal = LocalEraseJournal;
            if (journal.Load() is null) return;
            RecoverLocalEraseForward(journal);
        }
    }

    private ILocalVaultEraseJournalStore LocalEraseJournal
        => new FileLocalVaultEraseJournalStore(_configuration.LocalEraseJournalFile);

    private void RecoverLocalEraseForward(ILocalVaultEraseJournalStore journal)
    {
        while (journal.Load() is { } state)
        {
            switch (state)
            {
                case LocalVaultEraseState.Confirmed:
                    QuiesceForLocalErase();
                    journal.Save(LocalVaultEraseState.OperationsQuiesced);
                    break;
                case LocalVaultEraseState.OperationsQuiesced:
                    CleanupRuntimeFileDeliveries();
                    if (HasRuntimeFileCleanupFailures)
                    {
                        throw VaultException.Database(
                            "Temporary credential cleanup must finish before local erase.");
                    }
                    journal.Save(LocalVaultEraseState.DeliveriesCleared);
                    break;
                case LocalVaultEraseState.DeliveriesCleared:
                case LocalVaultEraseState.BackupsStopped:
                    DeleteEncryptedLocalData();
                    journal.Save(LocalVaultEraseState.DataDeleted);
                    break;
                case LocalVaultEraseState.DataDeleted:
                    DeleteAllLocalKeyMaterial();
                    journal.Save(LocalVaultEraseState.KeyDeleted);
                    break;
                case LocalVaultEraseState.KeyDeleted:
                    journal.Clear();
                    break;
                default:
                    throw new LocalVaultEraseException(LocalVaultEraseError.InvalidJournal);
            }
        }
    }

    private void QuiesceForLocalErase()
    {
        var wasPaused = AgentAccessGate.BeginExclusiveChange();
        try
        {
            BrokerRequests.PauseAndCancelAll();
            ApprovalRequests.PauseAndCancelAll();
        }
        finally
        {
            AgentAccessGate.EndExclusiveChange(paused: true);
        }
        _ = wasPaused;
    }

    private void DeleteEncryptedLocalData()
    {
        CloseStoreForLocalErase();
        var directory = _configuration.ApplicationSupportDirectory;
        if (Directory.Exists(directory))
        {
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget is not null)
            {
                info.Delete();
            }
            else
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        else if (File.Exists(directory))
        {
            throw VaultException.Database("The local library path is not a directory.");
        }
    }

    private void DeleteAllLocalKeyMaterial()
    {
        _keyStore.DeleteLegacyKey();
        _keyStore.DeletePendingKey();
        _keyStore.DeleteAppKey();
        FinishLocalErase();
    }
}
