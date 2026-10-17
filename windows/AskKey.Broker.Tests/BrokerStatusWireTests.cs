using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Broker.Tests;

public sealed class BrokerStatusWireTests
{
    [Fact]
    public void RequestStatusUsesSwiftAssociatedValueShapeAndRoundTrips()
    {
        var response = BrokerResponse.Success(
            BrokerPayload.FromRequestStatus(BrokerRequestState.Pending));
        var encoded = global::System.Text.Encoding.UTF8.GetString(BrokerJson.Encode(response));
        Assert.Equal("{\"success\":{\"_0\":{\"requestStatus\":{\"_0\":\"pending\"}}}}", encoded);
        Assert.Equal(response, BrokerJson.DecodeResponse(BrokerJson.Encode(response)));
    }
}
