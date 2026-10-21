using System.Diagnostics;
using AskKey.Integrations;
using Velopack;
using Velopack.Sources;

namespace AskKeyApp;

public enum UpdateState
{
    /// <summary>The installed build is the newest published release.</summary>
    UpToDate,

    /// <summary>A newer release is published and can be downloaded.</summary>
    Available,

    /// <summary>This build cannot check: it is not a Velopack install, or the feed was unreachable.</summary>
    Unavailable,
}

public readonly record struct UpdateProbe(UpdateState State, string Version);

/// <summary>Where updates come from. Separated so the check and download rules can be
/// tested without a Velopack install or a network.</summary>
internal interface IUpdateFeed
{
    Task<UpdateProbe> CheckAsync();
    Task DownloadAsync(Action<int> progress);
    void Apply();
}

/// <summary>Publish feed for the public GitHub repository, which serves a release feed
/// without a token.</summary>
internal sealed class VelopackUpdateFeed : IUpdateFeed
{
    private const string RepositoryUrl = "https://github.com/A1mAssist/AskKey";

    private readonly UpdateManager _manager;
    private UpdateInfo? _update;

    private VelopackUpdateFeed(UpdateManager manager) => _manager = manager;

    /// <summary>Returns null for a portable or development build: those have nothing to update into.</summary>
    public static VelopackUpdateFeed? TryCreate()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, prerelease: false, downloader: null));
            return manager.IsInstalled ? new VelopackUpdateFeed(manager) : null;
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            return null;
        }
    }

    public async Task<UpdateProbe> CheckAsync()
    {
        _update = await _manager.CheckForUpdatesAsync();
        return _update is null
            ? new UpdateProbe(UpdateState.UpToDate, AskKeyVersion.Current)
            : new UpdateProbe(UpdateState.Available, _update.TargetFullRelease.Version.ToString());
    }

    public Task DownloadAsync(Action<int> progress)
        => _manager.DownloadUpdatesAsync(
            _update ?? throw new InvalidOperationException("Check for updates before downloading."),
            progress, CancellationToken.None);

    // Exits this process, so nothing may run after it.
    public void Apply() => _manager.ApplyUpdatesAndRestart(_update!.TargetFullRelease);
}

/// <summary>Checks once at startup and on demand. Downloading is always a user action:
/// an update replaces the app, so it never starts on its own.</summary>
public sealed class AppUpdateService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IUpdateFeed? _feed;

    public AppUpdateService() : this(VelopackUpdateFeed.TryCreate())
    {
    }

    internal AppUpdateService(IUpdateFeed? feed) => _feed = feed;

    public event Action<string>? UpdateFound;

    public bool IsSupported => _feed is not null;
    public UpdateProbe State { get; private set; }

    public Task<UpdateProbe> CheckAsync() => ExclusiveAsync(async () =>
    {
        if (_feed is null) return State = new UpdateProbe(UpdateState.Unavailable, "");
        UpdateProbe probe;
        try
        {
            probe = await _feed.CheckAsync();
        }
        catch (Exception error)
        {
            // An unreachable feed is not worth interrupting the user over.
            Debug.WriteLine(error);
            return State = new UpdateProbe(UpdateState.Unavailable, "");
        }
        State = probe;
        if (probe.State == UpdateState.Available) UpdateFound?.Invoke(probe.Version);
        return probe;
    });

    public Task DownloadAndRestartAsync(Action<int> progress) => ExclusiveAsync(async () =>
    {
        if (_feed is null || State.State != UpdateState.Available)
            throw new InvalidOperationException("There is no update to download.");
        // Claim the update before downloading, so a second click during a long download
        // cannot queue another full package. A failed download gives the claim back.
        var claimed = State;
        State = new UpdateProbe(UpdateState.Unavailable, "");
        try
        {
            await _feed.DownloadAsync(progress);
            _feed.Apply();
        }
        catch
        {
            State = claimed;
            throw;
        }
        return true;
    });

    private async Task<T> ExclusiveAsync<T>(Func<Task<T>> action)
    {
        await _gate.WaitAsync();
        try { return await action(); }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
