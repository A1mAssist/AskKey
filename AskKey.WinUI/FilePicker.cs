using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AskKeyApp;

internal static class FilePicker
{
    public static async Task<string?> PickAsync(string title)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        foreach (var extension in new[] { ".env", ".txt", ".json", ".yaml", ".yml", ".toml", ".pem", ".key" })
            picker.FileTypeFilter.Add(extension);
        picker.FileTypeFilter.Add("*");
        var window = App.CurrentWindow;
        if (window is null) return null;
        InitializeWithWindow.Initialize(picker, WindowHelpers.Handle(window));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
