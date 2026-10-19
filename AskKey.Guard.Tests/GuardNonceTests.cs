namespace AskKey.Guard.Tests;

public sealed class GuardNonceTests
{
    [Fact]
    public void NonceIs32BytesOneTimeAndExpiresAfterFiveMinutes()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new GuardNonceStore(clock);
        const string sid = "S-1-5-21-1";

        var nonce = store.Issue(sid);
        Assert.Equal(32, nonce.Length);
        Assert.True(store.Consume(sid, nonce));
        Assert.False(store.Consume(sid, nonce));

        var expired = store.Issue(sid);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.False(store.Consume(sid, expired));
    }

    [Fact]
    public void NonceBelongsToItsSid()
    {
        var store = new GuardNonceStore();
        var nonce = store.Issue("S-1-5-21-1");
        Assert.False(store.Consume("S-1-5-21-2", nonce));
        Assert.True(store.Consume("S-1-5-21-1", nonce));
    }

    [Fact]
    public void FifthPendingNonceEvictsTheOldest()
    {
        var store = new GuardNonceStore();
        const string sid = "S-1-5-21-1";
        var first = store.Issue(sid);
        var remaining = Enumerable.Range(0, 4)
            .Select(_ => store.Issue(sid))
            .ToArray();

        Assert.False(store.Consume(sid, first));
        Assert.All(remaining, nonce => Assert.True(store.Consume(sid, nonce)));
    }
}
