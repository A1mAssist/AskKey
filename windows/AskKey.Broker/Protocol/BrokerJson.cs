using System.Text.Json;
using System.Text.Json.Nodes;
using AskKey.Broker.Approval;

namespace AskKey.Broker.Protocol;

/// <summary>
/// Swift-synthesized Codable shape. Enums with associated values encode as
/// <c>{"case":{"_0":...}}</c> (or the labeled keys), optionals are omitted, and
/// <c>Data</c> is base64. Decoding is tolerant of unknown keys, matching Swift.
/// </summary>
public static partial class BrokerJson
{
    public static byte[] Encode(BrokerRequest request)
        => JsonSerializer.SerializeToUtf8Bytes(EncodeRequest(request));

    public static BrokerRequest? DecodeRequest(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return DecodeRequest(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static byte[] Encode(BrokerResponse response)
        => JsonSerializer.SerializeToUtf8Bytes(EncodeResponse(response));

    public static BrokerResponse? DecodeResponse(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return DecodeResponse(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static JsonObject EncodeRequest(BrokerRequest request)
    {
        var json = new JsonObject
        {
            ["version"] = request.Version,
            ["method"] = request.Method,
        };
        Put(json, "requestID", request.RequestId);
        Put(json, "capability", request.Capability);
        if (request.TextRun is { } textRun) json["textRun"] = EncodeTextRunRequest(textRun);
        if (request.FileWrite is { } fileWrite) json["fileWrite"] = EncodeFileWriteRequest(fileWrite);
        Put(json, "operationID", request.OperationId);
        if (request.TextWrite is { } textWrite) json["textWrite"] = EncodeTextWriteRequest(textWrite);
        Put(json, "runtimeChannel", request.RuntimeChannel);
        return json;
    }

    public static BrokerRequest DecodeRequest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("request");
        return new BrokerRequest
        {
            Version = RequiredInt(root, "version"),
            Method = RequiredString(root, "method"),
            RequestId = OptionalString(root, "requestID"),
            Capability = OptionalString(root, "capability"),
            TextRun = Optional(root, "textRun") is { } textRun ? DecodeTextRunRequest(textRun) : null,
            FileWrite = Optional(root, "fileWrite") is { } fileWrite ? DecodeFileWriteRequest(fileWrite) : null,
            OperationId = OptionalString(root, "operationID"),
            TextWrite = Optional(root, "textWrite") is { } textWrite ? DecodeTextWriteRequest(textWrite) : null,
            RuntimeChannel = OptionalString(root, "runtimeChannel"),
        };
    }

    public static JsonObject EncodeResponse(BrokerResponse response)
    {
        if (response.ResponseKind == BrokerResponse.Kind.Success)
        {
            return new JsonObject { ["success"] = new JsonObject { ["_0"] = EncodePayload(response.Payload!) } };
        }
        return new JsonObject
        {
            ["failure"] = new JsonObject { ["_0"] = BrokerWire.ToWire(response.Error) },
        };
    }

    public static BrokerResponse DecodeResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("response");
        if (Case(root, "success") is { } success) return BrokerResponse.Success(DecodePayload(Required(success, "_0")));
        if (Case(root, "failure") is { } failure)
        {
            if (!BrokerWire.TryParseErrorCode(RequiredString(failure, "_0"), out var code))
            {
                throw new FormatException("error code");
            }
            return BrokerResponse.Failure(code);
        }
        throw new FormatException("response case");
    }

    public static JsonObject EncodePayload(BrokerPayload payload)
    {
        switch (payload.PayloadKind)
        {
            case BrokerPayload.Kind.Health:
                return Case("health", new JsonObject { ["_0"] = EncodeHealth(payload.Health!) });
            case BrokerPayload.Kind.Version:
                return Case("version", new JsonObject
                {
                    ["_0"] = new JsonObject { ["protocolVersion"] = payload.Version!.ProtocolVersion },
                });
            case BrokerPayload.Kind.Catalog:
                var catalog = new JsonObject
                {
                    ["_0"] = new JsonArray((payload.CatalogItems ?? Array.Empty<BrokerCatalogItem>())
                        .Select(item => (JsonNode)EncodeCatalogItem(item)).ToArray()),
                };
                if (payload.CatalogGroups is { } groups)
                {
                    catalog["groups"] = new JsonArray(groups.Select(group => (JsonNode)JsonValue.Create(group)!).ToArray());
                }
                return new JsonObject { ["catalog"] = catalog };
            case BrokerPayload.Kind.RequestStatus:
                // Swift synthesizes `{"requestStatus":{"_0":"pending"}}` for an enum
                // case with one associated value; keep that exact shape.
                return Case("requestStatus", new JsonObject
                {
                    ["_0"] = BrokerWire.ToWire(payload.RequestState),
                });
            case BrokerPayload.Kind.TextRun:
                return Case("textRun", new JsonObject { ["_0"] = EncodeTextRunResult(payload.TextRun!) });
            case BrokerPayload.Kind.FileWrite:
                return Case("fileWrite", new JsonObject { ["_0"] = EncodeFileWritePayload(payload.FileWrite!) });
            case BrokerPayload.Kind.TextWriteRequest:
                return Case("textWriteRequest", new JsonObject { ["_0"] = EncodeTextWriteOutcome(payload.TextWriteRequest!) });
            case BrokerPayload.Kind.TextWriteResult:
                return Case("textWriteResult", new JsonObject { ["_0"] = EncodeTextWriteResult(payload.TextWriteResult!) });
            case BrokerPayload.Kind.OrganizationWriteResult:
                return new JsonObject
                {
                    ["organizationWriteResult"] = new JsonObject
                    {
                        ["operationID"] = payload.OrganizationOperationId,
                    },
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(payload));
        }
    }

    public static BrokerPayload DecodePayload(JsonElement element)
    {
        if (Case(element, "health") is { } health)
        {
            var inner = Required(health, "_0");
            return BrokerPayload.FromHealth(new BrokerHealth(
                RequiredInt(inner, "version"), RequiredString(inner, "status")));
        }
        if (Case(element, "version") is { } version)
        {
            var inner = Required(version, "_0");
            return BrokerPayload.FromVersion(new BrokerVersion(RequiredInt(inner, "protocolVersion")));
        }
        if (Case(element, "catalog") is { } catalog)
        {
            var items = new List<BrokerCatalogItem>();
            if (Optional(catalog, "_0") is { } array)
            {
                foreach (var item in array.EnumerateArray()) items.Add(DecodeCatalogItem(item));
            }
            IReadOnlyList<string>? groups = null;
            if (Optional(catalog, "groups") is { ValueKind: JsonValueKind.Array } groupArray)
            {
                groups = groupArray.EnumerateArray().Select(value => value.GetString()!).ToArray();
            }
            return BrokerPayload.FromCatalog(items, groups);
        }
        if (Case(element, "requestStatus") is { } status)
        {
            if (!BrokerWire.TryParseRequestState(RequiredString(status, "_0"), out var state))
            {
                throw new FormatException("request state");
            }
            return BrokerPayload.FromRequestStatus(state);
        }
        if (Case(element, "textRun") is { } textRun)
        {
            return BrokerPayload.FromTextRun(DecodeTextRunResult(Required(textRun, "_0")));
        }
        if (Case(element, "fileWrite") is { } fileWrite)
        {
            return BrokerPayload.FromFileWrite(DecodeFileWritePayload(Required(fileWrite, "_0")));
        }
        if (Case(element, "textWriteRequest") is { } writeRequest)
        {
            return BrokerPayload.FromTextWriteRequest(DecodeTextWriteOutcome(Required(writeRequest, "_0")));
        }
        if (Case(element, "textWriteResult") is { } writeResult)
        {
            return BrokerPayload.FromTextWriteResult(DecodeTextWriteResult(Required(writeResult, "_0")));
        }
        if (Case(element, "organizationWriteResult") is { } organization)
        {
            return BrokerPayload.FromOrganizationWriteResult(RequiredString(organization, "operationID"));
        }
        throw new FormatException("payload case");
    }
    public static JsonObject EncodeHealth(BrokerHealth health)
        => new() { ["version"] = health.Version, ["status"] = health.Status };

    public static JsonObject EncodeCatalogItem(BrokerCatalogItem item)
    {
        var json = new JsonObject();
        Put(json, "credentialID", item.CredentialId);
        if (item.Components is { } components)
        {
            json["components"] = new JsonArray(components.Select(component => (JsonNode)new JsonObject
            {
                ["name"] = component.Name,
                ["payloadKind"] = BrokerWire.ToWire(component.PayloadKind),
                ["delivery"] = EncodeDelivery(component.Delivery),
            }).ToArray());
        }
        json["name"] = item.Name;
        json["payloadKind"] = BrokerWire.ToWire(item.PayloadKind);
        json["usageInstructions"] = item.UsageInstructions;
        json["group"] = item.Group;
        Put(json, "environmentVariable", item.EnvironmentVariable);
        json["expired"] = item.Expired;
        return json;
    }

    public static BrokerCatalogItem DecodeCatalogItem(JsonElement element)
    {
        IReadOnlyList<BrokerCatalogComponent>? components = null;
        if (Optional(element, "components") is { ValueKind: JsonValueKind.Array } array)
        {
            components = array.EnumerateArray().Select(component => new BrokerCatalogComponent
            {
                Name = RequiredString(component, "name"),
                PayloadKind = RequiredPayloadKind(component, "payloadKind"),
                Delivery = DecodeDelivery(Required(component, "delivery")),
            }).ToArray();
        }
        if (!BrokerWire.TryParsePayloadKind(RequiredString(element, "payloadKind"), out var payloadKind))
        {
            throw new FormatException("payload kind");
        }
        return new BrokerCatalogItem
        {
            CredentialId = OptionalString(element, "credentialID"),
            Components = components,
            Name = RequiredString(element, "name"),
            PayloadKind = payloadKind,
            UsageInstructions = RequiredString(element, "usageInstructions"),
            Group = OptionalString(element, "group"),
            EnvironmentVariable = OptionalString(element, "environmentVariable"),
            Expired = RequiredBool(element, "expired"),
        };
    }

    public static JsonObject EncodeDelivery(BrokerComponentDelivery delivery) => delivery.DeliveryKind switch
    {
        BrokerComponentDelivery.Kind.EnvironmentVariable =>
            Case("environmentVariable", new JsonObject { ["_0"] = delivery.Name! }),
        BrokerComponentDelivery.Kind.TemporaryFile =>
            Case("temporaryFile", new JsonObject { ["_0"] = delivery.Name! }),
        _ => Case("none", new JsonObject()),
    };

    public static BrokerComponentDelivery DecodeDelivery(JsonElement element)
    {
        if (Case(element, "environmentVariable") is { } variable)
        {
            return BrokerComponentDelivery.EnvironmentVariable(RequiredString(variable, "_0"));
        }
        if (Case(element, "temporaryFile") is { } file)
        {
            return BrokerComponentDelivery.TemporaryFile(RequiredString(file, "_0"));
        }
        if (Case(element, "none") is not null) return BrokerComponentDelivery.None;
        throw new FormatException("delivery");
    }

    public static JsonObject EncodeTextRunRequest(BrokerTextRunRequest request)
    {
        var environment = new JsonObject();
        foreach (var (key, value) in request.InheritedEnvironment) environment[key] = value;
        var json = new JsonObject
        {
            ["operationID"] = request.OperationId,
            ["command"] = new JsonArray(request.Command.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
            ["credentialNames"] = new JsonArray(request.CredentialNames.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
        };
        Put(json, "workingDirectory", request.WorkingDirectory);
        json["inheritedEnvironment"] = environment;
        Put(json, "callerName", request.CallerName);
        Put(json, "callerPurpose", request.CallerPurpose);
        return json;
    }

    public static BrokerTextRunRequest DecodeTextRunRequest(JsonElement element)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Optional(element, "inheritedEnvironment") is { ValueKind: JsonValueKind.Object } env)
        {
            foreach (var property in env.EnumerateObject())
            {
                environment[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }
        return new BrokerTextRunRequest(
            RequiredString(element, "operationID"),
            RequiredStringArray(element, "command"),
            RequiredStringArray(element, "credentialNames"),
            OptionalString(element, "workingDirectory"),
            environment,
            OptionalString(element, "callerName"),
            OptionalString(element, "callerPurpose"));
    }

    public static JsonObject EncodeTextRunResult(BrokerTextRunResult result) => result.ResultKind switch
    {
        BrokerTextRunResult.Kind.Exited => Case("exited", new JsonObject { ["_0"] = result.ExitCode }),
        BrokerTextRunResult.Kind.ApprovalRequired => Case("approvalRequired", new JsonObject
        {
            ["operationID"] = result.OperationId,
            ["tickets"] = new JsonArray(result.Tickets.Select(ticket => (JsonNode)EncodeTicket(ticket)).ToArray()),
        }),
        _ => Case("outcomeUnknown", new JsonObject()),
    };

    public static BrokerTextRunResult DecodeTextRunResult(JsonElement element)
    {
        if (Case(element, "exited") is { } exited) return BrokerTextRunResult.Exited(RequiredInt(exited, "_0"));
        if (Case(element, "approvalRequired") is { } approval)
        {
            var tickets = new List<BrokerApprovalTicket>();
            if (Optional(approval, "tickets") is { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var ticket in array.EnumerateArray()) tickets.Add(DecodeTicket(ticket));
            }
            return BrokerTextRunResult.ApprovalRequired(RequiredString(approval, "operationID"), tickets);
        }
        if (Case(element, "outcomeUnknown") is not null) return BrokerTextRunResult.OutcomeUnknown;
        throw new FormatException("text run result");
    }

    public static JsonObject EncodeTicket(BrokerApprovalTicket ticket) => new()
    {
        ["requestID"] = ticket.RequestId,
        ["capability"] = ticket.Capability,
        ["state"] = BrokerWire.ToWire(ticket.State),
        ["retryCount"] = ticket.RetryCount,
    };

    public static BrokerApprovalTicket DecodeTicket(JsonElement element)
    {
        if (!BrokerWire.TryParseRequestState(RequiredString(element, "state"), out var state))
        {
            throw new FormatException("ticket state");
        }
        return new BrokerApprovalTicket(
            RequiredString(element, "requestID"),
            RequiredString(element, "capability"),
            state,
            RequiredInt(element, "retryCount"));
    }
    public static JsonObject EncodeFileWriteRequest(BrokerFileWriteRequest request) => request.RequestKind switch
    {
        BrokerFileWriteRequest.Kind.BeginComponent => Case("beginComponent", new JsonObject
        {
            ["_0"] = new JsonObject
            {
                ["operationID"] = request.ComponentBegin!.OperationId,
                ["originalFilename"] = request.ComponentBegin.OriginalFilename,
                ["expectedByteCount"] = request.ComponentBegin.ExpectedByteCount,
            },
        }),
        BrokerFileWriteRequest.Kind.FreezeComponent => Case("freezeComponent", EncodeFreeze(request.FreezeRequest!)),
        BrokerFileWriteRequest.Kind.CancelUpload => Case("cancelUpload", EncodeFreeze(request.FreezeRequest!)),
        BrokerFileWriteRequest.Kind.Begin => Case("begin", new JsonObject
        {
            ["_0"] = new JsonObject
            {
                ["operationID"] = request.BeginRequest!.OperationId,
                ["credentialID"] = request.BeginRequest.CredentialId,
                ["targetID"] = request.BeginRequest.TargetId,
                ["operation"] = BrokerWire.ToWire(request.BeginRequest.Operation),
                ["originalFilename"] = request.BeginRequest.OriginalFilename,
                ["expectedByteCount"] = request.BeginRequest.ExpectedByteCount,
            },
        }),
        BrokerFileWriteRequest.Kind.Append => Case("append", new JsonObject
        {
            ["_0"] = new JsonObject
            {
                ["uploadID"] = request.ChunkRequest!.UploadId,
                ["capability"] = request.ChunkRequest.Capability,
                ["offset"] = request.ChunkRequest.Offset,
                ["bytes"] = global::System.Convert.ToBase64String(request.ChunkRequest.Bytes),
            },
        }),
        _ => Case("freeze", EncodeFreeze(request.FreezeRequest!)),
    };

    private static JsonObject EncodeFreeze(BrokerFileWriteFreezeRequest request) => new()
    {
        ["_0"] = new JsonObject
        {
            ["uploadID"] = request.UploadId,
            ["capability"] = request.Capability,
        },
    };

    public static BrokerFileWriteRequest DecodeFileWriteRequest(JsonElement element)
    {
        if (Case(element, "beginComponent") is { } beginComponent)
        {
            var inner = Required(beginComponent, "_0");
            return BrokerFileWriteRequest.BeginComponent(new BrokerComponentUploadBeginRequest(
                RequiredString(inner, "operationID"),
                RequiredString(inner, "originalFilename"),
                RequiredInt(inner, "expectedByteCount")));
        }
        if (Case(element, "freezeComponent") is { } freezeComponent)
        {
            return BrokerFileWriteRequest.FreezeComponent(DecodeFreeze(freezeComponent));
        }
        if (Case(element, "cancelUpload") is { } cancel)
        {
            return BrokerFileWriteRequest.CancelUpload(DecodeFreeze(cancel));
        }
        if (Case(element, "begin") is { } begin)
        {
            var inner = Required(begin, "_0");
            if (!BrokerWire.TryParseApprovalOperation(RequiredString(inner, "operation"), out var operation))
            {
                throw new FormatException("approval operation");
            }
            return BrokerFileWriteRequest.Begin(new BrokerFileWriteBeginRequest(
                RequiredString(inner, "operationID"),
                RequiredString(inner, "credentialID"),
                RequiredString(inner, "targetID"),
                operation,
                RequiredString(inner, "originalFilename"),
                RequiredInt(inner, "expectedByteCount")));
        }
        if (Case(element, "append") is { } append)
        {
            var inner = Required(append, "_0");
            return BrokerFileWriteRequest.Append(new BrokerFileWriteChunkRequest(
                RequiredString(inner, "uploadID"),
                RequiredString(inner, "capability"),
                RequiredInt(inner, "offset"),
                global::System.Convert.FromBase64String(RequiredString(inner, "bytes"))));
        }
        if (Case(element, "freeze") is { } freeze) return BrokerFileWriteRequest.Freeze(DecodeFreeze(freeze));
        throw new FormatException("file write request");
    }

    private static BrokerFileWriteFreezeRequest DecodeFreeze(JsonElement element)
    {
        var inner = Required(element, "_0");
        return new BrokerFileWriteFreezeRequest(
            RequiredString(inner, "uploadID"), RequiredString(inner, "capability"));
    }

    public static JsonObject EncodeFileWritePayload(BrokerFileWritePayload payload) => payload.PayloadKind switch
    {
        BrokerFileWritePayload.Kind.ComponentFrozen => Case("componentFrozen", new JsonObject
        {
            ["_0"] = new JsonObject
            {
                ["uploadID"] = payload.ComponentReference!.UploadId,
                ["capability"] = payload.ComponentReference.Capability,
                ["digest"] = payload.ComponentReference.Digest,
            },
        }),
        BrokerFileWritePayload.Kind.UploadCancelled => Case("uploadCancelled", new JsonObject()),
        BrokerFileWritePayload.Kind.Upload => Case("upload", new JsonObject
        {
            ["_0"] = new JsonObject
            {
                ["uploadID"] = payload.UploadTicket!.UploadId,
                ["capability"] = payload.UploadTicket.Capability,
            },
        }),
        BrokerFileWritePayload.Kind.ChunkAccepted => Case("chunkAccepted", new JsonObject
        {
            ["nextOffset"] = payload.NextOffset,
        }),
        _ => Case("approval", new JsonObject { ["_0"] = EncodeTicket(payload.ApprovalTicket!.Value) }),
    };

    public static BrokerFileWritePayload DecodeFileWritePayload(JsonElement element)
    {
        if (Case(element, "componentFrozen") is { } frozen)
        {
            var inner = Required(frozen, "_0");
            return BrokerFileWritePayload.ComponentFrozen(new BrokerComponentFileReference(
                RequiredString(inner, "uploadID"),
                RequiredString(inner, "capability"),
                RequiredString(inner, "digest")));
        }
        if (Case(element, "uploadCancelled") is not null) return BrokerFileWritePayload.UploadCancelled;
        if (Case(element, "upload") is { } upload)
        {
            var inner = Required(upload, "_0");
            return BrokerFileWritePayload.Upload(new BrokerFileUploadTicket(
                RequiredString(inner, "uploadID"), RequiredString(inner, "capability")));
        }
        if (Case(element, "chunkAccepted") is { } chunk)
        {
            return BrokerFileWritePayload.ChunkAccepted(RequiredInt(chunk, "nextOffset"));
        }
        if (Case(element, "approval") is { } approval)
        {
            return BrokerFileWritePayload.Approval(DecodeTicket(Required(approval, "_0")));
        }
        throw new FormatException("file write payload");
    }

    public static JsonObject EncodeTextWriteRequest(AgentTextWriteRequest request)
    {
        var json = new JsonObject
        {
            ["operationID"] = request.OperationId,
            ["action"] = EncodeTextWriteAction(request.Action),
        };
        Put(json, "callerName", request.CallerName);
        Put(json, "callerPurpose", request.CallerPurpose);
        return json;
    }

    public static AgentTextWriteRequest DecodeTextWriteRequest(JsonElement element)
        => new(
            RequiredString(element, "operationID"),
            DecodeTextWriteAction(Required(element, "action")),
            OptionalString(element, "callerName"),
            OptionalString(element, "callerPurpose"));

    public static JsonObject EncodeTextWriteAction(AgentTextWriteAction action) => action.ActionKind switch
    {
        AgentTextWriteActionKind.Organize => Case("organize", new JsonObject
        {
            ["_0"] = new JsonArray((action.OrganizationOperations ?? Array.Empty<BrokerOrganizationOperation>())
                .Select(operation => (JsonNode)EncodeOrganizationOperation(operation)).ToArray()),
        }),
        AgentTextWriteActionKind.CreateBundle => Case("createBundle", EncodeCreateBundle(action)),
        AgentTextWriteActionKind.ModifyBundle => Case("modifyBundle", EncodeModifyBundle(action)),
        AgentTextWriteActionKind.Create => Case("create", new JsonObject
        {
            ["name"] = action.Name,
            ["value"] = action.Value,
        }),
        AgentTextWriteActionKind.Modify => Case("modify", new JsonObject
        {
            ["name"] = action.Name,
            ["value"] = action.Value,
        }),
        _ => Case("delete", new JsonObject { ["name"] = action.Name }),
    };

    private static JsonObject EncodeCreateBundle(AgentTextWriteAction action)
    {
        var json = new JsonObject
        {
            ["name"] = action.Name,
            ["components"] = new JsonArray((action.Components ?? Array.Empty<BrokerCredentialComponentInput>())
                .Select(component => (JsonNode)EncodeComponentInput(component)).ToArray()),
        };
        Put(json, "usageInstructions", action.UsageInstructions);
        Put(json, "group", action.Group);
        return json;
    }

    private static JsonObject EncodeModifyBundle(AgentTextWriteAction action)
    {
        var json = new JsonObject
        {
            ["name"] = action.Name,
            ["changes"] = new JsonArray((action.Changes ?? Array.Empty<BrokerCredentialComponentChange>())
                .Select(change => (JsonNode)EncodeComponentChange(change)).ToArray()),
        };
        Put(json, "usageInstructions", action.UsageInstructions);
        if (action.GroupChange is { } group) json["group"] = EncodeGroupChange(group);
        return json;
    }

    public static AgentTextWriteAction DecodeTextWriteAction(JsonElement element)
    {
        if (Case(element, "organize") is { } organize)
        {
            var operations = new List<BrokerOrganizationOperation>();
            if (Optional(organize, "_0") is { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var operation in array.EnumerateArray()) operations.Add(DecodeOrganizationOperation(operation));
            }
            return AgentTextWriteAction.Organize(operations);
        }
        if (Case(element, "createBundle") is { } createBundle)
        {
            var components = new List<BrokerCredentialComponentInput>();
            if (Optional(createBundle, "components") is { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var component in array.EnumerateArray()) components.Add(DecodeComponentInput(component));
            }
            return AgentTextWriteAction.CreateBundle(
                RequiredString(createBundle, "name"),
                components,
                OptionalString(createBundle, "usageInstructions"),
                OptionalString(createBundle, "group"));
        }
        if (Case(element, "modifyBundle") is { } modifyBundle)
        {
            var changes = new List<BrokerCredentialComponentChange>();
            if (Optional(modifyBundle, "changes") is { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var change in array.EnumerateArray()) changes.Add(DecodeComponentChange(change));
            }
            return AgentTextWriteAction.ModifyBundle(
                RequiredString(modifyBundle, "name"),
                changes,
                OptionalString(modifyBundle, "usageInstructions"),
                Optional(modifyBundle, "group") is { ValueKind: JsonValueKind.Object } group
                    ? DecodeGroupChange(group)
                    : null);
        }
        if (Case(element, "create") is { } create)
        {
            return AgentTextWriteAction.Create(
                RequiredString(create, "name"), RequiredString(create, "value"));
        }
        if (Case(element, "modify") is { } modify)
        {
            return AgentTextWriteAction.Modify(
                RequiredString(modify, "name"), RequiredString(modify, "value"));
        }
        if (Case(element, "delete") is { } delete)
        {
            return AgentTextWriteAction.Delete(RequiredString(delete, "name"));
        }
        throw new FormatException("text write action");
    }

    public static JsonObject EncodeComponentInput(BrokerCredentialComponentInput input) => new()
    {
        ["name"] = input.Name,
        ["value"] = input.Value.Text is { } text
            ? Case("text", new JsonObject { ["_0"] = text })
            : Case("file", new JsonObject
            {
                ["_0"] = new JsonObject
                {
                    ["uploadID"] = input.Value.File!.UploadId,
                    ["capability"] = input.Value.File.Capability,
                    ["digest"] = input.Value.File.Digest,
                },
            }),
        ["delivery"] = EncodeDelivery(input.Delivery),
        ["masked"] = input.Masked,
    };

    public static BrokerCredentialComponentInput DecodeComponentInput(JsonElement element)
    {
        var value = Required(element, "value");
        BrokerCredentialComponentValue resolved;
        if (Case(value, "text") is { } text)
        {
            resolved = BrokerCredentialComponentValue.FromText(RequiredString(text, "_0"));
        }
        else if (Case(value, "file") is { } file)
        {
            var inner = Required(file, "_0");
            resolved = BrokerCredentialComponentValue.FromFile(new BrokerComponentFileReference(
                RequiredString(inner, "uploadID"),
                RequiredString(inner, "capability"),
                RequiredString(inner, "digest")));
        }
        else
        {
            throw new FormatException("component value");
        }
        return new BrokerCredentialComponentInput
        {
            Name = RequiredString(element, "name"),
            Value = resolved,
            Delivery = DecodeDelivery(Required(element, "delivery")),
            Masked = OptionalBool(element, "masked") ?? true,
        };
    }

    public static JsonObject EncodeComponentChange(BrokerCredentialComponentChange change)
        => change.Upsert is { } upsert
            ? Case("upsert", new JsonObject { ["_0"] = EncodeComponentInput(upsert) })
            : Case("remove", new JsonObject { ["_0"] = change.RemoveName! });

    public static BrokerCredentialComponentChange DecodeComponentChange(JsonElement element)
    {
        if (Case(element, "upsert") is { } upsert)
        {
            return BrokerCredentialComponentChange.UpsertComponent(DecodeComponentInput(Required(upsert, "_0")));
        }
        if (Case(element, "remove") is { } remove)
        {
            return BrokerCredentialComponentChange.Remove(RequiredString(remove, "_0"));
        }
        throw new FormatException("component change");
    }

    public static JsonObject EncodeGroupChange(BrokerCredentialGroupChange change)
        => change.Ungrouped
            ? Case("ungrouped", new JsonObject())
            : Case("named", new JsonObject { ["_0"] = change.Name! });

    public static BrokerCredentialGroupChange DecodeGroupChange(JsonElement element)
    {
        if (Case(element, "ungrouped") is not null) return BrokerCredentialGroupChange.Clear;
        if (Case(element, "named") is { } named)
        {
            return BrokerCredentialGroupChange.Named(RequiredString(named, "_0"));
        }
        throw new FormatException("group change");
    }

    private static JsonObject EncodeMove(BrokerOrganizationOperation operation)
    {
        var json = new JsonObject { ["credential"] = operation.Credential };
        Put(json, "group", operation.Group);
        return json;
    }

    public static JsonObject EncodeOrganizationOperation(BrokerOrganizationOperation operation)
        => operation.OperationKind switch
        {
            BrokerOrganizationOperation.Kind.Move => Case("move", EncodeMove(operation)),
            BrokerOrganizationOperation.Kind.CreateGroup =>
                Case("createGroup", new JsonObject { ["_0"] = operation.Name! }),
            BrokerOrganizationOperation.Kind.RenameGroup => Case("renameGroup", new JsonObject
            {
                ["from"] = operation.From,
                ["to"] = operation.To,
            }),
            _ => Case("deleteGroup", new JsonObject { ["_0"] = operation.Name! }),
        };

    public static BrokerOrganizationOperation DecodeOrganizationOperation(JsonElement element)
    {
        if (Case(element, "move") is { } move)
        {
            return BrokerOrganizationOperation.Move(
                RequiredString(move, "credential"), OptionalString(move, "group"));
        }
        if (Case(element, "createGroup") is { } create) return BrokerOrganizationOperation.CreateGroup(RequiredString(create, "_0"));
        if (Case(element, "renameGroup") is { } rename)
        {
            return BrokerOrganizationOperation.RenameGroup(
                RequiredString(rename, "from"), RequiredString(rename, "to"));
        }
        if (Case(element, "deleteGroup") is { } delete) return BrokerOrganizationOperation.DeleteGroup(RequiredString(delete, "_0"));
        throw new FormatException("organization operation");
    }

    public static JsonObject EncodeTextWriteOutcome(AgentTextWriteRequestOutcome outcome)
        => outcome.Submission is { } submission
            ? Case("submitted", new JsonObject { ["_0"] = EncodeSubmission(submission) })
            : Case("completed", new JsonObject { ["_0"] = EncodeTextWriteResult(outcome.CompletedResult!) });

    public static AgentTextWriteRequestOutcome DecodeTextWriteOutcome(JsonElement element)
    {
        if (Case(element, "submitted") is { } submitted)
        {
            return AgentTextWriteRequestOutcome.Submitted(DecodeSubmission(Required(submitted, "_0")));
        }
        if (Case(element, "completed") is { } completed)
        {
            return AgentTextWriteRequestOutcome.Completed(DecodeTextWriteResult(Required(completed, "_0")));
        }
        throw new FormatException("write outcome");
    }

    public static JsonObject EncodeSubmission(AgentTextWriteSubmission submission) => new()
    {
        ["operationID"] = submission.OperationId,
        ["requestID"] = submission.RequestId,
        ["capability"] = submission.Capability,
        ["state"] = BrokerWire.ToWire(submission.State),
        ["retryCount"] = submission.RetryCount,
    };

    public static AgentTextWriteSubmission DecodeSubmission(JsonElement element)
    {
        if (!BrokerWire.TryParseRequestState(RequiredString(element, "state"), out var state))
        {
            throw new FormatException("submission state");
        }
        return new AgentTextWriteSubmission(
            RequiredString(element, "operationID"),
            RequiredString(element, "requestID"),
            RequiredString(element, "capability"),
            state,
            RequiredInt(element, "retryCount"));
    }

    public static JsonObject EncodeTextWriteResult(AgentTextWriteResult result) => new()
    {
        ["operationID"] = result.OperationId,
        ["credentialID"] = result.CredentialId,
        ["state"] = BrokerWire.ToWire(result.State),
    };

    public static AgentTextWriteResult DecodeTextWriteResult(JsonElement element)
    {
        if (!BrokerWire.TryParseRequestState(RequiredString(element, "state"), out var state))
        {
            throw new FormatException("write result state");
        }
        return new AgentTextWriteResult(
            RequiredString(element, "operationID"),
            RequiredString(element, "credentialID"),
            state);
    }

    private static JsonObject Case(string name, JsonNode value) => new() { [name] = value };

    /// <summary>Returns the inner object of <c>{"case":{...}}</c>, or null when absent.</summary>
    private static JsonElement? Case(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var container)) return null;
        return container.ValueKind == JsonValueKind.Object ? container : null;
    }

    private static JsonElement? Optional(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static JsonElement Required(JsonElement element, string name)
        => Optional(element, name) ?? throw new FormatException(name);

    private static string RequiredString(JsonElement element, string name)
        => Required(element, name).GetString() ?? throw new FormatException(name);

    private static string? OptionalString(JsonElement element, string name)
        => Optional(element, name) is { } value ? value.GetString() : null;

    private static int RequiredInt(JsonElement element, string name)
    {
        var value = Required(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)
            || number is < int.MinValue or > int.MaxValue)
        {
            throw new FormatException(name);
        }
        return (int)number;
    }

    private static bool RequiredBool(JsonElement element, string name)
    {
        var value = Required(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException(name),
        };
    }

    private static bool? OptionalBool(JsonElement element, string name)
        => Optional(element, name) is { } value
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new FormatException(name),
            }
            : null;

    private static BrokerCatalogPayloadKind RequiredPayloadKind(JsonElement element, string name)
    {
        if (!BrokerWire.TryParsePayloadKind(RequiredString(element, name), out var kind))
        {
            throw new FormatException(name);
        }
        return kind;
    }

    private static IReadOnlyList<string> RequiredStringArray(JsonElement element, string name)
    {
        var value = Required(element, name);
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException(name);
        return value.EnumerateArray().Select(item => item.GetString() ?? throw new FormatException(name)).ToArray();
    }

    private static void Put(JsonObject json, string name, string? value)
    {
        if (value is not null) json[name] = value;
    }
}

