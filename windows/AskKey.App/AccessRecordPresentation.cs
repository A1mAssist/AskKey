using System.Globalization;
using AskKey.Vault;

namespace AskKeyApp;

public sealed record AccessRecordRow(
    CredentialAccessEvent Record,
    string Time,
    string Credential,
    string Description,
    string Result);

public static class AccessRecordPresentation
{
    public static IReadOnlyList<AccessRecordRow> Build(
        IEnumerable<CredentialAccessEvent> records,
        IReadOnlyDictionary<string, string> names,
        CultureInfo culture)
        => records
            .OrderByDescending(record => record.Timestamp)
            .Select(record => new AccessRecordRow(
                record,
                record.Timestamp.ToLocalTime().ToString("g", culture),
                CredentialName(record, names),
                Describe(record, names),
                ResultLabel(record)))
            .ToList();

    public static string CredentialName(CredentialAccessEvent record, IReadOnlyDictionary<string, string> names)
    {
        if (record.CredentialId is not { } id) return Loc.T("Hidden credential request");
        return names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : id;
    }

    public static string Describe(CredentialAccessEvent record, IReadOnlyDictionary<string, string> names)
    {
        var caller = string.IsNullOrWhiteSpace(record.CallerHint) ? Loc.T("Local Agent") : record.CallerHint;
        var credential = CredentialName(record, names);
        var happened = record.Result == CredentialAccessResult.Allowed;
        return record.Operation switch
        {
            CredentialAccessOperation.Catalog => Loc.F("{0} browsed the credential list", caller),
            CredentialAccessOperation.RuntimeRead => record.ExecutableBasename is { Length: > 0 } executable
                ? Loc.F(happened ? "{0} used {1} to run {2}" : "{0} asked to use {1} to run {2}",
                    caller, credential, executable)
                : Loc.F(happened ? "{0} used {1}" : "{0} asked to use {1}", caller, credential),
            CredentialAccessOperation.Create => Loc.F(happened
                ? "{0} created credential {1}" : "{0} asked to create credential {1}", caller, credential),
            CredentialAccessOperation.Modify => Loc.F(happened
                ? "{0} changed credential {1}" : "{0} asked to change credential {1}", caller, credential),
            _ => Loc.F(happened
                ? "{0} deleted credential {1}" : "{0} asked to delete credential {1}", caller, credential),
        };
    }

    public static string ResultLabel(CredentialAccessEvent record) => record.Result switch
    {
        CredentialAccessResult.Allowed => record.Operation is CredentialAccessOperation.Create
            or CredentialAccessOperation.Modify or CredentialAccessOperation.Delete
            ? Loc.T("Approved") : Loc.T("Allowed"),
        CredentialAccessResult.Denied => Loc.T("Denied"),
        CredentialAccessResult.Expired => Loc.T("Expired without decision"),
        CredentialAccessResult.Failed => Loc.T("Failed"),
        _ => Loc.T("Hidden"),
    };

    public static string OperationLabel(CredentialAccessOperation operation) => operation switch
    {
        CredentialAccessOperation.Catalog => Loc.T("Browse"),
        CredentialAccessOperation.RuntimeRead => Loc.T("Read"),
        CredentialAccessOperation.Create => Loc.T("Create"),
        CredentialAccessOperation.Modify => Loc.T("Modify"),
        _ => Loc.T("Delete"),
    };
}
