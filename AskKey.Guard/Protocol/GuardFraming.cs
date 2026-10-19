using System.Buffers.Binary;

namespace AskKey.Guard;

public enum GuardFrameReadKind
{
    Success,
    End,
    TooLarge,
    Incomplete,
}

public readonly record struct GuardFrameRead(GuardFrameReadKind Kind, byte[]? Data)
{
    public static GuardFrameRead End => new(GuardFrameReadKind.End, null);

    public static GuardFrameRead TooLarge => new(GuardFrameReadKind.TooLarge, null);

    public static GuardFrameRead Incomplete => new(GuardFrameReadKind.Incomplete, null);
}

/// <summary>4-byte little-endian length prefix + UTF-8 JSON body, capped at 64 KiB.</summary>
public static class GuardFraming
{
    public static async Task<GuardFrameRead> ReadAsync(
        Stream stream,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        // One deadline for the whole frame: a peer cannot hold a pipe slot forever by
        // trickling one byte before each per-read timeout.
        using var deadline = timeout is { } limit ? new CancellationTokenSource(limit) : null;
        using var linked = deadline is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked?.Token ?? cancellationToken;

        var header = new byte[4];
        var status = await ReadExactAsync(
            stream, header, token, allowCleanEnd: true).ConfigureAwait(false);
        if (status != GuardFrameReadKind.Success)
        {
            return status == GuardFrameReadKind.End ? GuardFrameRead.End : GuardFrameRead.Incomplete;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0) return GuardFrameRead.Incomplete;
        if (length > GuardProtocol.MaximumFrameBytes) return GuardFrameRead.TooLarge;

        var body = new byte[length];
        status = await ReadExactAsync(
            stream, body, token, allowCleanEnd: false).ConfigureAwait(false);
        if (status != GuardFrameReadKind.Success)
        {
            Array.Clear(body);
            return GuardFrameRead.Incomplete;
        }
        return new GuardFrameRead(GuardFrameReadKind.Success, body);
    }

    public static async Task<bool> WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (data.Length > GuardProtocol.MaximumFrameBytes)
        {
            throw new ArgumentException("frame exceeds the guard limit", nameof(data));
        }

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)data.Length);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static async Task<GuardFrameReadKind> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken,
        bool allowCleanEnd)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            int received;
            try
            {
                received = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return GuardFrameReadKind.Incomplete;
            }
            catch (IOException)
            {
                return GuardFrameReadKind.Incomplete;
            }
            catch (ObjectDisposedException)
            {
                return GuardFrameReadKind.Incomplete;
            }

            if (received == 0)
            {
                return offset == 0 && allowCleanEnd
                    ? GuardFrameReadKind.End
                    : GuardFrameReadKind.Incomplete;
            }
            offset += received;
        }
        return GuardFrameReadKind.Success;
    }
}
