namespace AskKeyApp.Tests;

public sealed class ClipboardSafetyTests
{
    [Fact]
    public void ANewerCopyInvalidatesTheEarlierScheduledClear()
    {
        var first = ClipboardSafety.BeginCopy();
        var second = ClipboardSafety.BeginCopy();

        // Copying the same value twice leaves the clipboard identical, so value
        // comparison cannot separate them: only the generation can.
        Assert.True(ClipboardSafety.IsStale(first));
        Assert.False(ClipboardSafety.IsStale(second));
    }

    [Fact]
    public void TheLatestCopyIsNotStale()
    {
        ClipboardSafety.BeginCopy();
        Assert.False(ClipboardSafety.IsStale(ClipboardSafety.BeginCopy()));
        Assert.False(ClipboardSafety.IsStale(ClipboardSafety.BeginCopy()));
    }
}
