using Windows.ApplicationModel.DataTransfer;

namespace AskKeyApp;

public static class ClipboardSafety
{
    public static async void Copy(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
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
