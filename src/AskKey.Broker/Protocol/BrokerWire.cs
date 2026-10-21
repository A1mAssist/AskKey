using AskKey.Broker.Approval;

namespace AskKey.Broker.Protocol;

/// <summary>Swift raw-value projections for enums that are shared with the Approval namespace.</summary>
public static class BrokerWire
{
    public static string ToWire(this BrokerErrorCode code) => code switch
    {
        BrokerErrorCode.UnsupportedVersion => "unsupported_version",
        BrokerErrorCode.MethodNotAllowed => "method_not_allowed",
        BrokerErrorCode.InvalidRequest => "invalid_request",
        BrokerErrorCode.RequestNotFound => "request_not_found",
        BrokerErrorCode.ResourceExhausted => "resource_exhausted",
        BrokerErrorCode.DeadlineExceeded => "deadline_exceeded",
        BrokerErrorCode.ResponseTooLarge => "response_too_large",
        BrokerErrorCode.InternalError => "internal_error",
        BrokerErrorCode.AgentAccessPaused => "agent_access_paused",
        BrokerErrorCode.RequestRejected => "request_rejected",
        BrokerErrorCode.VaultLocked => "vault_locked",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    public static bool TryParseErrorCode(string? raw, out BrokerErrorCode code)
    {
        switch (raw)
        {
            case "unsupported_version": code = BrokerErrorCode.UnsupportedVersion; return true;
            case "method_not_allowed": code = BrokerErrorCode.MethodNotAllowed; return true;
            case "invalid_request": code = BrokerErrorCode.InvalidRequest; return true;
            case "request_not_found": code = BrokerErrorCode.RequestNotFound; return true;
            case "resource_exhausted": code = BrokerErrorCode.ResourceExhausted; return true;
            case "deadline_exceeded": code = BrokerErrorCode.DeadlineExceeded; return true;
            case "response_too_large": code = BrokerErrorCode.ResponseTooLarge; return true;
            case "internal_error": code = BrokerErrorCode.InternalError; return true;
            case "agent_access_paused": code = BrokerErrorCode.AgentAccessPaused; return true;
            case "request_rejected": code = BrokerErrorCode.RequestRejected; return true;
            case "vault_locked": code = BrokerErrorCode.VaultLocked; return true;
            default: code = default; return false;
        }
    }

    public static string ToWire(this BrokerRequestState state) => state switch
    {
        BrokerRequestState.Pending => "pending",
        BrokerRequestState.Approved => "approved",
        BrokerRequestState.Denied => "denied",
        BrokerRequestState.Cancelled => "cancelled",
        BrokerRequestState.Expired => "expired",
        BrokerRequestState.Completed => "completed",
        BrokerRequestState.Consumed => "consumed",
        BrokerRequestState.OutcomeUnknown => "outcome_unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static bool TryParseRequestState(string? raw, out BrokerRequestState state)
    {
        switch (raw)
        {
            case "pending": state = BrokerRequestState.Pending; return true;
            case "approved": state = BrokerRequestState.Approved; return true;
            case "denied": state = BrokerRequestState.Denied; return true;
            case "cancelled": state = BrokerRequestState.Cancelled; return true;
            case "expired": state = BrokerRequestState.Expired; return true;
            case "completed": state = BrokerRequestState.Completed; return true;
            case "consumed": state = BrokerRequestState.Consumed; return true;
            case "outcome_unknown": state = BrokerRequestState.OutcomeUnknown; return true;
            default: state = default; return false;
        }
    }

    public static string ToWire(this BrokerApprovalOperation operation) => operation switch
    {
        BrokerApprovalOperation.Read => "read",
        BrokerApprovalOperation.Create => "create",
        BrokerApprovalOperation.Modify => "modify",
        BrokerApprovalOperation.Delete => "delete",
        BrokerApprovalOperation.Organize => "organize",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    public static bool TryParseApprovalOperation(string? raw, out BrokerApprovalOperation operation)
    {
        switch (raw)
        {
            case "read": operation = BrokerApprovalOperation.Read; return true;
            case "create": operation = BrokerApprovalOperation.Create; return true;
            case "modify": operation = BrokerApprovalOperation.Modify; return true;
            case "delete": operation = BrokerApprovalOperation.Delete; return true;
            case "organize": operation = BrokerApprovalOperation.Organize; return true;
            default: operation = default; return false;
        }
    }

    public static string ToWire(this BrokerCatalogPayloadKind kind) => kind switch
    {
        BrokerCatalogPayloadKind.Text => "text",
        BrokerCatalogPayloadKind.File => "file",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryParsePayloadKind(string? raw, out BrokerCatalogPayloadKind kind)
    {
        switch (raw)
        {
            case "text": kind = BrokerCatalogPayloadKind.Text; return true;
            case "file": kind = BrokerCatalogPayloadKind.File; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>UTF-8 byte count, matching Swift's <c>value.utf8.count</c> field limits.</summary>
    public static int Utf8Length(string value) => global::System.Text.Encoding.UTF8.GetByteCount(value);
}
