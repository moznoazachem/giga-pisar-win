using GigaPisar.App;
using Xunit;

namespace GigaPisar.Tests;

/// <summary>IdleCache: the speech model is loaded on demand and freed after a quiet spell.</summary>
public class IdleCacheTests
{
    private sealed class Model : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private DateTime _now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private int _loads;

    private IdleCache<Model> Cache(Func<Model>? load = null) =>
        new(load ?? (() => { _loads++; return new Model(); }), () => _now);

    [Fact]
    public async Task LoadsOnFirstUseAndKeepsIt()
    {
        var cache = Cache();
        Assert.False(cache.IsLoaded);

        var first = await cache.GetAsync();
        var second = await cache.GetAsync();

        Assert.Same(first, second);
        Assert.Equal(1, _loads);
        Assert.True(cache.IsLoaded);
    }

    [Fact]
    public async Task UsesDuringALoadShareIt()
    {
        using var gate = new ManualResetEventSlim();
        var cache = Cache(() => { gate.Wait(); _loads++; return new Model(); });

        var a = cache.GetAsync();   // the key press starts the load
        var b = cache.GetAsync();   // the release waits for the same one
        gate.Set();

        Assert.Same(await a, await b);
        Assert.Equal(1, _loads);
    }

    [Fact]
    public async Task FreesOnlyAfterTheIdleTime()
    {
        var cache = Cache();
        var model = await cache.GetAsync();

        _now += TimeSpan.FromMinutes(9);
        Assert.False(cache.FreeIfIdle(TimeSpan.FromMinutes(10), inUse: false));
        Assert.False(model.Disposed);

        _now += TimeSpan.FromMinutes(1);
        Assert.True(cache.FreeIfIdle(TimeSpan.FromMinutes(10), inUse: false));
        Assert.True(model.Disposed);
        Assert.False(cache.IsLoaded);
    }

    [Fact]
    public async Task NeverFreesWhatIsInUse()
    {
        var cache = Cache();
        var model = await cache.GetAsync();
        _now += TimeSpan.FromHours(1);

        Assert.False(cache.FreeIfIdle(TimeSpan.FromMinutes(10), inUse: true));   // recording or recognizing
        Assert.False(model.Disposed);
    }

    [Fact]
    public async Task TouchStartsTheIdleTimeOver()
    {
        var cache = Cache();
        await cache.GetAsync();
        _now += TimeSpan.FromMinutes(8);
        cache.Touch();   // a take has just ended
        _now += TimeSpan.FromMinutes(8);

        Assert.False(cache.FreeIfIdle(TimeSpan.FromMinutes(10), inUse: false));
    }

    [Fact]
    public void DoesNotFreeWhileLoading()
    {
        using var gate = new ManualResetEventSlim();
        var cache = Cache(() => { gate.Wait(); return new Model(); });
        var loading = cache.GetAsync();
        _now += TimeSpan.FromHours(1);

        Assert.False(cache.FreeIfIdle(TimeSpan.FromMinutes(10), inUse: false));
        gate.Set();
        Assert.False(loading.Result.Disposed);
    }

    [Fact]
    public async Task LoadsAgainAfterBeingFreed()
    {
        var cache = Cache();
        var first = await cache.GetAsync();
        _now += TimeSpan.FromMinutes(10);
        cache.FreeIfIdle(TimeSpan.FromMinutes(10), inUse: false);

        var second = await cache.GetAsync();

        Assert.NotSame(first, second);
        Assert.Equal(2, _loads);
        Assert.False(second.Disposed);
    }

    [Fact]
    public async Task RetriesAFailedLoad()
    {
        bool fail = true;
        var cache = Cache(() => fail ? throw new IOException("model file is locked") : new Model());

        await Assert.ThrowsAsync<IOException>(cache.GetAsync);
        fail = false;
        var model = await cache.GetAsync();

        Assert.False(model.Disposed);
        Assert.True(cache.IsLoaded);
    }

    [Fact]
    public async Task DisposeFreesTheModelEvenIfItIsStillLoading()
    {
        var cache = Cache();
        var loaded = await cache.GetAsync();
        cache.Dispose();
        Assert.True(loaded.Disposed);

        using var gate = new ManualResetEventSlim();
        var slow = Cache(() => { gate.Wait(); return new Model(); });
        var loading = slow.GetAsync();
        slow.Dispose();   // quitting while the model loads
        gate.Set();
        var late = await loading;
        Assert.True(SpinWait.SpinUntil(() => late.Disposed, 2000), "the model that finished loading after Dispose was kept");
    }
}
