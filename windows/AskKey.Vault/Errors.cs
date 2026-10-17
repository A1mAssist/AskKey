namespace AskKey.Vault;

public enum VaultErrorCode
{
    VaultLocked,
    VaultStorageUnavailable,
    KeychainReadFailed,
    KeychainWriteFailed,
    KeychainKeyUnreachable,
    InvalidSecretName,
    EncryptionFailed,
    DecryptionFailed,
    DatabaseError,
    ManagementAuthenticationRequired,
    InvalidCredentialName,
    CredentialNameConflict,
    CredentialNotFound,
    CredentialChanged,
    CredentialUnavailable,
    AgentAccessPaused,
    InvalidFileCredential,
}

public enum FileCredentialIssue
{
    SymbolicLink,
    Directory,
    SpecialFile,
    TooLarge,
    ReplacedDuringRead,
    MissingFile,
    DigestMismatch,
    NotFound,
}

public sealed class VaultException : Exception
{
    public VaultErrorCode Code { get; }
    public string? Detail { get; }
    public int? Status { get; }
    public FileCredentialIssue? FileIssue { get; }

    public VaultException(VaultErrorCode code, string message, string? detail = null, int? status = null,
        FileCredentialIssue? fileIssue = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Detail = detail;
        Status = status;
        FileIssue = fileIssue;
    }

    public static VaultException Locked() => new(
        VaultErrorCode.VaultLocked,
        "The vault is locked. Unlock it in the AskKey app (or approve the unlock prompt) and retry.");

    public static VaultException Database(string message, Exception? inner = null) => new(
        VaultErrorCode.DatabaseError, "Database error: " + message, message, inner: inner);

    public static VaultException InvalidSecretName(string name) => new(
        VaultErrorCode.InvalidSecretName,
        "Invalid secret name. Names must be shell-safe environment variable identifiers, and must not exceed 4096 UTF-8 bytes.",
        name);

    public static VaultException InvalidCredentialName(string raw) => new(
        VaultErrorCode.InvalidCredentialName,
        "Credential names cannot be empty, contain control characters, exceed 255 characters, or exceed 4096 UTF-8 bytes.",
        raw);

    public static VaultException NameConflict(string name) => new(
        VaultErrorCode.CredentialNameConflict, $"A credential named '{name}' already exists.", name);

    public static VaultException NotFound(string id) => new(
        VaultErrorCode.CredentialNotFound, $"Credential '{id}' was not found.", id);

    public static VaultException Changed() => new(
        VaultErrorCode.CredentialChanged,
        "The credential changed after this request was created. Submit a new request.");

    public static VaultException Unavailable() => new(
        VaultErrorCode.CredentialUnavailable, "The credential is unavailable for this Agent operation.");

    public static VaultException AgentPaused() => new(
        VaultErrorCode.AgentAccessPaused, "Agent access is paused in the Ask Key app.");

    public static VaultException ManagementAuthenticationRequired() => new(
        VaultErrorCode.ManagementAuthenticationRequired,
        "Credential management requires confirmation before it can continue.");

    public static VaultException File(FileCredentialIssue issue) => new(
        VaultErrorCode.InvalidFileCredential, FileIssueMessage(issue), fileIssue: issue);

    public static string FileIssueMessage(FileCredentialIssue issue) => issue switch
    {
        FileCredentialIssue.SymbolicLink => "AskKey only imports ordinary files. Symbolic links are not allowed.",
        FileCredentialIssue.Directory => "AskKey only imports ordinary files. Directories are not allowed.",
        FileCredentialIssue.SpecialFile => "AskKey only imports ordinary files. Special files are not allowed.",
        FileCredentialIssue.TooLarge => "This file is larger than AskKey's 5 MB import limit.",
        FileCredentialIssue.ReplacedDuringRead => "The file changed while AskKey was reading it. Import was cancelled.",
        FileCredentialIssue.MissingFile => "Choose an ordinary file before saving this credential.",
        FileCredentialIssue.DigestMismatch => "Stored file bytes do not match the saved digest.",
        FileCredentialIssue.NotFound => "The file could not be read.",
        _ => "The file could not be read.",
    };
}

public enum AppKeyStoreError
{
    MissingPendingKey,
    MissingAppKey,
    SecurityFailure,
    ConflictingKey,
}

public sealed class AppKeyStoreException : Exception
{
    public AppKeyStoreError Error { get; }
    public int Status { get; }

    public AppKeyStoreException(AppKeyStoreError error, int status = 0, Exception? inner = null)
        : base(ErrorText(error, status), inner)
    {
        Error = error;
        Status = status;
    }

    private static string ErrorText(AppKeyStoreError error, int status) => error switch
    {
        AppKeyStoreError.MissingPendingKey => "The pending vault key is missing.",
        AppKeyStoreError.MissingAppKey => "The App-owned vault key is missing.",
        AppKeyStoreError.ConflictingKey => "A different vault key already exists and was not overwritten.",
        _ => $"The vault key store failed (status {status}).",
    };
}

public enum VaultBootstrapError
{
    MissingDatabase,
    MissingKey,
    InvalidState,
    InvalidKey,
    UnsupportedMigrations,
    SchemaMismatch,
}

public sealed class VaultBootstrapException : Exception
{
    public VaultBootstrapError Error { get; }

    public VaultBootstrapException(VaultBootstrapError error, Exception? inner = null)
        : base(MessageFor(error), inner)
    {
        Error = error;
    }

    private static string MessageFor(VaultBootstrapError error) => error switch
    {
        VaultBootstrapError.MissingDatabase => "The vault key exists but its database is missing. Ask Key did not create or overwrite a library.",
        VaultBootstrapError.MissingKey => "The library exists but its App-owned key is missing. Existing data was not changed.",
        VaultBootstrapError.InvalidState => "The local library state could not be verified. Existing data was not changed.",
        VaultBootstrapError.InvalidKey => "The local vault key is invalid. Existing data was not changed.",
        VaultBootstrapError.UnsupportedMigrations => "The library has an unsupported migration history. Existing data was not changed.",
        _ => "The library schema does not match the supported baseline. Existing data was not changed.",
    };
}

public enum CredentialRecordAuthenticationError
{
    MissingAuthentication,
    InvalidAuthentication,
}

public sealed class CredentialRecordAuthenticationException : Exception
{
    public CredentialRecordAuthenticationError Error { get; }

    public CredentialRecordAuthenticationException(CredentialRecordAuthenticationError error)
        : base("Credential storage could not be authenticated. Access was refused.")
    {
        Error = error;
    }
}

public enum LocalVaultEraseError
{
    AuthenticationRequired,
    ConfirmationMismatch,
    InvalidJournal,
}

public sealed class LocalVaultEraseException : Exception
{
    public LocalVaultEraseError Error { get; }

    public LocalVaultEraseException(LocalVaultEraseError error)
        : base(error switch
        {
            LocalVaultEraseError.AuthenticationRequired => "Erasing Ask Key requires system authentication.",
            LocalVaultEraseError.ConfirmationMismatch => "The erase confirmation text did not match.",
            _ => "Ask Key found an invalid local erase journal and stopped safely.",
        })
    {
        Error = error;
    }
}
