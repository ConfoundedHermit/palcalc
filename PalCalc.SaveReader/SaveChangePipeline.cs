using Serilog;

namespace PalCalc.SaveReader;

internal enum SaveChangeEventSource
{
    StandardFileSystemWatcher,
    XboxWgsFolderWatcher,
}

internal enum SaveChangeEventOutcome
{
    Ignored,
    Pending,
    Notify,
}

internal record RawSaveChangeEvent(
    SaveChangeEventSource Source,
    WatcherChangeTypes Kind,
    string Path,
    DateTimeOffset Timestamp)
{
    public static RawSaveChangeEvent Create(SaveChangeEventSource source, WatcherChangeTypes kind, string path) =>
        new(source, kind, path, DateTimeOffset.UtcNow);
}

/// <summary>
/// Converts relevant watcher events into one notification after a per-save quiet interval.
/// Callers classify paths before submitting them, so unsupported paths never reach UI state.
/// </summary>
internal sealed class SaveChangePipeline : IDisposable
{
    public static readonly TimeSpan DefaultQuietInterval = TimeSpan.FromSeconds(1);

    private static readonly ILogger logger = Log.ForContext<SaveChangePipeline>();

    private readonly TimeSpan quietInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Dictionary<string, CancellationTokenSource> pendingBySaveKey = [];
    private bool disposed;

    public SaveChangePipeline(
        TimeSpan quietInterval,
        Func<TimeSpan, CancellationToken, Task> delayAsync = null)
    {
        this.quietInterval = quietInterval;
        this.delayAsync = delayAsync ?? Task.Delay;
    }

    public SaveChangeEventOutcome Process(
        string saveKey,
        RawSaveChangeEvent rawEvent,
        bool isRelevant,
        Action<RawSaveChangeEvent> notify)
    {
        if (!isRelevant)
        {
            logger.Debug(
                "Ignored save change event {source} {kind} at {path}",
                rawEvent.Source,
                rawEvent.Kind,
                rawEvent.Path);
            return SaveChangeEventOutcome.Ignored;
        }

        CancellationTokenSource cancellation;
        lock (pendingBySaveKey)
        {
            if (disposed)
                return SaveChangeEventOutcome.Ignored;

            if (pendingBySaveKey.Remove(saveKey, out var previous))
                previous.Cancel();

            cancellation = new CancellationTokenSource();
            pendingBySaveKey.Add(saveKey, cancellation);
        }

        logger.Debug(
            "Pending save change event {source} {kind} at {path} for {saveKey}",
            rawEvent.Source,
            rawEvent.Kind,
            rawEvent.Path,
            saveKey);
        _ = NotifyAfterQuietIntervalAsync(saveKey, rawEvent, cancellation, notify);
        return SaveChangeEventOutcome.Pending;
    }

    public void Dispose()
    {
        lock (pendingBySaveKey)
        {
            if (disposed)
                return;

            disposed = true;
            foreach (var cancellation in pendingBySaveKey.Values)
                cancellation.Cancel();

            pendingBySaveKey.Clear();
        }
    }

    private async Task NotifyAfterQuietIntervalAsync(
        string saveKey,
        RawSaveChangeEvent rawEvent,
        CancellationTokenSource cancellation,
        Action<RawSaveChangeEvent> notify)
    {
        try
        {
            await delayAsync(quietInterval, cancellation.Token);

            lock (pendingBySaveKey)
            {
                if (disposed || cancellation.IsCancellationRequested ||
                    !pendingBySaveKey.TryGetValue(saveKey, out var current) || current != cancellation)
                    return;

                pendingBySaveKey.Remove(saveKey);
            }

            logger.Information(
                "Notifying stable save change {source} {kind} at {path} for {saveKey}",
                rawEvent.Source,
                rawEvent.Kind,
                rawEvent.Path,
                saveKey);
            notify(rawEvent);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Save change debounce failed for {saveKey}", saveKey);
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
