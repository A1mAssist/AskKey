using Microsoft.Win32;

namespace AskKeyApp;

/// <summary>Maps Windows session transitions to the approval privacy gate.</summary>
internal static class SessionPrivacyPolicy
{
    public static bool HidesDetailedApprovals(SessionSwitchReason reason) => reason
        is SessionSwitchReason.SessionLock
        or SessionSwitchReason.ConsoleDisconnect
        or SessionSwitchReason.RemoteDisconnect
        or SessionSwitchReason.SessionLogoff;

    public static bool RestoresDetailedApprovals(SessionSwitchReason reason) => reason
        is SessionSwitchReason.SessionUnlock
        or SessionSwitchReason.ConsoleConnect
        or SessionSwitchReason.RemoteConnect;
}
