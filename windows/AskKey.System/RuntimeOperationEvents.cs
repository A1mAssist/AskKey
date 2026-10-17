namespace AskKey.System;

public enum RuntimeOperation
{
    Cli,
    Keychain,
    ConfigWrite,
    CursorHelper,
}

/// <summary>Optional observation of runtime I/O boundaries; ordinary executables install no consumer.</summary>
public static class RuntimeOperationEvents
{
    private static readonly object Lock = new();
    private static Action<RuntimeOperation>? _consumer;

    public static void Install(Action<RuntimeOperation>? consumer)
    {
        lock (Lock) _consumer = consumer;
    }

    public static void Publish(RuntimeOperation operation)
    {
        Action<RuntimeOperation>? consumer;
        lock (Lock) consumer = _consumer;
        consumer?.Invoke(operation);
    }
}

/// <summary>Ambient cancellation, the C# equivalent of the Swift task-local value.</summary>
public static class RestrictedProcessCancellation
{
    private static readonly AsyncLocal<Func<bool>?> Context = new();

    public static Func<bool>? Current => Context.Value;

    public static T WithValue<T>(Func<bool> isCancelled, Func<T> operation)
    {
        var previous = Context.Value;
        Context.Value = isCancelled;
        try
        {
            return operation();
        }
        finally
        {
            Context.Value = previous;
        }
    }
}
