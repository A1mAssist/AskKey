using System.Text.Json.Nodes;
using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;
using AskKey.Integrations;

namespace AskKey.Helper;

internal static class McpTools
{
    private const string McpProtocolVersion = MCPProtocol.Current;

    public static JsonObject Call(
        JsonNode? id,
        string name,
        JsonObject arguments,
        BrokerSocketClient client,
        CredentialDiscoveryGuard discoveryGuard)
    {
        switch (name)
        {
            case "credential_discovery_guard":
                return McpResponses.ToolText(id, discoveryGuard.Response(arguments).ToJsonString());

            case "connection_status":
                return McpResponses.ToolText(id, ConnectionStatus(client));

            case "organize_credentials":
                if (ParseOrganization(arguments) is not { } organization)
                {
                    return McpResponses.Failure(
                        id, -32602, "operations must contain 1 to 64 valid group operations");
                }
                return CallTextWrite(id, AgentTextWriteAction.Organize(organization), arguments, client);

            case "begin_component_upload":
                if (String(arguments, "operation_id") is not { } operationId
                    || String(arguments, "filename") is not { } filename
                    || Int(arguments, "byte_count") is not { } byteCount)
                {
                    return McpResponses.Failure(
                        id, -32602, "operation_id, filename and byte_count are required");
                }
                return McpResponses.BrokerResponseFor(
                    id,
                    client.Send(new BrokerRequest
                    {
                        Version = BrokerProtocolVersion.Current,
                        Method = "credential.file.write",
                        FileWrite = BrokerFileWriteRequest.BeginComponent(
                            new BrokerComponentUploadBeginRequest(operationId, filename, byteCount)),
                    }),
                    ExpectedFileWriteResponse.Upload);

            case "freeze_component_upload":
            case "cancel_component_upload":
                if (String(arguments, "upload_id") is not { } uploadId
                    || String(arguments, "capability") is not { } capability)
                {
                    return McpResponses.Failure(id, -32602, "upload_id and capability are required");
                }
                var freeze = new BrokerFileWriteFreezeRequest(uploadId, capability);
                return McpResponses.BrokerResponseFor(
                    id,
                    client.Send(new BrokerRequest
                    {
                        Version = BrokerProtocolVersion.Current,
                        Method = "credential.file.write",
                        FileWrite = name == "freeze_component_upload"
                            ? BrokerFileWriteRequest.FreezeComponent(freeze)
                            : BrokerFileWriteRequest.CancelUpload(freeze),
                    }),
                    name == "freeze_component_upload"
                        ? ExpectedFileWriteResponse.Component
                        : ExpectedFileWriteResponse.Cancelled);

            case "create_credential":
                if (String(arguments, "name") is not { } credentialName
                    || Array(arguments, "components") is not { } rawComponents
                    || !OptionalString(arguments, "usage_instructions", out var usageInstructions, allowNull: false)
                    || !OptionalString(arguments, "group", out var group, allowNull: false)
                    || ParseComponents(rawComponents) is not { } components)
                {
                    return McpResponses.Failure(id, -32602, "name and valid components are required");
                }
                return CallTextWrite(
                    id,
                    AgentTextWriteAction.CreateBundle(
                        credentialName, components, usageInstructions, group),
                    arguments,
                    client);

            case "modify_credential":
                string? changesError = null;
                if (String(arguments, "name") is not { } modifyName
                    || !OptionalString(arguments, "usage_instructions", out var modifyUsage, allowNull: false)
                    || !OptionalGroup(arguments, out var groupChange)
                    || !ParseChanges(arguments, out var changes, out changesError))
                {
                    return McpResponses.Failure(
                        id, -32602,
                        changesError ?? "name and valid changes, usage_instructions or group are required");
                }
                if (changes is null && modifyUsage is null && !HasGroup(arguments))
                {
                    return McpResponses.Failure(
                        id, -32602, "name and valid changes, usage_instructions or group are required");
                }
                return CallTextWrite(
                    id,
                    AgentTextWriteAction.ModifyBundle(
                        modifyName, changes ?? global::System.Array.Empty<BrokerCredentialComponentChange>(),
                        modifyUsage, groupChange),
                    arguments,
                    client);

            case "begin_file_write":
                if (String(arguments, "operation_id") is not { } fileOperationId
                    || String(arguments, "credential_id") is not { } credentialId
                    || String(arguments, "target_id") is not { } targetId
                    || String(arguments, "operation") is not { } operationName
                    || !BrokerWire.TryParseApprovalOperation(operationName, out var operation)
                    || operation is not (BrokerApprovalOperation.Create or BrokerApprovalOperation.Modify)
                    || String(arguments, "filename") is not { } fileFilename
                    || Long(arguments, "byte_count") is not { } expectedBytes)
                {
                    return McpResponses.Failure(id, -32602, "Invalid file write begin request");
                }
                return McpResponses.BrokerResponseFor(
                    id,
                    client.Send(new BrokerRequest
                    {
                        Version = BrokerProtocolVersion.Current,
                        Method = "credential.file.write",
                        FileWrite = BrokerFileWriteRequest.Begin(new BrokerFileWriteBeginRequest(
                            fileOperationId, credentialId, targetId, operation,
                            fileFilename, expectedBytes)),
                    }),
                    ExpectedFileWriteResponse.Upload);

            case "append_file_write":
            case "append_component_upload":
                if (String(arguments, "upload_id") is not { } appendUploadId
                    || String(arguments, "capability") is not { } appendCapability
                    || Long(arguments, "offset") is not { } offset
                    || String(arguments, "chunk_base64") is not { } base64)
                {
                    return McpResponses.Failure(id, -32602, "Invalid bounded file write chunk");
                }
                byte[] bytes;
                try { bytes = Convert.FromBase64String(base64); }
                catch (FormatException)
                {
                    return McpResponses.Failure(id, -32602, "Invalid bounded file write chunk");
                }
                if (bytes.Length == 0 || bytes.Length > BrokerFileWriteCoordinator.MaximumChunkByteCount)
                {
                    return McpResponses.Failure(id, -32602, "Invalid bounded file write chunk");
                }
                return McpResponses.BrokerResponseFor(
                    id,
                    client.Send(new BrokerRequest
                    {
                        Version = BrokerProtocolVersion.Current,
                        Method = "credential.file.write",
                        FileWrite = BrokerFileWriteRequest.Append(new BrokerFileWriteChunkRequest(
                            appendUploadId, appendCapability, offset, bytes)),
                    }),
                    ExpectedFileWriteResponse.Chunk);

            case "freeze_file_write":
                if (String(arguments, "upload_id") is not { } freezeUploadId
                    || String(arguments, "capability") is not { } freezeCapability)
                {
                    return McpResponses.Failure(id, -32602, "Invalid file write freeze request");
                }
                return McpResponses.BrokerResponseFor(
                    id,
                    client.Send(new BrokerRequest
                    {
                        Version = BrokerProtocolVersion.Current,
                        Method = "credential.file.write",
                        FileWrite = BrokerFileWriteRequest.Freeze(
                            new BrokerFileWriteFreezeRequest(freezeUploadId, freezeCapability)),
                    }),
                    ExpectedFileWriteResponse.Approval);

            case "create_text_credential":
                if (String(arguments, "name") is not { } createName
                    || String(arguments, "value") is not { } createValue)
                {
                    return McpResponses.Failure(id, -32602, "name and value are required");
                }
                return CallTextWrite(id, AgentTextWriteAction.Create(createName, createValue), arguments, client);

            case "modify_text_credential":
                if (String(arguments, "name") is not { } modifyTextName
                    || String(arguments, "value") is not { } modifyValue)
                {
                    return McpResponses.Failure(id, -32602, "name and value are required");
                }
                return CallTextWrite(id, AgentTextWriteAction.Modify(modifyTextName, modifyValue), arguments, client);

            case "delete_credential":
                if (String(arguments, "name") is not { } deleteName)
                {
                    return McpResponses.Failure(id, -32602, "name is required");
                }
                return CallTextWrite(id, AgentTextWriteAction.Delete(deleteName), arguments, client);

            case "request_status":
            case "request_cancel":
                if (String(arguments, "request_id") is not { } requestId
                    || String(arguments, "capability") is not { } requestCapability)
                {
                    return McpResponses.Failure(id, -32602, "request_id and capability are required");
                }
                return McpResponses.RequestStateResponse(
                    id,
                    client.Send(new BrokerRequest
                    {
                        Version = BrokerProtocolVersion.Current,
                        Method = name == "request_status" ? "request.status" : "request.cancel",
                        RequestId = requestId,
                        Capability = requestCapability,
                    }));

            case "list_credentials":
                // Release the reminder only after a real lookup attempt has
                // finished, including an unavailable Broker.
                try
                {
                    var response = client.Send(new BrokerRequest
                    {
                        Version = BrokerProtocolVersion.Current,
                        Method = "catalog",
                    });
                    if (response.ResponseKind == BrokerResponse.Kind.Failure)
                    {
                        return McpResponses.BrokerFailure(id, response.Error);
                    }
                    if (response.Payload?.PayloadKind != BrokerPayload.Kind.Catalog)
                    {
                        return McpResponses.StatusError(id, "unexpected_response");
                    }
                    var items = response.Payload.CatalogItems ?? global::System.Array.Empty<BrokerCatalogItem>();
                    var groups = response.Payload.CatalogGroups ?? global::System.Array.Empty<string>();
                    var catalog = new JsonObject
                    {
                        ["credentials"] = new JsonArray(items
                            .Select(item => (JsonNode)BrokerJson.EncodeCatalogItem(item)).ToArray()),
                        ["groups"] = new JsonArray(groups
                            .Select(group => (JsonNode)JsonValue.Create(group)!).ToArray()),
                    };
                    return McpResponses.ToolText(id, catalog.ToJsonString(), AgentUsageGuide.CatalogDescription);
                }
                finally
                {
                    discoveryGuard.CatalogAttemptFinished(String(arguments, "discovery_token"));
                }

            case "run":
                if (StringArray(arguments, "credentials") is not { Count: > 0 } credentials
                    || StringArray(arguments, "command") is not { Count: > 0 } command)
                {
                    return McpResponses.Failure(id, -32602, "credentials and command are required");
                }
                var request = new BrokerTextRunRequest(
                    String(arguments, "operation_id") ?? Guid.NewGuid().ToString(),
                    command,
                    credentials,
                    String(arguments, "cwd"),
                    CliRunner.CurrentEnvironment(),
                    String(arguments, "caller_name"),
                    String(arguments, "caller_purpose"));
                using (var channel = BrokerRuntimeChannel.CreateCaller())
                {
                    _ = channel.CloseStdinAfterConnectAsync();
                    var drains = new[]
                    {
                        Drain(channel, channel.Stdout),
                        Drain(channel, channel.Stderr),
                    };
                    BrokerTextRunResult result;
                    try
                    {
                        result = client.Run(request, channel);
                    }
                    finally
                    {
                        try { channel.Stdin.Close(); }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                        WaitForDrains(drains);
                    }
                    return McpResponses.ToolText(
                        id,
                        BrokerJson.EncodeTextRunResult(result).ToJsonString(),
                        AgentUsageGuide.NextStep(result));
                }

            default:
                return McpResponses.Failure(id, -32602, "Unknown tool: " + name);
        }
    }

    private static JsonObject CallTextWrite(
        JsonNode? id,
        AgentTextWriteAction action,
        JsonObject arguments,
        BrokerSocketClient client)
    {
        if (String(arguments, "operation_id") is not { } operationId)
        {
            return McpResponses.Failure(id, -32602, "operation_id is required");
        }
        var request = new AgentTextWriteRequest(
            operationId,
            action,
            String(arguments, "caller_name"),
            String(arguments, "caller_purpose"));
        var requestId = String(arguments, "request_id");
        var capability = String(arguments, "capability");
        BrokerRequest brokerRequest;
        if (requestId is null && capability is null)
        {
            brokerRequest = new BrokerRequest
            {
                Version = BrokerProtocolVersion.Current,
                Method = "credential.write.request",
                TextWrite = request,
            };
        }
        else if (requestId is not null && capability is not null)
        {
            brokerRequest = new BrokerRequest
            {
                Version = BrokerProtocolVersion.Current,
                Method = "credential.write.commit",
                RequestId = requestId,
                Capability = capability,
                TextWrite = request,
            };
        }
        else
        {
            return McpResponses.Failure(
                id, -32602, "request_id and capability must be supplied together");
        }
        return McpResponses.TextWriteResponse(id, client.Send(brokerRequest));
    }

    private static string ConnectionStatus(BrokerSocketClient client)
    {
        JsonObject status;
        try
        {
            var versionResponse = client.Send(new BrokerRequest
            {
                Version = BrokerProtocolVersion.Current,
                Method = "version",
            });
            if (versionResponse.ResponseKind == BrokerResponse.Kind.Success
                && versionResponse.Payload?.PayloadKind == BrokerPayload.Kind.Version
                && versionResponse.Payload.Version is { } version
                && version.ProtocolVersion == BrokerProtocolVersion.Current)
            {
                var healthResponse = client.Send(new BrokerRequest
                {
                    Version = BrokerProtocolVersion.Current,
                    Method = "health",
                });
                if (healthResponse.ResponseKind == BrokerResponse.Kind.Success
                    && healthResponse.Payload?.PayloadKind == BrokerPayload.Kind.Health
                    && healthResponse.Payload.Health is { } health)
                {
                    if (health.Version != BrokerProtocolVersion.Current)
                    {
                        status = new JsonObject
                        {
                            ["status"] = "protocol_incompatible",
                            ["helperProtocolVersion"] = BrokerProtocolVersion.Current,
                            ["brokerProtocolVersion"] = health.Version,
                        };
                    }
                    else if (health.Status == "ok")
                    {
                        status = new JsonObject
                        {
                            ["status"] = "connected",
                            ["helperVersion"] = AskKeyVersion.Current,
                            ["mcpProtocolVersion"] = McpProtocolVersion,
                            ["brokerProtocolVersion"] = health.Version,
                        };
                    }
                    else
                    {
                        status = new JsonObject { ["status"] = "broker_unavailable" };
                    }
                }
                else if (healthResponse.ResponseKind == BrokerResponse.Kind.Failure
                         && healthResponse.Error == BrokerErrorCode.UnsupportedVersion)
                {
                    status = new JsonObject
                    {
                        ["status"] = "protocol_incompatible",
                        ["helperProtocolVersion"] = BrokerProtocolVersion.Current,
                    };
                }
                else
                {
                    status = new JsonObject { ["status"] = "broker_unavailable" };
                }
            }
            else if (versionResponse.ResponseKind == BrokerResponse.Kind.Success
                     && versionResponse.Payload?.PayloadKind == BrokerPayload.Kind.Version
                     && versionResponse.Payload.Version is { } otherVersion)
            {
                status = new JsonObject
                {
                    ["status"] = "protocol_incompatible",
                    ["helperProtocolVersion"] = BrokerProtocolVersion.Current,
                    ["brokerProtocolVersion"] = otherVersion.ProtocolVersion,
                };
            }
            else if (versionResponse.ResponseKind == BrokerResponse.Kind.Failure
                     && versionResponse.Error == BrokerErrorCode.UnsupportedVersion)
            {
                status = new JsonObject
                {
                    ["status"] = "protocol_incompatible",
                    ["helperProtocolVersion"] = BrokerProtocolVersion.Current,
                };
            }
            else
            {
                status = new JsonObject { ["status"] = "broker_unavailable" };
            }
        }
        catch (BrokerSocketException error)
        {
            var failure = McpResponses.ConnectionFailure(error);
            status = new JsonObject { ["status"] = failure.Status };
            if (failure.BrokerCode is not null) status["brokerCode"] = failure.BrokerCode;
        }
        catch (Exception)
        {
            status = new JsonObject { ["status"] = "request_failed" };
        }
        return status.ToJsonString();
    }

    private static async Task Drain(BrokerRuntimeChannel channel, Stream source)
    {
        try
        {
            if (!await channel.Connected.ConfigureAwait(false)) return;
            await source.CopyToAsync(Stream.Null).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static void WaitForDrains(params Task[] tasks)
    {
        try
        {
            if (!Task.WaitAll(tasks, TimeSpan.FromSeconds(10)))
            {
                _ = Task.WhenAll(tasks).ContinueWith(
                    static finished => _ = finished.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (AggregateException)
        {
        }
    }

    private static List<BrokerOrganizationOperation>? ParseOrganization(JsonObject arguments)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "operations", "operation_id", "request_id", "capability", "caller_name", "caller_purpose",
        };
        if (!arguments.All(pair => allowed.Contains(pair.Key))
            || Array(arguments, "operations") is not { } raw
            || raw.Count is < 1 or > 64
            || String(arguments, "operation_id") is not { } operationId)
        {
            return null;
        }
        if (!OptionalString(arguments, "request_id", out _, allowNull: false)
            || !OptionalString(arguments, "capability", out _, allowNull: false)
            || !OptionalString(arguments, "caller_name", out var callerName, allowNull: false)
            || !OptionalString(arguments, "caller_purpose", out var callerPurpose, allowNull: false))
        {
            return null;
        }
        var operations = new List<BrokerOrganizationOperation>();
        foreach (var node in raw)
        {
            if (node is not JsonObject item || item.Count != 1) return null;
            if (item["move"] is JsonObject move)
            {
                if (move.Count != 2
                    || String(move, "credential") is not { } credential
                    || !move.TryGetPropertyValue("group", out var groupNode))
                {
                    return null;
                }
                if (groupNode is null)
                {
                    operations.Add(BrokerOrganizationOperation.Move(credential, null));
                }
                else if (groupNode is JsonValue value && value.TryGetValue<string>(out var group))
                {
                    operations.Add(BrokerOrganizationOperation.Move(credential, group));
                }
                else
                {
                    return null;
                }
            }
            else if (String(item, "create_group") is { } createGroup)
            {
                operations.Add(BrokerOrganizationOperation.CreateGroup(createGroup));
            }
            else if (item["rename_group"] is JsonObject rename)
            {
                if (rename.Count != 2
                    || String(rename, "from") is not { } from
                    || String(rename, "to") is not { } to)
                {
                    return null;
                }
                operations.Add(BrokerOrganizationOperation.RenameGroup(from, to));
            }
            else if (String(item, "delete_group") is { } deleteGroup)
            {
                operations.Add(BrokerOrganizationOperation.DeleteGroup(deleteGroup));
            }
            else
            {
                return null;
            }
        }
        var request = new AgentTextWriteRequest(
            operationId,
            AgentTextWriteAction.Organize(operations),
            callerName,
            callerPurpose);
        return request.IsBounded ? operations : null;
    }

    private static List<BrokerCredentialComponentInput>? ParseComponents(JsonArray raw)
    {
        if (raw.Count is < 1 or > 64) return null;
        var inputs = new List<BrokerCredentialComponentInput>();
        foreach (var node in raw)
        {
            if (node is not JsonObject item
                || item.Any(pair => pair.Key is not ("name" or "text" or "file" or "delivery" or "masked"))
                || String(item, "name") is not { } name
                || item["delivery"] is not JsonObject mapping
                || String(mapping, "type") is not { } type)
            {
                return null;
            }
            BrokerComponentDelivery delivery;
            if (type == "none")
            {
                if (mapping.Count != 1) return null;
                delivery = BrokerComponentDelivery.None;
            }
            else if (type is "environment_variable" or "temporary_file")
            {
                if (mapping.Count != 2
                    || String(mapping, "environment_variable") is not { } variable)
                {
                    return null;
                }
                delivery = type == "environment_variable"
                    ? BrokerComponentDelivery.EnvironmentVariable(variable)
                    : BrokerComponentDelivery.TemporaryFile(variable);
            }
            else
            {
                return null;
            }

            BrokerCredentialComponentValue value;
            var hasText = item.TryGetPropertyValue("text", out var textNode);
            var hasFile = item.TryGetPropertyValue("file", out var fileNode);
            if (hasText && !hasFile && textNode is JsonValue textValue
                && textValue.TryGetValue<string>(out var text))
            {
                value = BrokerCredentialComponentValue.FromText(text);
            }
            else if (!hasText && hasFile && fileNode is JsonObject file
                     && file.Count == 3
                     && String(file, "upload_id") is { } uploadId
                     && String(file, "capability") is { } capability
                     && String(file, "digest") is { } digest)
            {
                value = BrokerCredentialComponentValue.FromFile(
                    new BrokerComponentFileReference(uploadId, capability, digest));
            }
            else
            {
                return null;
            }

            var masked = true;
            if (item.TryGetPropertyValue("masked", out var maskedNode))
            {
                if (maskedNode is not JsonValue maskedValue || !maskedValue.TryGetValue<bool>(out masked))
                {
                    return null;
                }
            }
            inputs.Add(new BrokerCredentialComponentInput
            {
                Name = name,
                Value = value,
                Delivery = delivery,
                Masked = masked,
            });
        }
        return inputs;
    }

    private static bool ParseChanges(
        JsonObject arguments,
        out IReadOnlyList<BrokerCredentialComponentChange>? changes,
        out string? error)
    {
        changes = null;
        error = null;
        if (!arguments.TryGetPropertyValue("changes", out var changesNode)) return true;
        if (changesNode is null || changesNode is not JsonArray raw)
        {
            return false;
        }
        if (raw.Any(node => node is not JsonObject))
        {
            return false;
        }
        if (raw.Count is < 1 or > 64)
        {
            error = "changes must contain between 1 and 64 entries";
            return false;
        }
        var parsed = new List<BrokerCredentialComponentChange>();
        foreach (var node in raw)
        {
            var item = (JsonObject)node!;
            if (item.Count != 1)
            {
                error = "Each change requires one upsert or remove";
                return false;
            }
            if (String(item, "remove") is { } remove)
            {
                parsed.Add(BrokerCredentialComponentChange.Remove(remove));
                continue;
            }
            if (item["upsert"] is JsonObject upsert)
            {
                var component = ParseComponents(new JsonArray { upsert.DeepClone() });
                if (component is not { Count: 1 })
                {
                    error = "Each change requires one upsert or remove";
                    return false;
                }
                parsed.Add(BrokerCredentialComponentChange.UpsertComponent(component[0]));
                continue;
            }
            error = "Each change requires one upsert or remove";
            return false;
        }
        changes = parsed;
        return true;
    }

    private static bool OptionalString(
        JsonObject arguments,
        string name,
        out string? value,
        bool allowNull = true)
    {
        value = null;
        if (!arguments.TryGetPropertyValue(name, out var node)) return true;
        if (node is null) return allowNull;
        if (node is not JsonValue json || !json.TryGetValue<string>(out var text)) return false;
        value = text;
        return true;
    }

    private static bool OptionalGroup(JsonObject arguments, out BrokerCredentialGroupChange? group)
    {
        group = null;
        if (!arguments.TryGetPropertyValue("group", out var node)) return true;
        if (node is null)
        {
            group = BrokerCredentialGroupChange.Clear;
            return true;
        }
        if (node is not JsonValue value || !value.TryGetValue<string>(out var name)) return false;
        group = BrokerCredentialGroupChange.Named(name);
        return true;
    }

    private static bool HasGroup(JsonObject arguments)
        => arguments.ContainsKey("group");

    private static string? String(JsonObject? node, string name)
        => node?[name] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static int? Int(JsonObject node, string name)
    {
        if (node[name] is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<long>(out var wide) && wide is >= int.MinValue and <= int.MaxValue)
        {
            return (int)wide;
        }
        return null;
    }

    private static long? Long(JsonObject node, string name)
    {
        if (node[name] is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<int>(out var narrow)) return narrow;
        return null;
    }

    private static JsonArray? Array(JsonObject node, string name) => node[name] as JsonArray;

    private static List<string>? StringArray(JsonObject node, string name)
    {
        if (node[name] is not JsonArray array) return null;
        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var text)) return null;
            values.Add(text);
        }
        return values;
    }
}

