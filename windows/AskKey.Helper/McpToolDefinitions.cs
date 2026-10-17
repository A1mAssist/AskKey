using System.Text.Json.Nodes;

namespace AskKey.Helper;

internal static class McpToolDefinitions
{
    public static JsonArray All { get; } = Build();

    private static JsonArray Build()
    {
        var tools = new JsonArray
        {
            CreateCredential(),
            ModifyCredential(),
            BeginComponentUpload(),
            AppendComponentUpload(),
            FreezeComponentUpload(),
            CancelComponentUpload(),
            OrganizeCredentials(),
            CredentialDiscoveryGuardTool(),
            ConnectionStatus(),
            BeginFileWrite(),
            AppendFileWrite(),
            FreezeFileWrite(),
            TextWrite(
                "create_text_credential",
                "Request creation of an Agent-known text credential. Re-submit with request_id and capability after approval to commit.",
                requiresValue: true),
            TextWrite(
                "modify_text_credential",
                "Request modification of an Agent-known text credential. Re-submit with request_id and capability after approval to commit.",
                requiresValue: true),
            TextWrite(
                "delete_credential",
                "Request that a credential move to the App recycle bin. Re-submit with request_id and capability after approval to commit.",
                requiresValue: false),
            RequestStatus(),
            RequestCancel(),
            ListCredentials(),
            Run(),
        };
        return tools;
    }

    private static JsonObject CreateCredential()
    {
        var properties = CommonProperties();
        properties["components"] = new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = 1,
            ["maxItems"] = 64,
            ["items"] = ComponentSchema(),
        };
        properties["usage_instructions"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = AgentUsageGuide.MetadataWrites,
        };
        properties["group"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Reuse a matching group or create it on approved commit. Omitted means Ungrouped.",
        };
        return Definition(
            "create_credential",
            "Create a whole credential with text/file components under one frozen approval. Files use operation-bound staged references; never local paths.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray("name", "operation_id", "components"),
            });
    }

    private static JsonObject ModifyCredential()
    {
        var properties = CommonProperties();
        properties["usage_instructions"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = AgentUsageGuide.MetadataWrites + " Omitted means unchanged; empty string clears instructions.",
        };
        properties["group"] = new JsonObject
        {
            ["type"] = new JsonArray("string", "null"),
            ["description"] = "Omitted means unchanged; null means Ungrouped. Unknown names create groups on approved commit.",
        };
        properties["changes"] = new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = 1,
            ["maxItems"] = 64,
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["oneOf"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["properties"] = new JsonObject { ["upsert"] = ComponentSchema() },
                        ["required"] = new JsonArray("upsert"),
                        ["additionalProperties"] = false,
                    },
                    new JsonObject
                    {
                        ["properties"] = new JsonObject { ["remove"] = StringSchema() },
                        ["required"] = new JsonArray("remove"),
                        ["additionalProperties"] = false,
                    },
                },
            },
        };
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("name", "operation_id"),
            ["anyOf"] = new JsonArray
            {
                new JsonObject { ["required"] = new JsonArray("changes") },
                new JsonObject { ["required"] = new JsonArray("usage_instructions") },
                new JsonObject { ["required"] = new JsonArray("group") },
            },
        };
        return Definition(
            "modify_credential",
            "Atomically change components, usage instructions and/or group; omitted fields are preserved. Repeat exact operation_id and payload with request_id/capability to commit.",
            schema);
    }

    private static JsonObject BeginComponentUpload()
        => Definition(
            "begin_component_upload",
            "Begin encrypted staging for a component in one credential operation.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["operation_id"] = StringSchema(),
                    ["filename"] = StringSchema(),
                    ["byte_count"] = new JsonObject { ["type"] = "integer" },
                },
                ["required"] = new JsonArray("operation_id", "filename", "byte_count"),
            });

    private static JsonObject AppendComponentUpload()
        => Definition(
            "append_component_upload",
            "Append a bounded base64 chunk to encrypted component staging.",
            AppendSchema());

    private static JsonObject FreezeComponentUpload()
        => Definition(
            "freeze_component_upload",
            "Freeze staged bytes and return a digest-bound reference for create_credential/modify_credential; this does not approve a write.",
            UploadSchema());

    private static JsonObject CancelComponentUpload()
        => Definition(
            "cancel_component_upload",
            "Cancel staging and remove its encrypted temporary payload.",
            UploadSchema());

    private static JsonObject OrganizeCredentials()
    {
        var move = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["credential"] = StringSchema(),
                ["group"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
            },
            ["required"] = new JsonArray("credential", "group"),
        };
        var rename = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["from"] = StringSchema(),
                ["to"] = StringSchema(),
            },
            ["required"] = new JsonArray("from", "to"),
        };
        JsonObject Operation(string key, JsonNode value) => new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject { [key] = value },
            ["required"] = new JsonArray(key),
        };

        var properties = new JsonObject
        {
            ["operation_id"] = StringSchema(),
            ["request_id"] = StringSchema(),
            ["capability"] = StringSchema(),
            ["caller_name"] = StringSchema(),
            ["caller_purpose"] = StringSchema(),
            ["operations"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = 64,
                ["items"] = new JsonObject
                {
                    ["oneOf"] = new JsonArray
                    {
                        Operation("move", move),
                        Operation("create_group", StringSchema()),
                        Operation("rename_group", rename),
                        Operation("delete_group", StringSchema()),
                    },
                },
            },
        };
        return Definition(
            "organize_credentials",
            AgentUsageGuide.OrganizationWrites,
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = properties,
                ["required"] = new JsonArray("operation_id", "operations"),
            });
    }

    private static JsonObject CredentialDiscoveryGuardTool()
        => Definition(
            "credential_discovery_guard",
            "Client adapter lifecycle hook for PreToolUse credential discovery. Configure as a native MCP tool hook; not a credential lookup or access tool. Does not read secrets or execute commands.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = StringSchema(),
                    ["turn_id"] = StringSchema(),
                    ["tool_name"] = StringSchema(),
                    ["tool_input"] = new JsonObject { ["type"] = "object" },
                },
                ["required"] = new JsonArray("session_id", "turn_id", "tool_name", "tool_input"),
            });

    private static JsonObject ConnectionStatus()
        => Definition(
            "connection_status",
            "Check that this helper can negotiate the local Ask Key Broker protocol.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
            },
            new JsonObject
            {
                ["readOnlyHint"] = true,
                ["openWorldHint"] = false,
            });

    private static JsonObject BeginFileWrite()
        => Definition(
            "begin_file_write",
            "Begin a bounded create or modify request for an Agent-known file value.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["operation_id"] = StringSchema(),
                    ["credential_id"] = StringSchema(),
                    ["target_id"] = StringSchema(),
                    ["operation"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("create", "modify"),
                    },
                    ["filename"] = StringSchema(),
                    ["byte_count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
                },
                ["required"] = new JsonArray(
                    "operation_id", "credential_id", "target_id", "operation", "filename", "byte_count"),
            });

    private static JsonObject AppendFileWrite()
        => Definition(
            "append_file_write",
            "Append one base64-encoded chunk to a file write upload.",
            AppendSchema(minimumOffset: true));

    private static JsonObject FreezeFileWrite()
        => Definition(
            "freeze_file_write",
            "Freeze uploaded bytes and create the App approval request.",
            UploadSchema());

    private static JsonObject RequestStatus()
        => Definition(
            "request_status",
            "Check an existing approval ticket using requestID as request_id and capability. This does not execute or consume it. For run, after approval retry the original run or CLI command with the same operation_id and identical payload. For credential writes, repeat the original write with request_id and capability. Stop on denial, cancellation or expiry.",
            RequestSchema(),
            new JsonObject
            {
                ["readOnlyHint"] = true,
                ["openWorldHint"] = false,
            });

    private static JsonObject RequestCancel()
        => Definition(
            "request_cancel",
            "Cancel one incomplete approval request using its capability.",
            RequestSchema());

    private static JsonObject ListCredentials()
        => Definition(
            "list_credentials",
            AgentUsageGuide.CatalogDescription,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["discovery_token"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Client lifecycle correlation token; omit on manual calls.",
                    },
                },
            },
            new JsonObject
            {
                ["readOnlyHint"] = true,
                ["openWorldHint"] = false,
            });

    private static JsonObject Run()
        => Definition(
            "run",
            AgentUsageGuide.RunDescription,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["credentials"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["items"] = StringSchema(),
                        ["description"] = "Exact visible credential names from list_credentials, not credentialID values. Select only credentials needed for this task.",
                    },
                    ["command"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["items"] = StringSchema(),
                        ["description"] = "Executable and arguments, not a shell string. Consume the environment variables specified by component delivery mappings; a shell must be explicitly invoked to expand variables. Keep saved values out of argv and output. For command output choose the CLI before starting.",
                    },
                    ["cwd"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Existing absolute working directory. Keep unchanged when resuming approval.",
                    },
                    ["operation_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Choose a unique ID before starting; keep the same ID and identical request for approval retries. If omitted, preserve the operationID returned by approvalRequired. A new task needs a new ID.",
                    },
                    ["caller_name"] = StringSchema(),
                    ["caller_purpose"] = StringSchema(),
                },
                ["required"] = new JsonArray("credentials", "command"),
            });

    private static JsonObject TextWrite(string name, string description, bool requiresValue)
    {
        var properties = new JsonObject
        {
            ["operation_id"] = StringSchema(),
            ["name"] = StringSchema(),
            ["caller_name"] = StringSchema(),
            ["caller_purpose"] = StringSchema(),
            ["request_id"] = StringSchema(),
            ["capability"] = StringSchema(),
        };
        var required = new JsonArray("operation_id", "name");
        if (requiresValue)
        {
            properties["value"] = StringSchema();
            required.Add("value");
        }
        return Definition(name, description, new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        });
    }

    private static JsonObject Definition(
        string name,
        string description,
        JsonObject inputSchema,
        JsonObject? annotations = null)
    {
        var definition = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema,
        };
        if (annotations is not null) definition["annotations"] = annotations;
        return definition;
    }

    private static JsonObject CommonProperties() => new()
    {
        ["name"] = StringSchema(),
        ["operation_id"] = StringSchema(),
        ["request_id"] = StringSchema(),
        ["capability"] = StringSchema(),
        ["caller_name"] = StringSchema(),
        ["caller_purpose"] = StringSchema(),
    };

    private static JsonObject ComponentSchema()
    {
        var properties = new JsonObject
        {
            ["name"] = StringSchema(),
            ["text"] = StringSchema(),
            ["file"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["upload_id"] = StringSchema(),
                    ["capability"] = StringSchema(),
                    ["digest"] = StringSchema(),
                },
                ["required"] = new JsonArray("upload_id", "capability", "digest"),
            },
            ["delivery"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["type"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("environment_variable", "temporary_file", "none"),
                    },
                    ["environment_variable"] = StringSchema(),
                },
                ["required"] = new JsonArray("type"),
            },
            ["masked"] = new JsonObject { ["type"] = "boolean" },
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties,
            ["required"] = new JsonArray("name", "delivery"),
            ["oneOf"] = new JsonArray
            {
                new JsonObject { ["required"] = new JsonArray("text") },
                new JsonObject { ["required"] = new JsonArray("file") },
            },
        };
    }

    private static JsonObject AppendSchema(bool minimumOffset = false)
    {
        var offset = new JsonObject { ["type"] = "integer" };
        if (minimumOffset) offset["minimum"] = 0;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["upload_id"] = StringSchema(),
                ["capability"] = StringSchema(),
                ["offset"] = offset,
                ["chunk_base64"] = StringSchema(),
            },
            ["required"] = new JsonArray("upload_id", "capability", "offset", "chunk_base64"),
        };
    }

    private static JsonObject UploadSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["upload_id"] = StringSchema(),
            ["capability"] = StringSchema(),
        },
        ["required"] = new JsonArray("upload_id", "capability"),
    };

    private static JsonObject RequestSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["request_id"] = StringSchema(),
            ["capability"] = StringSchema(),
        },
        ["required"] = new JsonArray("request_id", "capability"),
    };

    private static JsonObject StringSchema() => new() { ["type"] = "string" };
}
