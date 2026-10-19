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
            // Older Windows builds may reject the history/sync flags; copying is still useful.
            try { Clipboard.SetContent(package); } catch { return; }
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
