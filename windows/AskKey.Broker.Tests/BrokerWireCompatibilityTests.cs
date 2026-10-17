using System.Text;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Broker.Tests;

public sealed class BrokerWireCompatibilityTests
{
    [Fact]
    public void PayloadCasesUseSwiftAssociatedValueEnvelopes()
    {
        var health = BrokerResponse.Success(
            BrokerPayload.FromHealth(new BrokerHealth(1, "ok")));
        Assert.Equal(
            "{\"success\":{\"_0\":{\"health\":{\"_0\":{\"version\":1,\"status\":\"ok\"}}}}}",
            Text(health));
        Assert.Equal(health, BrokerJson.DecodeResponse(BrokerJson.Encode(health)));

        var version = BrokerResponse.Success(
            BrokerPayload.FromVersion(new BrokerVersion(1)));
        Assert.Equal(
            "{\"success\":{\"_0\":{\"version\":{\"_0\":{\"protocolVersion\":1}}}}}",
            Text(version));
        Assert.Equal(version, BrokerJson.DecodeResponse(BrokerJson.Encode(version)));

        var textRun = BrokerResponse.Success(
            BrokerPayload.FromTextRun(BrokerTextRunResult.Exited(7)));
        Assert.Equal(
            "{\"success\":{\"_0\":{\"textRun\":{\"_0\":{\"exited\":{\"_0\":7}}}}}}",
            Text(textRun));
        Assert.Equal(textRun, BrokerJson.DecodeResponse(BrokerJson.Encode(textRun)));

        var fileWrite = BrokerResponse.Success(
            BrokerPayload.FromFileWrite(BrokerFileWritePayload.ChunkAccepted(3)));
        Assert.Equal(
            "{\"success\":{\"_0\":{\"fileWrite\":{\"_0\":{\"chunkAccepted\":{\"nextOffset\":3}}}}}}",
            Text(fileWrite));
        Assert.Equal(fileWrite, BrokerJson.DecodeResponse(BrokerJson.Encode(fileWrite)));

        var writeRequest = BrokerResponse.Success(BrokerPayload.FromTextWriteRequest(
            AgentTextWriteRequestOutcome.Submitted(new AgentTextWriteSubmission(
                "op", "req", "cap", BrokerRequestState.Pending, 0))));
        Assert.Equal(
            "{\"success\":{\"_0\":{\"textWriteRequest\":{\"_0\":{\"submitted\":{\"_0\":{\"operationID\":\"op\",\"requestID\":\"req\",\"capability\":\"cap\",\"state\":\"pending\",\"retryCount\":0}}}}}}}",
            Text(writeRequest));
        Assert.Equal(writeRequest, BrokerJson.DecodeResponse(BrokerJson.Encode(writeRequest)));

        var writeResult = BrokerResponse.Success(BrokerPayload.FromTextWriteResult(
            new AgentTextWriteResult("op", "credential-id")));
        Assert.Equal(
            "{\"success\":{\"_0\":{\"textWriteResult\":{\"_0\":{\"operationID\":\"op\",\"credentialID\":\"credential-id\",\"state\":\"completed\"}}}}}",
            Text(writeResult));
        Assert.Equal(writeResult, BrokerJson.DecodeResponse(BrokerJson.Encode(writeResult)));
    }

    [Fact]
    public void TextWriteDeleteUsesItsLabelAndBundleOptionalsAreOmitted()
    {
        Assert.Equal(
            "{\"delete\":{\"name\":\"Disposable\"}}",
            BrokerJson.EncodeTextWriteAction(AgentTextWriteAction.Delete("Disposable")).ToJsonString());

        var create = BrokerJson.EncodeTextWriteAction(
            AgentTextWriteAction.CreateBundle("Service", Array.Empty<BrokerCredentialComponentInput>()));
        Assert.False(create.ContainsKey("usageInstructions"));
        Assert.False(create.ContainsKey("group"));

        var modify = BrokerJson.EncodeTextWriteAction(
            AgentTextWriteAction.ModifyBundle("Service"));
        Assert.False(modify.ContainsKey("usageInstructions"));
        Assert.False(modify.ContainsKey("group"));
    }

    [Fact]
    public void OrganizationMoveOmitsNilGroup()
    {
        Assert.Equal(
            "{\"move\":{\"credential\":\"Service\"}}",
            BrokerJson.EncodeOrganizationOperation(
                BrokerOrganizationOperation.Move("Service", null)).ToJsonString());
        Assert.Equal(
            "{\"move\":{\"credential\":\"Service\",\"group\":\"Production\"}}",
            BrokerJson.EncodeOrganizationOperation(
                BrokerOrganizationOperation.Move("Service", "Production")).ToJsonString());
    }

    [Fact]
    public void CatalogItemKeepsSwiftExplicitNullGroupAndOmitsNilOptionalFields()
    {
        var json = BrokerJson.EncodeCatalogItem(new BrokerCatalogItem
        {
            Name = "Service",
            PayloadKind = BrokerCatalogPayloadKind.Text,
            UsageInstructions = "",
            Expired = false,
        }).ToJsonString();

        Assert.Contains("\"group\":null", json);
        Assert.DoesNotContain("\"credentialID\"", json);
        Assert.DoesNotContain("\"components\"", json);
        Assert.DoesNotContain("\"environmentVariable\"", json);
    }

    private static string Text(BrokerResponse response)
        => Encoding.UTF8.GetString(BrokerJson.Encode(response));
}
