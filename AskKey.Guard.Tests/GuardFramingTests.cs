using System.Buffers.Binary;
using System.Text.Json;

namespace AskKey.Guard.Tests;

public sealed class GuardFramingTests
{
    [Fact]
    public async Task LittleEndianLengthRoundTrips()
    {
        using var stream = new MemoryStream();
        var payload = "guard"u8.ToArray();

        Assert.True(await GuardFraming.WriteAsync(stream, payload));
        var bytes = stream.ToArray();
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, bytes[..4]);
        Assert.Equal(payload, bytes[4..]);

        stream.Position = 0;
        var frame = await GuardFraming.ReadAsync(stream, TimeSpan.FromSeconds(1));
        Assert.Equal(GuardFrameReadKind.Success, frame.Kind);
        Assert.Equal(payload, frame.Data);
    }

    [Fact]
    public async Task MaximumFrameIsAcceptedAndOversizedIsRejected()
    {
        var maximum = new byte[GuardProtocol.MaximumFrameBytes];
        Random.Shared.NextBytes(maximum);
        using var maxStream = new MemoryStream();
        Assert.True(await GuardFraming.WriteAsync(maxStream, maximum));
        maxStream.Position = 0;
        var accepted = await GuardFraming.ReadAsync(maxStream, TimeSpan.FromSeconds(1));
        Assert.Equal(GuardFrameReadKind.Success, accepted.Kind);
        Assert.Equal(maximum, accepted.Data);

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header, GuardProtocol.MaximumFrameBytes + 1);
        using var oversized = new MemoryStream(header);
        var rejected = await GuardFraming.ReadAsync(oversized, TimeSpan.FromSeconds(1));
        Assert.Equal(GuardFrameReadKind.TooLarge, rejected.Kind);
    }

    [Fact]
    public async Task ZeroLengthAndTruncatedFramesFailClosed()
    {
        using var zero = new MemoryStream(new byte[4]);
        Assert.Equal(
            GuardFrameReadKind.Incomplete,
            (await GuardFraming.ReadAsync(zero, TimeSpan.FromSeconds(1))).Kind);

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 10);
        using var truncated = new MemoryStream();
        truncated.Write(header);
        truncated.Write("abc"u8);
        truncated.Position = 0;
        Assert.Equal(
            GuardFrameReadKind.Incomplete,
            (await GuardFraming.ReadAsync(truncated, TimeSpan.FromSeconds(1))).Kind);
    }

    [Fact]
    public void JsonWireUsesContractNamesAndBase64()
    {
        var proof = new byte[32];
        proof[0] = 7;
        var request = new GuardRequest
        {
            Operation = GuardOperations.ConsumeEnroll,
            Nonce = new byte[32],
            Proof = proof,
        };

        var encoded = GuardJson.EncodeRequest(request);
        using var document = JsonDocument.Parse(encoded);
        Assert.Equal(GuardProtocol.Version, document.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("consume-enroll", document.RootElement.GetProperty("op").GetString());
        Assert.Equal(Convert.ToBase64String(proof), document.RootElement.GetProperty("proof").GetString());

        var decoded = GuardJson.DecodeRequest(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(request.Nonce, decoded.Nonce);
        Assert.Equal(request.Proof, decoded.Proof);
    }

    [Fact]
    public void MissingVersionIsRejected()
    {
        Assert.Null(GuardJson.DecodeRequest("""{"op":"status"}"""u8.ToArray()));
        Assert.Null(GuardJson.DecodeRequest("""{"v":99,"op":"status"}"""u8.ToArray()));
    }
}
