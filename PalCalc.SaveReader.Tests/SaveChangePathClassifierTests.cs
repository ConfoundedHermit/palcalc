using PalCalc.SaveReader.SaveFile.Xbox;

namespace PalCalc.SaveReader.Tests;

[TestClass]
public class SaveChangePathClassifierTests
{
    private const string SaveRoot = @"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF";

    [DataTestMethod]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF\\Level.sav")]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF\\Level_1.sav")]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF\\LevelMeta.sav")]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF\\LocalData.sav")]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF\\WorldOption.sav")]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF\\Players\\0123456789ABCDEF0123456789ABCDEF.sav")]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF\\Players\\0123456789ABCDEF0123456789ABCDEF_dps.sav")]
    public void IsRelevantStandardSavePath_RecognizesWorldAndPlayerFiles(string path)
    {
        Assert.IsTrue(SaveChangePathClassifier.IsRelevantStandardSavePath(SaveRoot, path));
    }

    [DataTestMethod]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF\\backup\\world\\Level.sav")]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF\\Players\\not-a-player.sav")]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF\\Level.sav.tmp")]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\ABCDEF\\notes.txt")]
    [DataRow(@"C:\\Palworld\\Saved\\SaveGames\\12345\\OTHER\\Level.sav")]
    public void IsRelevantStandardSavePath_RejectsBackupsTemporaryAndUnrelatedPaths(string path)
    {
        Assert.IsFalse(SaveChangePathClassifier.IsRelevantStandardSavePath(SaveRoot, path));
    }

    [DataTestMethod]
    [DataRow("worldone-Level", "worldone")]
    [DataRow("worldone-LevelMeta", "worldone")]
    [DataRow("worldone-Players-0123456789ABCDEF0123456789ABCDEF", "worldone")]
    public void TryGetSaveIdFromWgsEntryName_RecognizesSupportedLogicalEntries(string entryName, string expectedSaveId)
    {
        Assert.IsTrue(SaveChangePathClassifier.TryGetSaveIdFromWgsEntryName(entryName, out var saveId));
        Assert.AreEqual(expectedSaveId, saveId);
    }

    [DataTestMethod]
    [DataRow("Slot1-Level")]
    [DataRow("worldone-Unknown")]
    [DataRow("worldone-Level.tmp")]
    [DataRow("Level")]
    public void TryGetSaveIdFromWgsEntryName_RejectsBackupsAndUnknownEntries(string entryName)
    {
        Assert.IsFalse(SaveChangePathClassifier.TryGetSaveIdFromWgsEntryName(entryName, out _));
    }

    [TestMethod]
    public async Task SaveChangePipeline_CoalescesRelevantEventsBySave()
    {
        var delays = new Queue<TaskCompletionSource>();
        using var pipeline = new SaveChangePipeline(
            TimeSpan.FromSeconds(1),
            (_, _) =>
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                delays.Enqueue(completion);
                return completion.Task;
            });
        var notifications = new List<RawSaveChangeEvent>();
        var notificationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = RawSaveChangeEvent.Create(SaveChangeEventSource.StandardFileSystemWatcher, WatcherChangeTypes.Changed, @"C:\\save\\Level.sav");
        var second = RawSaveChangeEvent.Create(SaveChangeEventSource.StandardFileSystemWatcher, WatcherChangeTypes.Changed, @"C:\\save\\Players\\0123456789ABCDEF0123456789ABCDEF.sav");

        Assert.AreEqual(SaveChangeEventOutcome.Pending, pipeline.Process("save-a", first, isRelevant: true, notifications.Add));
        Assert.AreEqual(
            SaveChangeEventOutcome.Pending,
            pipeline.Process("save-a", second, isRelevant: true, change =>
            {
                notifications.Add(change);
                notificationCompleted.SetResult();
            }));

        delays.Dequeue().SetResult();
        await Task.Yield();
        Assert.AreEqual(0, notifications.Count);

        delays.Dequeue().SetResult();
        await notificationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(second, notifications.Single());
    }

    [TestMethod]
    public async Task SaveChangePipeline_IgnoresIrrelevantEventsAndCancelsPendingEventsOnDispose()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new SaveChangePipeline(TimeSpan.FromSeconds(1), (_, _) => completion.Task);
        var notifications = new List<RawSaveChangeEvent>();
        var change = RawSaveChangeEvent.Create(SaveChangeEventSource.StandardFileSystemWatcher, WatcherChangeTypes.Changed, @"C:\\save\\backup\\Level.sav");

        Assert.AreEqual(SaveChangeEventOutcome.Ignored, pipeline.Process("save-a", change, isRelevant: false, notifications.Add));
        Assert.AreEqual(SaveChangeEventOutcome.Pending, pipeline.Process("save-a", change, isRelevant: true, notifications.Add));

        pipeline.Dispose();
        completion.SetResult();
        await Task.Yield();

        Assert.AreEqual(0, notifications.Count);
    }

    [TestMethod]
    public async Task StandardSaveGame_NotifiesOnceForRelevantWritesAndIgnoresBackupWrites()
    {
        var saveRoot = Path.Combine(Path.GetTempPath(), $"palcalc-save-monitor-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(saveRoot);

        try
        {
            using var save = new StandardSaveGame(saveRoot);
            var notifications = 0;
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            save.Updated += _ =>
            {
                Interlocked.Increment(ref notifications);
                notified.TrySetResult();
            };

            await File.WriteAllTextAsync(Path.Combine(saveRoot, "Level.sav"), "test");
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(250));

            Assert.AreEqual(1, Volatile.Read(ref notifications));

            var backupPath = Path.Combine(saveRoot, "backup", "world");
            Directory.CreateDirectory(backupPath);
            await File.WriteAllTextAsync(Path.Combine(backupPath, "Level.sav"), "backup");
            await Task.Delay(TimeSpan.FromMilliseconds(1_250));

            Assert.AreEqual(1, Volatile.Read(ref notifications));
        }
        finally
        {
            Directory.Delete(saveRoot, recursive: true);
        }
    }

    [TestMethod]
    public void StandardSaveGame_RequiresManualRefreshForSymbolicLink()
    {
        var saveRoot = Path.Combine(Path.GetTempPath(), $"palcalc-save-link-tests-{Guid.NewGuid():N}");
        var targetPath = Path.Combine(Path.GetTempPath(), $"palcalc-save-link-target-{Guid.NewGuid():N}.sav");
        var linkPath = Path.Combine(saveRoot, "Level.sav");
        Directory.CreateDirectory(saveRoot);

        try
        {
            File.WriteAllText(targetPath, "test");
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                Assert.Inconclusive($"Creating symbolic links is not permitted in this environment: {ex.Message}");
            }
            catch (IOException ex)
            {
                Assert.Inconclusive($"Creating symbolic links is not permitted in this environment: {ex.Message}");
            }

            using var save = new StandardSaveGame(saveRoot);

            Assert.IsTrue(save.RequiresManualRefresh);
        }
        finally
        {
            Directory.Delete(saveRoot, recursive: true);
            File.Delete(targetPath);
        }
    }

    [TestMethod]
    public async Task XboxFolderMonitor_NotifiesOnlyTheClassifiedSave()
    {
        var folderPath = Path.Combine(Path.GetTempPath(), $"palcalc-xbox-monitor-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folderPath);

        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var pipeline = new SaveChangePipeline(TimeSpan.FromSeconds(1), (_, _) => completion.Task);
            using var monitor = new XboxFolderMonitor(folderPath, pipeline);
            var firstSave = monitor.GetSaveMonitor("save-one");
            var secondSave = monitor.GetSaveMonitor("save-two");
            var firstNotifications = 0;
            var secondNotifications = 0;
            var firstNotified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            firstSave.Updated += () =>
            {
                Interlocked.Increment(ref firstNotifications);
                firstNotified.TrySetResult();
            };
            secondSave.Updated += () => Interlocked.Increment(ref secondNotifications);
            var change = RawSaveChangeEvent.Create(SaveChangeEventSource.XboxWgsFolderWatcher, WatcherChangeTypes.Changed, Path.Combine(folderPath, "DATA"));

            Assert.AreEqual(SaveChangeEventOutcome.Pending, monitor.RouteKnownSaveChange("save-one", change));
            Assert.AreEqual(SaveChangeEventOutcome.Ignored, monitor.RouteKnownSaveChange("unknown-save", change));

            completion.SetResult();
            await firstNotified.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(1, Volatile.Read(ref firstNotifications));
            Assert.AreEqual(0, Volatile.Read(ref secondNotifications));
        }
        finally
        {
            Directory.Delete(folderPath, recursive: true);
        }
    }
}
