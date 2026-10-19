namespace AskKey.Vault;

public sealed class VaultConfiguration
{
    public const string ProductionServiceName = "AskKey.Vault";
    public const string DevelopmentServiceName = "AskKey.Vault.dev";

    public bool IsDevelopmentBuild { get; }
    public string ApplicationSupportDirectory { get; }
    public string LocalApplicationDataDirectory { get; }
    public string ServiceName => IsDevelopmentBuild ? DevelopmentServiceName : ProductionServiceName;

    public VaultConfiguration(string? applicationSupportRoot = null, string? localApplicationDataRoot = null,
        bool development = false)
    {
        IsDevelopmentBuild = development;
        var appRoot = applicationSupportRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localRoot = localApplicationDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var suffix = development ? Path.Combine("AskKey", "dev") : "AskKey";
        ApplicationSupportDirectory = Path.Combine(appRoot, suffix);
        LocalApplicationDataDirectory = Path.Combine(localRoot, suffix);
    }

    public static VaultConfiguration Default => new(
        development: Environment.GetEnvironmentVariable("ASKKEY_DEVELOPMENT") == "1");

    public string VaultFile => Path.Combine(ApplicationSupportDirectory, "credentials-v2.db");
    public string CreatingVaultFile => VaultFile + ".creating";
    public string KeyMaterialDirectory => Path.Combine(ApplicationSupportDirectory, "key-material");
    public string DeliveryDirectory => Path.Combine(LocalApplicationDataDirectory, "file-deliveries");
    public string LocalEraseJournalFile => Path.Combine(ApplicationSupportDirectory, "lifecycle", "erase.journal");
    public string DaemonPipeName => ServiceName + ".daemon";
}
