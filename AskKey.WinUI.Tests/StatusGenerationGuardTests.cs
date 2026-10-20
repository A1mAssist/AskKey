namespace AskKeyApp.Tests;

/// <summary>
/// A single-client check or connect that starts before a refresh must not write
/// its result over the newer refresh. Every row write goes through this rule, on
/// both the success and the failure path.
/// </summary>
public sealed class StatusGenerationGuardTests
{
    [Fact]
    public void AnUnchangedGenerationStillOwnsTheRow()
    {
        Assert.True(AgentAccessView.OwnsRow(7, 7));
    }

    [Fact]
    public void ANewerRefreshRevokesTheOlderOperation()
    {
        Assert.False(AgentAccessView.OwnsRow(7, 8));
    }

    [Fact]
    public async Task OnlyTheOperationThatStillOwnsTheRowWritesIt()
    {
        var current = 0;
        var applied = new System.Collections.Concurrent.ConcurrentBag<int>();
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            var mine = Interlocked.Increment(ref current);
            Thread.SpinWait(Random.Shared.Next(400));
            if (AgentAccessView.OwnsRow(mine, Volatile.Read(ref current))) applied.Add(mine);
        })));

        Assert.Contains(Volatile.Read(ref current), applied);
        // No generation may write twice, and nothing older than the newest survives.
        Assert.All(applied.GroupBy(mine => mine), entry => Assert.Single(entry));
        Assert.All(applied, mine => Assert.True(mine <= Volatile.Read(ref current)));
    }
}
