using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Vault;

public sealed partial class Vault
{
    /// <summary>
    /// Agent-visible catalog metadata. Values, private notes, and source filenames
    /// never cross this boundary.
    /// </summary>
    public List<BrokerCatalogItem> BrokerCredentialCatalog(BrokerCancellation? cancellation = null)
    {
        AgentAccessGate.BeginAgentOperation();
        try
        {
            cancellation?.Check();
            var key = RequireKey();
            var records = Store.FetchAllCredentials();
            cancellation?.Check();
            var result = new List<BrokerCatalogItem>();
            foreach (var record in records)
            {
                cancellation?.Check();
                if (!CredentialPermissionExtensions.TryParse(record.Permission, out var permission))
                {
                    throw VaultException.Database("Credential has an unknown permission.");
                }
                if (permission == CredentialPermission.Hidden) continue;
                if (!CredentialPayloadKindExtensions.TryParse(record.PayloadKind, out var payloadKind))
                {
                    throw VaultException.Database("Credential has an unknown payload kind.");
                }

                var name = VaultCrypto.Decrypt(record.EncryptedDisplayName, key);
                var instructions = VaultCrypto.Decrypt(record.EncryptedUsageInstructions, key);
                var group = record.EncryptedGroupName is null
                    ? null
                    : VaultCrypto.Decrypt(record.EncryptedGroupName, key);
                var environment = record.EncryptedEnvironmentVariable is null
                    ? null
                    : VaultCrypto.Decrypt(record.EncryptedEnvironmentVariable, key);
                if (new[] { name, instructions, group, environment }
                    .Where(value => value is not null)
                    .Any(value => Encoding.UTF8.GetByteCount(value!) > BrokerLimits.MaximumFieldBytes))
                {
                    throw VaultException.Database("Credential catalog field exceeds the Broker limit.");
                }

                var expiresAt = record.ExpiresAt is null ? (DateTimeOffset?)null : ParseExpiry(record.ExpiresAt);
                var expired = expiresAt is { } expiry && expiry <= CurrentDate;
                if (expired)
                {
                    ApprovalRequests.CancelPendingForExpiry(record.Id);
                    FileDeliveryManager.Revoke(record.Id);
                }

                var components = CredentialComponents(record, key)
                    .Select(component => new BrokerCatalogComponent
                    {
                        Name = component.Name,
                        PayloadKind = component.Value is CredentialComponentValue.File
                            ? BrokerCatalogPayloadKind.File
                            : BrokerCatalogPayloadKind.Text,
                        Delivery = component.Delivery.Kind switch
                        {
                            CredentialDeliveryKind.EnvironmentVariable =>
                                BrokerComponentDelivery.EnvironmentVariable(component.Delivery.Variable!),
                            CredentialDeliveryKind.TemporaryFile =>
                                BrokerComponentDelivery.TemporaryFile(component.Delivery.Variable!),
                            _ => BrokerComponentDelivery.None,
                        },
                    })
                    .ToList();

                result.Add(new BrokerCatalogItem
                {
                    CredentialId = record.Id,
                    Name = name,
                    PayloadKind = payloadKind == CredentialPayloadKind.File
                        ? BrokerCatalogPayloadKind.File
                        : BrokerCatalogPayloadKind.Text,
                    UsageInstructions = instructions,
                    EnvironmentVariable = environment,
                    Expired = expired,
                    Components = components,
                    Group = group,
                });
            }
            cancellation?.Check();
            return result.OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
        }
        finally
        {
            AgentAccessGate.EndAgentOperation();
        }
    }

    /// <summary>
    /// Resolves an explicit runtime credential set. Values are decrypted only
    /// after every ask-tier credential in the set has an approval.
    /// </summary>
    public BrokerTextCredentialResolution BrokerTextCredentials(BrokerTextRunRequest request,
        BrokerCancellation cancellation)
    {
        AgentAccessGate.BeginAgentOperation();
        var ownsAgentOperation = true;
        try
        {
            cancellation.Check();
            if (!request.DeclarationsAreValid)
            {
                throw new BrokerTextRuntimeException(BrokerTextRuntimeError.InvalidRequest);
            }
            var key = RequireKey();
            var payloadDigest = RuntimeRequestDigest(request);
            var callerName = request.CallerName;
            var callerPurpose = request.CallerPurpose;
            var executableBasename = RuntimeExecutableBasename(request.Command);
            var records = new List<CredentialRecord>();
            var consumptions = new List<BrokerApprovalConsumption>();
            var pending = new List<BrokerApprovalTicket>();

            foreach (var rawName in request.CredentialNames)
            {
                cancellation.Check();
                var displayName = CredentialName.DisplayName(rawName);
                var nameIndex = CredentialIndex.Hash(CredentialName.Normalized(displayName), key);
                var record = Store.FetchCredentialByNameIndex(nameIndex);
                if (record is null
                    || !CredentialPermissionExtensions.TryParse(record.Permission, out var permission)
                    || permission == CredentialPermission.Hidden
                    || !CredentialPayloadKindExtensions.TryParse(record.PayloadKind, out _))
                {
                    RecordHiddenCredentialGuess(callerName, callerPurpose);
                    throw VaultException.Unavailable();
                }
                var trustedName = VaultCrypto.Decrypt(record.EncryptedDisplayName, key);
                var expiresAt = record.ExpiresAt is null ? (DateTimeOffset?)null : ParseExpiry(record.ExpiresAt);
                if (expiresAt is { } expiry && expiry <= CurrentDate)
                {
                    RecordCredentialAccess(CredentialAccessEvent.Create(CurrentDate, record.Id,
                        CredentialAccessOperation.RuntimeRead, CredentialAccessResult.Failed,
                        callerName, callerPurpose, executableBasename));
                    ApprovalRequests.CancelPendingForExpiry(record.Id);
                    FileDeliveryManager.Revoke(record.Id);
                    throw VaultException.Unavailable();
                }
                records.Add(record);
                if (permission != CredentialPermission.Ask) continue;

                var approval = new BrokerApprovalOperationRequest(
                    RuntimeOperationId(request.OperationId, record.Id),
                    record.Id,
                    record.Id,
                    BrokerApprovalOperation.Read,
                    payloadDigest,
                    trustedName,
                    callerName,
                    callerPurpose,
                    display: RuntimeApprovalDisplay(request, record, key));
                var ticket = ApprovalRequests.Submit(approval,
                    expiresAt is { } deadline
                        ? BrokerCredentialDeadline.ExpiresAt(deadline)
                        : BrokerCredentialDeadline.None,
                    trustedName);
                switch (ticket.State)
                {
                    case BrokerRequestState.Approved:
                        consumptions.Add(new BrokerApprovalConsumption(ticket.RequestId, ticket.Capability,
                            approval));
                        break;
                    case BrokerRequestState.Pending:
                        pending.Add(ticket);
                        break;
                    default:
                        throw VaultException.Unavailable();
                }
            }

            if (pending.Count > 0)
            {
                return BrokerTextCredentialResolution.ApprovalRequired(pending);
            }

            var runtimeAuthorization = ApprovalRequests.ConsumeForRuntime(consumptions);
            var fileDeliveries = new List<FileDelivery>();
            List<BrokerTextCredential> credentials;
            try
            {
                credentials = new List<BrokerTextCredential>();
                foreach (var record in records)
                {
                    foreach (var component in CredentialComponents(record, key))
                    {
                        cancellation.Check();
                        if (component.Delivery.Kind == CredentialDeliveryKind.None) continue;
                        if (component.Delivery.Kind == CredentialDeliveryKind.EnvironmentVariable)
                        {
                            if (component.Value is not CredentialComponentValue.Text text)
                            {
                                throw VaultException.Unavailable();
                            }
                            credentials.Add(new BrokerTextCredential(component.Delivery.Variable!, text.Value));
                            continue;
                        }

                        byte[] bytes = component.Value switch
                        {
                            CredentialComponentValue.Text text => Encoding.UTF8.GetBytes(text.Value),
                            CredentialComponentValue.File file => file.Bytes,
                            _ => throw VaultException.Unavailable(),
                        };
                        var deliveryExpiry = record.ExpiresAt is null ? (DateTimeOffset?)null : ParseExpiry(record.ExpiresAt);
                        var delivery = FileDeliveryManager.Get().Materialize(record.Id, bytes, deliveryExpiry,
                            runtimeAuthorization);
                        fileDeliveries.Add(delivery);
                        credentials.Add(new BrokerTextCredential(component.Delivery.Variable!, delivery.Path));
                    }
                }
            }
            catch
            {
                foreach (var delivery in fileDeliveries) delivery.Finish();
                runtimeAuthorization?.Finish();
                throw;
            }

            DateTimeOffset? credentialDeadline = null;
            foreach (var record in records)
            {
                if (record.ExpiresAt is not { } storedExpiry) continue;
                var expiry = ParseExpiry(storedExpiry);
                credentialDeadline = credentialDeadline is { } current && current <= expiry ? current : expiry;
            }
            var spawnDeadlineValues = new List<DateTimeOffset>();
            if (credentialDeadline is { } credentialDate) spawnDeadlineValues.Add(credentialDate);
            if (runtimeAuthorization is not null) spawnDeadlineValues.Add(runtimeAuthorization.ExpiresAt);
            spawnDeadlineValues.AddRange(fileDeliveries.Select(delivery => delivery.ExpiresAt));
            DateTimeOffset? spawnDeadline = spawnDeadlineValues.Count == 0 ? null : spawnDeadlineValues.Min();

            var gate = AgentAccessGate;
            var lease = new BrokerTextDeliveryLease(
                () => gate.BeginSpawnAuthorization(),
                () =>
                {
                    cancellation.Check();
                    foreach (var record in records)
                    {
                        if (record.ExpiresAt is not { } storedExpiry) continue;
                        if (ParseExpiry(storedExpiry) <= CurrentDate)
                        {
                            throw new BrokerProviderException(BrokerProviderError.RequestRejected);
                        }
                    }
                },
                () => gate.EndSpawnAuthorization(),
                spawnDeadline,
                runtimeAuthorization,
                () => gate.EndAgentOperation(),
                () =>
                {
                    foreach (var delivery in fileDeliveries) delivery.Finish();
                });

            foreach (var record in records)
            {
                RecordCredentialAccess(CredentialAccessEvent.Create(CurrentDate, record.Id,
                    CredentialAccessOperation.RuntimeRead, CredentialAccessResult.Allowed,
                    callerName, callerPurpose, executableBasename));
            }
            ownsAgentOperation = false;
            return BrokerTextCredentialResolution.Resolved(credentials, records.Count, lease);
        }
        finally
        {
            if (ownsAgentOperation) AgentAccessGate.EndAgentOperation();
        }
    }

    public List<string> BrokerCredentialGroups(BrokerCancellation? cancellation = null)
    {
        AgentAccessGate.BeginAgentOperation();
        try
        {
            cancellation?.Check();
            var key = RequireKey();
            var snapshot = new AgentOrganizationSnapshot
            {
                Records = Store.FetchAllCredentialsIncludingRecycled(),
                StoredGroups = VaultGroups.StoredGroups(Store.ConfigValue(VaultGroups.ConfigKey), key),
            };
            var assignments = snapshot.Assignments(key);
            var names = new HashSet<string>(snapshot.StoredGroups, StringComparer.Ordinal);
            foreach (var value in assignments.Values)
            {
                if (value is not null) names.Add(value);
            }
            var result = new List<string>();
            foreach (var name in names)
            {
                cancellation?.Check();
                var normalized = CredentialName.Normalized(name);
                var members = snapshot.Records.Where(record =>
                    assignments.TryGetValue(record.Id, out var assigned)
                    && assigned is not null
                    && CredentialName.Normalized(assigned) == normalized).ToList();
                if (members.Count == 0 || members.Any(record =>
                        record.DeletedAt is null && record.Permission != CredentialPermission.Hidden.WireValue()))
                {
                    result.Add(name);
                }
            }
            cancellation?.Check();
            return result.OrderBy(value => value, StringComparer.Ordinal).ToList();
        }
        finally
        {
            AgentAccessGate.EndAgentOperation();
        }
    }

    private static string RuntimeRequestDigest(BrokerTextRunRequest request)
    {
        var environment = new JsonObject();
        foreach (var (name, value) in request.InheritedEnvironment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            environment[name] = value;
        }
        var node = new JsonObject
        {
            ["operationID"] = request.OperationId,
            ["command"] = new JsonArray(request.Command.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
            ["credentialNames"] = new JsonArray(request.CredentialNames.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
            ["inheritedEnvironment"] = environment,
        };
        if (request.WorkingDirectory is not null) node["workingDirectory"] = request.WorkingDirectory;
        if (request.CallerName is not null) node["callerName"] = request.CallerName;
        if (request.CallerPurpose is not null) node["callerPurpose"] = request.CallerPurpose;
        return VaultJson.Digest(VaultJson.WithoutNulls(node));
    }

    private static string RuntimeOperationId(string operationId, string credentialId)
        => Sha256Hex(Encoding.UTF8.GetBytes(operationId + ":" + credentialId));

    private BrokerApprovalOperationRequest.Display RuntimeApprovalDisplay(BrokerTextRunRequest request,
        CredentialRecord record, byte[] key)
    {
        var environmentVariables = new List<string>();
        var temporaryFileVariables = new List<string>();
        if (record.PayloadKind != CredentialPayloadKind.Bundle.WireValue())
        {
            var variable = record.EncryptedEnvironmentVariable is null
                ? null
                : VaultCrypto.Decrypt(record.EncryptedEnvironmentVariable, key);
            if (record.PayloadKind == CredentialPayloadKind.Text.WireValue())
            {
                if (variable is not null) environmentVariables.Add(variable);
            }
            else if (record.PayloadKind == CredentialPayloadKind.File.WireValue())
            {
                if (variable is not null) temporaryFileVariables.Add(variable);
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? directory = null;
        if (request.WorkingDirectory is { } workingDirectory)
        {
            var abbreviated = workingDirectory;
            if (!string.IsNullOrEmpty(home))
            {
                if (workingDirectory.Equals(home, StringComparison.OrdinalIgnoreCase)) abbreviated = "~";
                else if (workingDirectory.StartsWith(home + Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase))
                {
                    abbreviated = "~" + workingDirectory[home.Length..];
                }
                else if (workingDirectory.StartsWith(home + "/", StringComparison.OrdinalIgnoreCase))
                {
                    abbreviated = "~" + workingDirectory[home.Length..];
                }
            }
            directory = EscapedDisplayText(abbreviated);
        }

        return new BrokerApprovalOperationRequest.Display(
            string.Join(" ", request.Command.Select(ShellQuotedDisplayArgument)),
            directory,
            RuntimeExecutableBasename(request.Command),
            record.PayloadKind == CredentialPayloadKind.Bundle.WireValue() ? null : environmentVariables,
            record.PayloadKind == CredentialPayloadKind.Bundle.WireValue() ? null : temporaryFileVariables);
    }

    private static string? RuntimeExecutableBasename(IReadOnlyList<string> command)
    {
        if (command.Count == 0) return null;
        var parts = command[0].Split('/', '\\');
        var basename = parts.Length == 0 ? command[0] : parts[^1];
        var escaped = EscapedDisplayText(basename);
        var elements = GraphemeElements(escaped);
        if (elements.Count <= 64) return escaped;
        return string.Concat(elements.Take(32)) + "…" + string.Concat(elements.Skip(elements.Count - 31));
    }

    private static string ShellQuotedDisplayArgument(string value)
    {
        if (value.Length == 0) return "\"\"";

        var quote = value.Any(character => char.IsWhiteSpace(character)
            || character == '"' || char.IsControl(character));
        if (!quote) return value;

        // CommandLineToArgvW-compatible quoting: double the backslashes before a
        // quote or the closing quote, and escape an embedded quote with a backslash.
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes);
            backslashes = 0;
            switch (character)
            {
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\x").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    private static string EscapedDisplayText(string value)
    {
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (Rune.IsControl(rune))
                    {
                        foreach (var item in Encoding.UTF8.GetBytes(rune.ToString()))
                        {
                            builder.Append("\\x").Append(item.ToString("x2", CultureInfo.InvariantCulture));
                        }
                    }
                    else
                    {
                        builder.Append(rune.ToString());
                    }
                    break;
            }
        }
        return builder.ToString();
    }

    private static List<string> GraphemeElements(string value)
    {
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext()) result.Add((string)enumerator.Current);
        return result;
    }
}

