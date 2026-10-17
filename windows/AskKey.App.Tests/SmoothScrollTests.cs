namespace AskKeyApp.Tests;

public sealed class SmoothScrollTests
{
    [Fact]
    public void WheelDeltaMovesSmoothlyAndClampsAtBounds()
    {
        Assert.Equal(56, SmoothScroll.GetTargetOffset(0, -120, 500));
        Assert.Equal(44, SmoothScroll.GetTargetOffset(100, 120, 500));
        Assert.Equal(0, SmoothScroll.GetTargetOffset(20, 120, 500));
        Assert.Equal(500, SmoothScroll.GetTargetOffset(480, -240, 500));
    }

    [Fact]
    public void FrameAdvanceMovesTowardTargetWithoutOvershoot()
    {
        var first = SmoothScroll.Advance(0, 56, 1d / 60);
        Assert.InRange(first, 10, 20);
        Assert.Equal(56, SmoothScroll.Advance(55.95, 56, 1d / 60));
        Assert.Equal(0, SmoothScroll.Advance(0, 56, 0));
    }
}
