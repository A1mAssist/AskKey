using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;

namespace AskKey.Broker.Transport;

/// <summary>
/// Provider surface for one Broker. Every delegate may throw the ported approval
/// errors; <see cref="BrokerRequestHandler"/> maps them to wire codes.
/// </summary>
public sealed class BrokerRequestHandler
{
    public delegate IReadOnlyList<BrokerCatalogItem> CatalogProvider(BrokerCancellation cancellation);

    public delegate IReadOnlyList<string> CatalogGroupsProvider(BrokerCancellation cancellation);

    public delegate BrokerRequestState? RequestStatusProvider(string requestId, string capability);

    public delegate BrokerRequestState? RequestCancellationProvider(string requestId, string capability);

    public delegate BrokerTextRunResult TextRunProvider(
        BrokerTextRunRequest request, BrokerRuntimeChannel descriptors, BrokerCancellation cancellation);

    public delegate BrokerFileWritePayload FileWriteProvider(BrokerFileWriteRequest request);

    public delegate AgentTextWriteRequestOutcome TextWriteSubmissionProvider(
        AgentTextWriteRequest request, BrokerCancellation cancellation);

    public delegate AgentTextWriteResult TextWriteCommitProvider(
        AgentTextWriteRequest request, string requestId, string capability);

    public delegate BrokerRequestState TextWriteCancellationProvider(
        string operationId, string requestId, string capability);

    private readonly CatalogProvider _catalog;
    private readonly CatalogGroupsProvider _catalogGroups;
    private readonly RequestStatusProvider _requestStatus;
    private readonly RequestCancellationProvider _cancelRequest;
    private readonly TextRunProvider? _textRun;
    private readonly FileWriteProvider? _fileWrite;
    private readonly TextWriteSubmissionProvider? _submitTextWrite;
    private readonly TextWriteCommitProvider? _commitTextWrite;
    private readonly TextWriteCancellationProvider? _cancelTextWrite;

    public BrokerRequestHandler(
        CatalogProvider catalog,
        RequestStatusProvider requestStatus,
        CatalogGroupsProvider? catalogGroups = null,
        RequestCancellationProvider? cancelRequest = null,
        TextRunProvider? textRun = null,
        FileWriteProvider? fileWrite = null,
        TextWriteSubmissionProvider? submitTextWrite = null,
        TextWriteCommitProvider? commitTextWrite = null,
        TextWriteCancellationProvider? cancelTextWrite = null)
    {
        _catalog = catalog;
        _catalogGroups = catalogGroups ?? (_ => Array.Empty<string>());
        _requestStatus = requestStatus;
        _cancelRequest = cancelRequest ?? ((_, _) => null);
        _textRun = textRun;
        _fileWrite = fileWrite;
        _submitTextWrite = submitTextWrite;
        _commitTextWrite = commitTextWrite;
        _cancelTextWrite = cancelTextWrite;
    }

    /// <summary>Validation order is the Swift order; do not reorder the guards.</summary>
    public BrokerResponse Handle(
        BrokerRequest request,
        BrokerRuntimeChannel? descriptors = null,
        BrokerCancellation? cancellation = null)
    {
        cancellation ??= new BrokerCancellation();
        if (request.Version != BrokerProtocolVersion.Current)
        {
            return BrokerResponse.Failure(BrokerErrorCode.UnsupportedVersion);
        }
        if (!FieldsFit(request)) return BrokerResponse.Failure(BrokerErrorCode.InvalidRequest);
        if (!((request.Method == "runtime.run" || request.TextRun is null)
            && (request.Method == "credential.file.write" || request.FileWrite is null)
            && (request.Method.StartsWith("credential.write.", StringComparison.Ordinal) || request.TextWrite is null)
            && (request.Method == "credential.write.cancel" || request.OperationId is null)))
        {
            return BrokerResponse.Failure(BrokerErrorCode.InvalidRequest);
        }

        try
        {
            switch (request.Method)
            {
                case "health":
                    return BrokerResponse.Success(BrokerPayload.FromHealth(
                        new BrokerHealth(BrokerProtocolVersion.Current, "ok")));
                case "version":
                    return BrokerResponse.Success(BrokerPayload.FromVersion(
                        new BrokerVersion(BrokerProtocolVersion.Current)));
                case "catalog":
                    cancellation.Check();
                    var items = _catalog(cancellation);
                    var groups = _catalogGroups(cancellation);
                    cancellation.Check();
                    if (!items.All(CatalogFieldsFit)
                        || !groups.All(group => BrokerWire.Utf8Length(group) <= BrokerLimits.MaximumFieldBytes))
                    {
                        return BrokerResponse.Failure(BrokerErrorCode.InternalError);
                    }
                    return BrokerResponse.Success(BrokerPayload.FromCatalog(items, groups));
                case "request.status":
                    if (request.RequestId is null || request.Capability is null)
                    {
                        return BrokerResponse.Failure(BrokerErrorCode.InvalidRequest);
                    }
                    var status = _requestStatus(request.RequestId, request.Capability);
                    return status is null
                        ? BrokerResponse.Failure(BrokerErrorCode.RequestNotFound)
                        : BrokerResponse.Success(BrokerPayload.FromRequestStatus(status.Value));
                case "request.cancel":
                    if (request.RequestId is null || request.Capability is null)
                    {
                        return BrokerResponse.Failure(BrokerErrorCode.InvalidRequest);
                    }
                    var cancelled = _cancelRequest(request.RequestId, request.Capability);
                    return cancelled is null
                        ? BrokerResponse.Failure(BrokerErrorCode.RequestNotFound)
                        : BrokerResponse.Success(BrokerPayload.FromRequestStatus(cancelled.Value));
                case "runtime.run":
                    if (_textRun is null || request.TextRun is null || descriptors is null)
                    {
                        return BrokerResponse.Failure(BrokerErrorCode.InvalidRequest);
                    }
                    return BrokerResponse.Success(BrokerPayload.FromTextRun(
                        _textRun(request.TextRun, descriptors, cancellation)));
                case "credential.file.write":
                    if (_fileWrite is null || request.FileWrite is null
                        || request.RequestId is not null || request.Capability is not null
                        || request.TextRun is not null)
                    {
                        return BrokerResponse.Failure(BrokerErrorCode.InvalidRequest);
                    }
                    return BrokerResponse.Success(BrokerPayload.FromFileWrite(_fileWrite(request.FileWrite)));
                case "credential.write.request":
                    if (request.TextWrite is null || _submitTextWrite is null)
                    {
                        return BrokerResponse.Failure(BrokerErrorCode.InvalidRequest);
                    }
                    cancellation.Check();
                    var outcome = _submitTextWrite(request.TextWrite, cancellation);
                    try
                    {
                        cancellation.Check();
                    }
                    catch
                    {
                        if (outcome.Submission is { } submission)
                        {
                            CancelSubmittedTextWrite(request.TextWrite.OperationId, submission);
                        }
                        throw;
                    }
                    if (request.TextWrite.Action.ActionKind == AgentTextWriteActionKind.Organize
                        && outcome.CompletedResult is { } completed)
                    {
                        return BrokerResponse.Success(BrokerPayload.FromOrganizationWriteResult(completed.OperationId));
                    }
                    return BrokerResponse.Success(BrokerPayload.FromTextWriteRequest(outcome));
                case "credential.write.commit":
                    if (request.TextWrite is null || request.RequestId is null
                        || request.Capability is null || _commitTextWrite is null)
                    {
                        return BrokerResponse.Failure(BrokerErrorCode.InvalidRequest);
                    }
                    var result = _commitTextWrite(request.TextWrite, request.RequestId, request.Capability);
                    if (request.TextWrite.Action.ActionKind == AgentTextWriteActionKind.Organize)
                    {
                        return BrokerResponse.Success(BrokerPayload.FromOrganizationWriteResult(result.OperationId));
                    }
                    return BrokerResponse.Success(BrokerPayload.FromTextWriteResult(result));
                case "credential.write.cancel":
                    if (request.OperationId is null || request.RequestId is null
                        || request.Capability is null || _cancelTextWrite is null)
                    {
                        return BrokerResponse.Failure(BrokerErrorCode.InvalidRequest);
                    }
                    return BrokerResponse.Success(BrokerPayload.FromRequestStatus(
                        _cancelTextWrite(request.OperationId, request.RequestId, request.Capability)));
                default:
                    return BrokerResponse.Failure(BrokerErrorCode.MethodNotAllowed);
            }
        }
        catch (BrokerProviderException error)
        {
            return BrokerResponse.Failure(error.Error switch
            {
                BrokerProviderError.AgentAccessPaused => BrokerErrorCode.AgentAccessPaused,
                BrokerProviderError.RequestRejected => BrokerErrorCode.RequestRejected,
                BrokerProviderError.InvalidRequest => BrokerErrorCode.InvalidRequest,
                BrokerProviderError.RequestNotFound => BrokerErrorCode.RequestNotFound,
                _ => BrokerErrorCode.ResourceExhausted,
            });
        }
        catch (BrokerTextRuntimeException error)
        {
            return BrokerResponse.Failure(error.Error == BrokerTextRuntimeError.SpawnFailed
                ? BrokerErrorCode.InternalError
                : BrokerErrorCode.InvalidRequest);
        }
        catch (BrokerFileWriteException error)
        {
            return BrokerResponse.Failure(error.Error switch
            {
                BrokerFileWriteError.InvalidRequest
                    or BrokerFileWriteError.OutOfOrderChunk
                    or BrokerFileWriteError.TruncatedUpload
                    or BrokerFileWriteError.AlreadyFrozen => BrokerErrorCode.InvalidRequest,
                BrokerFileWriteError.InvalidCapability
                    or BrokerFileWriteError.RequestNotFound => BrokerErrorCode.RequestNotFound,
                BrokerFileWriteError.TooLarge
                    or BrokerFileWriteError.CapacityReached => BrokerErrorCode.ResourceExhausted,
                BrokerFileWriteError.DigestMismatch
                    or BrokerFileWriteError.TargetChanged
                    or BrokerFileWriteError.AuthenticationFailed => BrokerErrorCode.RequestRejected,
                BrokerFileWriteError.OutcomeUnknown => BrokerErrorCode.InternalError,
                _ => BrokerErrorCode.InternalError,
            });
        }
        catch (BrokerApprovalException error)
        {
            return BrokerResponse.Failure(error.Error switch
            {
                BrokerApprovalError.RequestNotFound => BrokerErrorCode.RequestNotFound,
                BrokerApprovalError.PayloadMismatch => BrokerErrorCode.RequestRejected,
                BrokerApprovalError.AgentAccessPaused => BrokerErrorCode.AgentAccessPaused,
                BrokerApprovalError.CapacityReached => BrokerErrorCode.ResourceExhausted,
                _ => BrokerErrorCode.InternalError,
            });
        }
        catch (BrokerCancellationException)
        {
            return BrokerResponse.Failure(BrokerErrorCode.DeadlineExceeded);
        }
        catch (Exception)
        {
            return BrokerResponse.Failure(BrokerErrorCode.InternalError);
        }
    }

    /// <summary>Called when a write submission response could not be delivered.</summary>
    public void CancelUndelivered(BrokerRequest request, BrokerResponse response)
    {
        if (request.Method != "credential.write.request" || request.TextWrite is null) return;
        if (response.Payload?.PayloadKind != BrokerPayload.Kind.TextWriteRequest) return;
        if (response.Payload.TextWriteRequest?.Submission is not { } submission) return;
        CancelSubmittedTextWrite(request.TextWrite.OperationId, submission);
    }

    private void CancelSubmittedTextWrite(string operationId, AgentTextWriteSubmission submission)
    {
        if (_cancelTextWrite is null) return;
        try
        {
            _ = _cancelTextWrite(operationId, submission.RequestId, submission.Capability);
        }
        catch (Exception)
        {
            // Best effort: the submission is already undelivered and must not mask it.
        }
    }

    private static bool FieldsFit(BrokerRequest request)
    {
        var envelope = new[] { request.Method, request.RequestId, request.Capability, request.OperationId }
            .Where(value => value is not null)
            .All(value => BrokerWire.Utf8Length(value!) <= BrokerLimits.MaximumFieldBytes);
        if (!envelope || request.TextWrite is null) return envelope;
        return request.TextWrite.IsBounded;
    }

    private static bool CatalogFieldsFit(BrokerCatalogItem item)
    {
        var fields = new[] { item.CredentialId, item.Name, item.UsageInstructions, item.EnvironmentVariable, item.Group }
            .Concat((item.Components ?? Array.Empty<BrokerCatalogComponent>())
                .SelectMany(component => new[] { component.Name, component.Delivery.EnvironmentVariableName }));
        return (item.Components?.Count ?? 0) <= 64
            && fields.Where(value => value is not null)
                .All(value => BrokerWire.Utf8Length(value!) <= BrokerLimits.MaximumFieldBytes);
    }
}


