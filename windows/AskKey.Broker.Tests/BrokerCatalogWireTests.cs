using System.Text;
using System.Text.Json;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Broker.Tests;

public sealed class BrokerCatalogWireTests
{
    [Fact]
    public void CatalogDeliveryUsesSwiftAssociatedValueShapeAndRoundTrips()
    {
        var item = new BrokerCatalogItem
        {
            CredentialId = "credential-id",
            Name = "Service",
            PayloadKind = BrokerCatalogPayloadKind.Text,
            UsageInstructions = "Use staging",
            EnvironmentVariable = "TOKEN",
            Expired = false,
            Group = "Production",
            Components = new[]
            {
                new BrokerCatalogComponent
                {
                    Name = "TOKEN",
                    PayloadKind = BrokerCatalogPayloadKind.Text,
                    Delivery = BrokerComponentDelivery.EnvironmentVariable("TOKEN"),
                },
                new BrokerCatalogComponent
                {
                    Name = "KEY_FILE",
                    PayloadKind = BrokerCatalogPayloadKind.File,
                    Delivery = BrokerComponentDelivery.TemporaryFile("KEY_FILE"),
                },
                new BrokerCatalogComponent
                {
                    Name = "NOTE",
                    PayloadKind = BrokerCatalogPayloadKind.Text,
                    Delivery = BrokerComponentDelivery.None,
                },
            },
        };
        var response = BrokerResponse.Success(
            BrokerPayload.FromCatalog(new[] { item }, new[] { "Production" }));

        var encoded = BrokerJson.Encode(response);
        var json = Encoding.UTF8.GetString(encoded);
        Assert.Contains("\"environmentVariable\":{\"_0\":\"TOKEN\"}", json);
        Assert.Contains("\"temporaryFile\":{\"_0\":\"KEY_FILE\"}", json);
        Assert.Contains("\"none\":{}", json);
        Assert.Equal(response, BrokerJson.DecodeResponse(encoded));
    }

    [Fact]
    public void TextWriteAssociatedValuesUseSwiftShapeAndRoundTrip()
    {
        var action = AgentTextWriteAction.ModifyBundle(
            "Service",
            new[]
            {
                BrokerCredentialComponentChange.UpsertComponent(new BrokerCredentialComponentInput
                {
                    Name = "TOKEN",
                    Value = BrokerCredentialComponentValue.FromText("synthetic"),
                    Delivery = BrokerComponentDelivery.EnvironmentVariable("TOKEN"),
                }),
                BrokerCredentialComponentChange.Remove("OLD"),
            },
            usageInstructions: "Use staging",
            group: BrokerCredentialGroupChange.Named("Production"));

        var encoded = BrokerJson.EncodeTextWriteAction(action);
        var json = encoded.ToJsonString();
        Assert.Contains("\"text\":{\"_0\":\"synthetic\"}", json);
        Assert.Contains("\"remove\":{\"_0\":\"OLD\"}", json);
        Assert.Contains("\"named\":{\"_0\":\"Production\"}", json);

        using var document = JsonDocument.Parse(json);
        var decoded = BrokerJson.DecodeTextWriteAction(document.RootElement);
        Assert.Equal(AgentTextWriteActionKind.ModifyBundle, decoded.ActionKind);
        Assert.Equal("Service", decoded.Name);
        Assert.Equal("Use staging", decoded.UsageInstructions);
        Assert.Equal("Production", decoded.GroupChange?.Name);
        Assert.Equal("TOKEN", decoded.Changes?[0].Upsert?.Delivery.Name);
        Assert.Equal("synthetic", decoded.Changes?[0].Upsert?.Value.Text);
        Assert.Equal("OLD", decoded.Changes?[1].RemoveName);
    }

    [Fact]
    public void OrganizationAssociatedValuesUseSwiftShapeAndRoundTrip()
    {
        var action = AgentTextWriteAction.Organize(new[]
        {
            BrokerOrganizationOperation.CreateGroup("New"),
            BrokerOrganizationOperation.Move("Service", "New"),
            BrokerOrganizationOperation.RenameGroup("Old", "Renamed"),
            BrokerOrganizationOperation.DeleteGroup("Renamed"),
        });
        var json = BrokerJson.EncodeTextWriteAction(action).ToJsonString();
        Assert.Contains("\"createGroup\":{\"_0\":\"New\"}", json);
        Assert.Contains("\"deleteGroup\":{\"_0\":\"Renamed\"}", json);

        using var document = JsonDocument.Parse(json);
        var decoded = BrokerJson.DecodeTextWriteAction(document.RootElement);
        var operations = Assert.IsAssignableFrom<IReadOnlyList<BrokerOrganizationOperation>>(
            decoded.OrganizationOperations);
        Assert.Equal(BrokerOrganizationOperation.Kind.CreateGroup, operations[0].OperationKind);
        Assert.Equal("New", operations[0].Name);
        Assert.Equal(BrokerOrganizationOperation.Kind.Move, operations[1].OperationKind);
        Assert.Equal("Service", operations[1].Credential);
        Assert.Equal("New", operations[1].Group);
        Assert.Equal(BrokerOrganizationOperation.Kind.RenameGroup, operations[2].OperationKind);
        Assert.Equal("Old", operations[2].From);
        Assert.Equal("Renamed", operations[2].To);
        Assert.Equal(BrokerOrganizationOperation.Kind.DeleteGroup, operations[3].OperationKind);
        Assert.Equal("Renamed", operations[3].Name);
    }
}
