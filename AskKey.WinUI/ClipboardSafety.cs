using Windows.ApplicationModel.DataTransfer;

namespace AskKeyApp;

public static class ClipboardSafety
{
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
        await Task.Delay(TimeSpan.FromSeconds(60));
        var dispatcher = App.CurrentWindow?.DispatcherQueue;
        if (dispatcher is null) return;
        dispatcher.TryEnqueue(async () =>
        {
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
}
