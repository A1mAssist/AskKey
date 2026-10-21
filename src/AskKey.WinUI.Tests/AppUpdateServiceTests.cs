namespace AskKeyApp.Tests;

/// <summary>An update replaces the running app, so the rules that decide whether one may
/// start are worth pinning: no feed means no update, a failed check is not an error, and
/// two clicks must not produce two downloads.</summary>
public sealed class AppUpdateServiceTests
{
    private sealed class FakeFeed : IUpdateFeed
    {
        public UpdateProbe Probe { get; set; } = new(UpdateState.UpToDate, "");
        public Exception? CheckFailure { get; set; }
        public bool DownloadFailure { get; set; }
        public int Checks;
        public int Downloads;
        public bool Applied;

        public async Task<UpdateProbe> CheckAsync()
        {
            Interlocked.Increment(ref Checks);
            await Task.Yield();
            if (CheckFailure is not null) throw CheckFailure;
            return Probe;
        }

        public async Task DownloadAsync(Action<int> progress)
        {
            Interlocked.Increment(ref Downloads);
            await Task.Yield();
            if (DownloadFailure) throw new IOException("connection lost");
            progress(50);
        }

        public void Apply() => Applied = true;
    }

    [Fact]
    public async Task ABuildWithoutAFeedReportsUnavailableInsteadOfThrowing()
    {
        using var service = new AppUpdateService(null);

        var probe = await service.CheckAsync();

        Assert.Equal(UpdateState.Unavailable, probe.State);
        Assert.False(service.IsSupported);
    }

    [Fact]
    public async Task DownloadingWithoutAPublishedUpdateIsRefused()
    {
        var feed = new FakeFeed();
        using var service = new AppUpdateService(feed);
        await service.CheckAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadAndRestartAsync(_ => { }));
        Assert.False(feed.Applied);
    }

    [Fact]
    public async Task AFailingFeedBecomesUnavailableAndNeverReachesTheUser()
    {
        var feed = new FakeFeed { CheckFailure = new HttpRequestException("no network") };
        using var service = new AppUpdateService(feed);
        var announced = new List<string>();
        service.UpdateFound += announced.Add;

        await service.CheckAsync();

        Assert.Equal(UpdateState.Unavailable, service.State.State);
        Assert.Empty(announced);
    }

    [Fact]
    public async Task ANewerReleaseIsAnnouncedOnce()
    {
        var feed = new FakeFeed { Probe = new UpdateProbe(UpdateState.Available, "9.9.9") };
        using var service = new AppUpdateService(feed);
        var announced = new List<string>();
        service.UpdateFound += announced.Add;

        await service.CheckAsync();

        Assert.Equal(UpdateState.Available, service.State.State);
        Assert.Equal(["9.9.9"], announced);
    }

    [Fact]
    public async Task ConcurrentDownloadRequestsStillProduceOneDownload()
    {
        var feed = new FakeFeed { Probe = new UpdateProbe(UpdateState.Available, "9.9.9") };
        using var service = new AppUpdateService(feed);
        await service.CheckAsync();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try { await service.DownloadAndRestartAsync(_ => { }); }
            catch (InvalidOperationException) { }
        })));

        Assert.Equal(1, feed.Downloads);
        Assert.True(feed.Applied);
    }

    [Fact]
    public async Task AFailedDownloadOffersTheUpdateAgain()
    {
        var feed = new FakeFeed { Probe = new UpdateProbe(UpdateState.Available, "9.9.9"), DownloadFailure = true };
        using var service = new AppUpdateService(feed);
        await service.CheckAsync();

        await Assert.ThrowsAsync<IOException>(() => service.DownloadAndRestartAsync(_ => { }));

        // The claim is temporary: losing the network must not hide a real update.
        Assert.Equal(UpdateState.Available, service.State.State);
    }
}


