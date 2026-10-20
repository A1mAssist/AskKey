namespace AskKeyApp.Tests;

public sealed class HotkeyPreferenceTests
{
    [Fact]
    public void ARegisteredShortcutIsPersisted()
        => Assert.Equal("ctrlShiftK",
            HotkeyPreference.ToPersist("ctrlShiftK", registered: true, current: "ctrlShiftSpace"));

    [Fact]
    public void AShortcutOwnedByAnotherProgramLeavesTheStoredPreferenceAlone()
    {
        // Persisting a shortcut Ask Key never registered would make every later
        // launch fail the same way while Settings still shows it as chosen.
        Assert.Equal("ctrlShiftSpace",
            HotkeyPreference.ToPersist("ctrlShiftK", registered: false, current: "ctrlShiftSpace"));
    }

    [Fact]
    public void TurningTheShortcutOffIsPersisted()
        => Assert.Equal("disabled",
            HotkeyPreference.ToPersist("disabled", registered: true, current: "ctrlShiftSpace"));

    [Fact]
    public void TheSameShortcutIsPersistedWhenItIsAlreadyRegistered()
        => Assert.Equal("ctrlShiftSpace",
            HotkeyPreference.ToPersist("ctrlShiftSpace", registered: true, current: "ctrlShiftSpace"));
}
