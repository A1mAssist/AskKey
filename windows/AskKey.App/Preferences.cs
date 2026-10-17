using System.Text.Json;
using System.Text.Json.Serialization;
using AskKey.Vault;
using Microsoft.Win32;

namespace AskKeyApp;

public sealed class Preferences
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _lock = new();

    public Preferences()
    {
        var root = VaultConfiguration.Default.LocalApplicationDataDirectory;
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "settings.json");
        Load();
    }

    public bool HasCompletedOnboarding { get; set; }
    public int DefaultTimedAllowanceMinutes { get; set; } = 30;
    public bool TimedAllowanceEnabled { get; set; } = true;
    public bool ReadApprovalAuthenticationEnabled { get; set; } = true;
    public int SessionTimeoutSeconds { get; set; } = 300;
    public string HotkeyShortcut { get; set; } = "ctrlShiftSpace";
    public string LanguageMode { get; set; } = "system";
    public string AppearanceMode { get; set; } = "system";
    public bool LaunchAtLogin { get; set; } = true;

    // Deserialization must not run the Preferences constructor: it loads this
    // same file and would recurse until the stack overflows.
    private sealed class PreferencesFile
    {
        public bool HasCompletedOnboarding { get; set; }
        public int DefaultTimedAllowanceMinutes { get; set; } = 30;
        public bool TimedAllowanceEnabled { get; set; } = true;
        public bool ReadApprovalAuthenticationEnabled { get; set; } = true;
        public int SessionTimeoutSeconds { get; set; } = 300;
        public string? HotkeyShortcut { get; set; } = "ctrlShiftSpace";
        public string? LanguageMode { get; set; } = "system";
        public string? AppearanceMode { get; set; } = "system";
        public bool LaunchAtLogin { get; set; } = true;
    }

    public void Save()
    {
        lock (_lock)
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<PreferencesFile>(File.ReadAllText(_path), JsonOptions);
            if (loaded is null) return;
            HasCompletedOnboarding = loaded.HasCompletedOnboarding;
            DefaultTimedAllowanceMinutes = loaded.DefaultTimedAllowanceMinutes > 0 ? loaded.DefaultTimedAllowanceMinutes : 30;
            TimedAllowanceEnabled = loaded.TimedAllowanceEnabled;
            ReadApprovalAuthenticationEnabled = loaded.ReadApprovalAuthenticationEnabled;
            SessionTimeoutSeconds = loaded.SessionTimeoutSeconds > 0 ? loaded.SessionTimeoutSeconds : 300;
            HotkeyShortcut = string.IsNullOrWhiteSpace(loaded.HotkeyShortcut) ? "ctrlShiftSpace" : loaded.HotkeyShortcut;
            LanguageMode = string.IsNullOrWhiteSpace(loaded.LanguageMode) ? "system" : loaded.LanguageMode;
            AppearanceMode = string.IsNullOrWhiteSpace(loaded.AppearanceMode) ? "system" : loaded.AppearanceMode;
            LaunchAtLogin = loaded.LaunchAtLogin;
        }
        catch (Exception)
        {
            // A corrupt preference file must not prevent the vault from opening.
        }
    }
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AskKey";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enabled)
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            key.SetValue(ValueName, $"\"{executable}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
