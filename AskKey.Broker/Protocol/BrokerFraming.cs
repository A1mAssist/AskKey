namespace AskKey.Broker.Protocol;

public enum BrokerFrameFailure
{
    TooLarge,
    IncompleteOrTimedOut,
}

public readonly struct BrokerFrameRead
{
    public enum Kind
    {
        Success,
        Failure,
        End,
    }

    public Kind Result { get; }
    public byte[]? Data { get; }
    public BrokerFrameFailure Failure { get; }

    private BrokerFrameRead(Kind result, byte[]? data, BrokerFrameFailure failure)
    {
        Result = result;
        Data = data;
        Failure = failure;
    }

    public static BrokerFrameRead Success(byte[] data) => new(Kind.Success, data, default);

    public static BrokerFrameRead Failed(BrokerFrameFailure failure) => new(Kind.Failure, null, failure);

    public static BrokerFrameRead End { get; } = new(Kind.End, null, default);
}

public enum BrokerClearedReadFailure
{
    Deadline,
    EndOfFile,
    PollError,
    ReadError,
}

/// <summary>Partial frame bytes, zeroed before the failure is reported, exactly as Swift clears them.</summary>
public sealed class BrokerClearedReadBuffer
{
    public required byte[] Buffer { get; init; }
    public required int BytesRead { get; init; }
    public required BrokerClearedReadFailure Failure { get; init; }
}

/// <summary>4-byte big-endian length prefix + JSON body, capped at 64 KiB.</summary>
public static class BrokerFraming
{
    public static async Task<BrokerFrameRead> ReadFrameAsync(
        Stream stream,
        int maximumBytes,
        TimeSpan? deadline,
        Action<BrokerClearedReadBuffer>? cleared = null,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        // Only the header read may treat EOF at offset 0 as a clean connection end.
        // Once a header is consumed, EOF while reading the body is a failed frame.
        var headerRead = await ReadExactAsync(
            stream, header, 0, deadline, cleared, cancellationToken, allowCleanEnd: true).ConfigureAwait(false);
        if (headerRead.Result != BrokerFrameRead.Kind.Success) return headerRead;

        var length = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
        if (length > (uint)maximumBytes) return BrokerFrameRead.Failed(BrokerFrameFailure.TooLarge);
        if (length == 0) return BrokerFrameRead.Failed(BrokerFrameFailure.IncompleteOrTimedOut);

        var body = new byte[length];
        return await ReadExactAsync(
            stream, body, 0, deadline, cleared, cancellationToken, allowCleanEnd: false).ConfigureAwait(false);
    }

    public static async Task<bool> WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var length = (uint)data.Length;
        header[0] = (byte)(length >> 24);
        header[1] = (byte)(length >> 16);
        header[2] = (byte)(length >> 8);
        header[3] = (byte)length;
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

    private static async Task<BrokerFrameRead> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        int offset,
        TimeSpan? deadline,
        Action<BrokerClearedReadBuffer>? cleared,
        CancellationToken cancellationToken,
        bool allowCleanEnd)
    {
        while (offset < buffer.Length)
        {
            int received;
            try
            {
                received = await ReadOnceAsync(stream, buffer, offset, deadline, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return Clear(buffer, offset, BrokerClearedReadFailure.Deadline, cleared);
            }
            catch (IOException)
            {
                return Clear(buffer, offset, BrokerClearedReadFailure.ReadError, cleared);
            }
            catch (ObjectDisposedException)
            {
                return Clear(buffer, offset, BrokerClearedReadFailure.ReadError, cleared);
            }

            if (received == 0)
            {
                if (offset == 0 && allowCleanEnd) return BrokerFrameRead.End;
                return Clear(buffer, offset, BrokerClearedReadFailure.EndOfFile, cleared);
            }
            offset += received;
        }
        return BrokerFrameRead.Success(buffer);
    }

    private static async Task<int> ReadOnceAsync(
        Stream stream,
        byte[] buffer,
        int offset,
        TimeSpan? deadline,
        CancellationToken cancellationToken)
    {
        if (deadline is not { } limit)
        {
            return await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
        }
        // Cancelling a pending pipe read aborts the connection and makes the
        // already-received header impossible to answer. Wait instead, and abandon
        // the read only because the caller closes the stream right after.
        var read = stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).AsTask();
        var finished = await Task.WhenAny(read, Task.Delay(limit, cancellationToken)).ConfigureAwait(false);
        if (finished == read) return await read.ConfigureAwait(false);
        _ = read.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        throw new TimeoutException();
    }

    private static BrokerFrameRead Clear(
        byte[] buffer,
        int bytesRead,
        BrokerClearedReadFailure failure,
        Action<BrokerClearedReadBuffer>? cleared)
    {
        // Swift zeroes and reports the whole frame buffer even when no body byte
        // arrived, so a consumed header with an immediate EOF is observable.
        global::System.Array.Clear(buffer);
        cleared?.Invoke(new BrokerClearedReadBuffer
        {
            Buffer = buffer,
            BytesRead = bytesRead,
            Failure = failure,
        });
        return BrokerFrameRead.Failed(BrokerFrameFailure.IncompleteOrTimedOut);
    }
}

