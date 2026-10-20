using System.Text;
using AskKey.Broker.Approval;

namespace AskKey.Vault;

public sealed partial class Vault
{
    private const string AccessRecordFailureKey = "credential_access_record_write_failed";

    public void RecordCredentialAccess(CredentialAccessEvent value)
    {
        try
        {
            var key = RequireKey();
            PurgeExpiredCredentialAccessRecords(key);
            var encrypted = VaultCrypto.Encrypt(CredentialAccessEvent.ToJson(value), key);
            Store.InsertCredentialAccessRecord(new CredentialAccessRecord
            {
                Id = Guid.NewGuid().ToString(),
                EncryptedRecord = encrypted,
            }, CredentialAccessRecordPolicy.MaximumEntries);
            Store.SetConfigValue(AccessRecordFailureKey, null);
            SetAccessRecordWriteFailed(false);
        }
        catch
        {
            SetAccessRecordWriteFailed(true);
            try { Store.SetConfigValue(AccessRecordFailureKey, "true"); }
            catch (Exception) { /* best effort; in-memory flag still reports failure */ }
        }
    }

    public void RecordHiddenCredentialGuess(string? callerHint, string? declaredPurpose)
        => RecordCredentialAccess(CredentialAccessEvent.Create(CurrentDate, null,
            CredentialAccessOperation.Catalog, CredentialAccessResult.HiddenNameRejected,
            callerHint, declaredPurpose));

    public List<CredentialAccessEvent> ListCredentialAccessRecords()
    {
        RequireManagementSession();
        var key = RequireKey();
        PurgeExpiredCredentialAccessRecords(key);
        return Store.FetchCredentialAccessRecords()
            .Select(record => CredentialAccessEvent.FromJson(
                VaultCrypto.Decrypt(record.EncryptedRecord, key)))
            .ToList();
    }

    public void ClearCredentialAccessRecords(ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, "Clear Ask Key access records");
        Store.DeleteAllCredentialAccessRecords();
    }

    public bool HasCredentialAccessRecordWriteFailure()
    {
        RequireManagementSession();
        if (AccessRecordWriteFailed) return true;
        return Store.ConfigValue(AccessRecordFailureKey) == "true";
    }

    private void PurgeExpiredCredentialAccessRecords(byte[] key)
    {
        var cutoff = CurrentDate - CredentialAccessRecordPolicy.Retention;
        var expired = new List<string>();
        foreach (var record in Store.FetchCredentialAccessRecords())
        {
            var value = CredentialAccessEvent.FromJson(VaultCrypto.Decrypt(record.EncryptedRecord, key));
            if (value.Timestamp < cutoff) expired.Add(record.Id);
        }
        Store.DeleteCredentialAccessRecords(expired);
    }
}
