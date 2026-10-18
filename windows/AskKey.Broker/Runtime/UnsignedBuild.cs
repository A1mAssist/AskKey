namespace AskKey.Broker.Runtime;

/// <summary>
/// Compiled into builds that ship without Authenticode. Helper trust then comes
/// from the manifest embedded in the host instead of a publisher signature.
/// </summary>
public static class UnsignedBuild
{
#if ASKKEY_UNSIGNED
    public const bool Allowed = true;
#else
    public const bool Allowed = false;
#endif
}
