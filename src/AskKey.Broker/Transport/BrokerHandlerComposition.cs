using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;

namespace AskKey.Broker.Transport;

/// <summary>
/// Production composition: wires the real <see cref="BrokerTextRuntime"/> and
/// <see cref="BrokerFileWriteCoordinator"/> into the provider delegates. Approval
/// lookups stay on the shared state machine; unknown tickets fail closed as
/// <c>request_not_found</c>.
/// </summary>
public static class BrokerHandlerComposition
{
    public static BrokerRequestHandler Create(
        BrokerApprovalStateMachine approvals,
        BrokerTextRuntime? runtime = null,
        BrokerFileWriteCoordinator? fileWrite = null,
        BrokerRequestHandler.CatalogProvider? catalog = null,
        BrokerRequestHandler.CatalogGroupsProvider? catalogGroups = null)
        => new(
            catalog ?? (_ => Array.Empty<BrokerCatalogItem>()),
            requestStatus: (requestId, capability) => StatusOrNull(approvals, requestId, capability),
            catalogGroups: catalogGroups,
            cancelRequest: (requestId, capability) => CancelOrNull(approvals, requestId, capability),
            textRun: runtime is null ? null : runtime.Run,
            fileWrite: fileWrite is null ? null : fileWrite.Handle);

    private static BrokerRequestState? StatusOrNull(
        BrokerApprovalStateMachine approvals, string requestId, string capability)
    {
        try
        {
            return approvals.Status(requestId, capability);
        }
        catch (BrokerApprovalException)
        {
            return null;
        }
    }

    private static BrokerRequestState? CancelOrNull(
        BrokerApprovalStateMachine approvals, string requestId, string capability)
    {
        try
        {
            return approvals.Cancel(requestId, capability);
        }
        catch (BrokerApprovalException)
        {
            return null;
        }
    }
}
