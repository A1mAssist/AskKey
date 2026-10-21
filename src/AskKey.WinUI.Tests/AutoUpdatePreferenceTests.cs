namespace AskKeyApp.Tests;

/// <summary>Auto-check starts on: an existing settings.json written before the switch
/// existed has no field for it, and a user who never asked to be opted out should not
/// silently have to press the button forever.</summary>
public sealed class AutoUpdatePreferenceTests
{
    private static string Sandbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "askkey-updates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void AutoCheckIsOnForANewInstall()
    {
        var root = Sandbox();
        try
        {
            Assert.True(new Preferences(root).AutoCheckUpdates);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TurningAutoCheckOffSurvivesARestart()
    {
        var root = Sandbox();
        try
        {
            new Preferences(root) { AutoCheckUpdates = false }.Save();

            Assert.False(new Preferences(root).AutoCheckUpdates);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AFileWrittenBeforeTheSwitchStillGetsAutoCheck()
    {
        var root = Sandbox();
        try
        {
            // Exactly what 1.0.2 wrote: no AutoCheckUpdates field at all.
            File.WriteAllText(Path.Combine(root, "settings.json"),
                """{ "HasCompletedOnboarding": true, "LaunchAtLogin": true }""");

            Assert.True(new Preferences(root).AutoCheckUpdates);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
