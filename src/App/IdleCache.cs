// Keeps an expensive object (the speech model) in memory while it is used and lets it go after a
// quiet spell, so an idle Pisar does not hold hundreds of megabytes. The next use loads it again.
// Not thread-safe: the app calls it from the UI thread only.

namespace GigaPisar.App;

public sealed class IdleCache<T> : IDisposable where T : class, IDisposable
{
    private readonly Func<T> _load;
    private readonly Func<DateTime> _clock;
    private Task<T>? _value;
    private DateTime _lastUse;

    public IdleCache(Func<T> load, Func<DateTime>? clock = null)
    {
        _load = load;
        _clock = clock ?? (() => DateTime.UtcNow);
        _lastUse = _clock();
    }

    /// <summary>In memory right now: loaded, not merely loading.</summary>
    public bool IsLoaded => _value is { IsCompletedSuccessfully: true };

    /// <summary>The object, loaded on a pool thread if it is not in memory. Every use shares one load,
    /// and a load that failed is tried again on the next use.</summary>
    public Task<T> GetAsync()
    {
        _lastUse = _clock();
        if (_value is null || _value.IsFaulted || _value.IsCanceled) _value = Task.Run(_load);
        return _value;
    }

    /// <summary>Counts as use without loading anything: the idle time starts over.</summary>
    public void Touch() => _lastUse = _clock();

    /// <summary>Frees the object once it has gone unused for <paramref name="after"/>. Only the
    /// caller knows whether it is being used right now (a take is recording or being recognized).</summary>
    public bool FreeIfIdle(TimeSpan after, bool inUse)
    {
        if (inUse || _value is not { IsCompletedSuccessfully: true } loaded || _clock() - _lastUse < after) return false;
        _value = null;
        loaded.Result.Dispose();
        return true;
    }

    public void Dispose()
    {
        if (_value is { IsCompletedSuccessfully: true } loaded) loaded.Result.Dispose();
        else _value?.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);   // freed once its load finishes
        _value = null;
    }
}
