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

    /// <summary>
    /// Takes the security-relevant values from the authenticated vault policy. These four
    /// are no longer read from settings.json, because that file is user-writable and a
    /// hand edit there would bypass the confirmation the UI requires.
    /// </summary>
    public void AdoptSecurityPolicy(AskKey.Vault.AgentAccessPolicy policy)
    {
        lock (_lock)
        {
            ReadApprovalAuthenticationEnabled = policy.ReadApprovalAuthenticationEnabled;
            TimedAllowanceEnabled = policy.TimedAllowanceEnabled;
            DefaultTimedAllowanceMinutes = policy.DefaultTimedAllowanceMinutes;
            SessionTimeoutSeconds = policy.SessionTimeoutSeconds;
        }
    }

    /// <summary>
    /// Applies a confirmed settings change to the authenticated policy. The caller has
    /// already obtained Windows Hello for the parts that weaken a setting.
    /// </summary>
    public void SaveSecuritySettings(AppHost host, string reason)
    {
        AdoptSecurityPolicy(host.Vault.EffectivePolicy);
        host.Vault.SaveSecuritySettings(
            host.AuthenticateAndBeginManagement(reason),
            ReadApprovalAuthenticationEnabled,
            TimedAllowanceEnabled,
            DefaultTimedAllowanceMinutes,
            SessionTimeoutSeconds);
        AdoptSecurityPolicy(host.Vault.EffectivePolicy);
    }

    // Deserialization must not run the Preferences constructor: it loads this
    // same file and would recurse until the stack overflows.
    private sealed class PreferencesFile
    {
        public bool HasCompletedOnboarding { get; set; }
        // The four security settings used to live here. They now come from the
        // authenticated vault policy; leftover fields in an old file are ignored and
        // dropped on the next save.
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
