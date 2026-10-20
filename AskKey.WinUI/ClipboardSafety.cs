using Windows.ApplicationModel.DataTransfer;

namespace AskKeyApp;

public static class ClipboardSafety
{
    private static int _generation;

    public static async void Copy(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        try
        {
            Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
            {
                IsAllowedInHistory = false,
                IsRoamable = false,
            });
        }
        catch
        {
            // Never fall back to a history-enabled plain copy: that silently
            // undoes the privacy guarantee this path exists to provide.
            Dialogs.ShowError("Ask Key could not copy without clipboard history. Copy was cancelled.");
            return;
        }
        var generation = BeginCopy();
        await Task.Delay(TimeSpan.FromSeconds(60));
        var dispatcher = App.CurrentWindow?.DispatcherQueue;
        if (dispatcher is null) return;
        dispatcher.TryEnqueue(async () =>
        {
            if (IsStale(generation)) return;
            try
            {
                var data = Clipboard.GetContent();
                if (!data.Contains(StandardDataFormats.Text)) return;
                if (await data.GetTextAsync() == value) Clipboard.Clear();
            }
            catch
            {
            }
        });
    }

    internal static int BeginCopy() => Interlocked.Increment(ref _generation);

    /// <summary>True once a newer copy has been scheduled, so an earlier timer must
    /// not clear the clipboard the user just copied again. Copying the same value
    /// twice is still two copies, and value comparison alone cannot tell them
    /// apart: both timers would see an identical clipboard.</summary>
    internal static bool IsStale(int generation) => generation != Volatile.Read(ref _generation);
}
