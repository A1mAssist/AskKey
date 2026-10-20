namespace AskKey.Helper;

internal static class JsonLineFraming
{
    internal enum Kind
    {
        Frame,
        TooLarge,
        End,
        Failure,
    }

    internal readonly record struct Result(Kind Kind, byte[]? Data);

    public static Result ReadFrame(Stream input, int maximumBytes)
    {
        using var frame = new MemoryStream();
        var oversized = false;
        while (true)
        {
            int value;
            try { value = input.ReadByte(); }
            catch (IOException) { return new Result(Kind.Failure, null); }
            catch (ObjectDisposedException) { return new Result(Kind.Failure, null); }
            if (value < 0)
            {
                if (oversized) return new Result(Kind.TooLarge, null);
                return frame.Length == 0
                    ? new Result(Kind.End, null)
                    : new Result(Kind.Frame, frame.ToArray());
            }
            if (value == '\n')
            {
                return oversized
                    ? new Result(Kind.TooLarge, null)
                    : new Result(Kind.Frame, frame.ToArray());
            }
            if (frame.Length >= maximumBytes)
            {
                oversized = true;
            }
            else if (!oversized)
            {
                frame.WriteByte((byte)value);
            }
        }
    }

    public static byte[]? ReadToEnd(Stream input, int maximumBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            int count;
            while ((count = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + count > maximumBytes) return null;
                buffer.Write(chunk, 0, count);
            }
        }
        catch (IOException) { return null; }
        catch (ObjectDisposedException) { return null; }
        return buffer.ToArray();
    }
}
