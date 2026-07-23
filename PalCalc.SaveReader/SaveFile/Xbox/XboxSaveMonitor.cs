using Serilog;

namespace PalCalc.SaveReader.SaveFile.Xbox
{
    public class XboxSaveMonitor(string saveId)
    {
        public string SaveId => saveId;

        public event Action Updated;

        internal void Notify() => Updated?.Invoke();
    }

    public class XboxFolderMonitor : IDisposable
    {
        private static readonly ILogger logger = Log.ForContext<XboxFolderMonitor>();

        private readonly Dictionary<string, XboxSaveMonitor> saveMonitorsById = [];
        private readonly SaveChangePipeline changePipeline;
        private readonly FileSystemWatcher watcher;

        internal XboxFolderMonitor(string basePath, SaveChangePipeline changePipeline = null)
        {
            this.changePipeline = changePipeline ?? new SaveChangePipeline(SaveChangePipeline.DefaultQuietInterval);

            watcher = new FileSystemWatcher(basePath);
            watcher.Changed += Watcher_Changed;
            watcher.Created += Watcher_Changed;
            watcher.Renamed += Watcher_Renamed;
            watcher.Deleted += Watcher_Changed;

            watcher.IncludeSubdirectories = true;
            watcher.EnableRaisingEvents = true;
        }

        internal event Action<RawSaveChangeEvent> RawChanged;

        private void Watcher_Changed(object sender, FileSystemEventArgs e) =>
            RawChanged?.Invoke(RawSaveChangeEvent.Create(SaveChangeEventSource.XboxWgsFolderWatcher, e.ChangeType, e.FullPath));

        private void Watcher_Renamed(object sender, RenamedEventArgs e)
        {
            RawChanged?.Invoke(RawSaveChangeEvent.Create(SaveChangeEventSource.XboxWgsFolderWatcher, e.ChangeType, e.FullPath));
            RawChanged?.Invoke(RawSaveChangeEvent.Create(SaveChangeEventSource.XboxWgsFolderWatcher, e.ChangeType, e.OldFullPath));
        }

        internal SaveChangeEventOutcome RouteKnownSaveChange(string saveId, RawSaveChangeEvent rawEvent)
        {
            XboxSaveMonitor monitor;
            lock (saveMonitorsById)
            {
                if (!saveMonitorsById.TryGetValue(saveId, out monitor))
                {
                    logger.Warning(
                        "Ignored WGS save change at {path}; classified save ID {saveId} is not registered",
                        rawEvent.Path,
                        saveId);
                    return SaveChangeEventOutcome.Ignored;
                }
            }

            return changePipeline.Process(saveId, rawEvent, isRelevant: true, _ => monitor.Notify());
        }

        public XboxSaveMonitor GetSaveMonitor(string saveId)
        {
            lock (saveMonitorsById)
            {
                if (saveMonitorsById.TryGetValue(saveId, out var existing))
                    return existing;

                var result = new XboxSaveMonitor(saveId);
                saveMonitorsById.Add(saveId, result);
                return result;
            }
        }

        public void ReleaseSaveMonitor(XboxSaveMonitor monitor)
        {
            lock (saveMonitorsById)
            {
                if (saveMonitorsById.TryGetValue(monitor.SaveId, out var existing) && existing == monitor)
                    saveMonitorsById.Remove(monitor.SaveId);
            }
        }

        public void Dispose()
        {
            watcher.Changed -= Watcher_Changed;
            watcher.Created -= Watcher_Changed;
            watcher.Renamed -= Watcher_Renamed;
            watcher.Deleted -= Watcher_Changed;
            watcher.Dispose();
            changePipeline.Dispose();

            lock (saveMonitorsById)
                saveMonitorsById.Clear();
        }
    }
}
