namespace AskKey.Guard;

public static class GuardPaths
{
    /// <summary>ProgramData for the per-machine service; LocalAppData for --dev.</summary>
    public static string DataRoot(bool development)
        => Path.Combine(
            Environment.GetFolderPath(development
                ? Environment.SpecialFolder.LocalApplicationData
                : Environment.SpecialFolder.CommonApplicationData),
            "AskKey",
            "Guard");
}
