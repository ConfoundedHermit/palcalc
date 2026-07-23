using PalCalc.Model;
using PalCalc.SaveReader;
using PalCalc.SaveReader.SaveFile;
using PalCalc.UI.Model;
using PalCalc.UI.Model.Persistence;
using PalCalc.UI.ViewModel.Mapped;

namespace PalCalc.UI.Tests;

[TestClass]
[DoNotParallelize]
public class GameSettingsPersistenceTests
{
    [TestMethod]
    public void Load_LoadsExistingPrimaryOnlyDocument()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        using var save = new TestSave("user", "save");
        var expected = CreateSettings(123);
        File.WriteAllText(Storage.GameSettingsPath(save), expected.ToJson());

        var actual = GameSettingsViewModel.Load(save);

        Assert.AreEqual(123, actual.BreedingTimeSeconds);
        Assert.IsFalse(File.Exists(Storage.GameSettingsPath(save) + RecoverableDocumentReader.BackupExtension));
    }

    [TestMethod]
    public void Load_CorruptPrimaryRecoversFromBackupAndPreservesDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        using var save = new TestSave("user", "save");
        var path = Storage.GameSettingsPath(save);
        File.WriteAllText(path, "{ malformed game settings");
        File.WriteAllText(path + RecoverableDocumentReader.BackupExtension, CreateSettings(456).ToJson());

        var actual = GameSettingsViewModel.Load(save);

        Assert.AreEqual(456, actual.BreedingTimeSeconds);
        Assert.AreEqual(456, GameSettingsViewModel.FromJson(File.ReadAllText(path)).BreedingTimeSeconds);
        Assert.AreEqual("{ malformed game settings", File.ReadAllText(Directory.EnumerateFiles(directory.DataPath, "game-settings.json.corrupt-*", SearchOption.AllDirectories).Single()));
    }

    [TestMethod]
    public void Save_ReplacesExistingPrimaryAndRetainsBackup()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        using var save = new TestSave("user", "save");
        var path = Storage.GameSettingsPath(save);
        File.WriteAllText(path, CreateSettings(60).ToJson());

        CreateSettings(789).Save(save);

        Assert.AreEqual(789, GameSettingsViewModel.FromJson(File.ReadAllText(path)).BreedingTimeSeconds);
        Assert.AreEqual(60, GameSettingsViewModel.FromJson(File.ReadAllText(path + RecoverableDocumentReader.BackupExtension)).BreedingTimeSeconds);
    }

    private static GameSettingsViewModel CreateSettings(int breedingTimeSeconds) =>
        new(GameSettings.Defaults) { BreedingTimeSeconds = breedingTimeSeconds };

    private sealed class TestSave(string userId, string gameId) : ISaveGame
    {
        public string BasePath => null!;
        public string UserId => userId;
        public string GameId => gameId;
        public DateTime LastModified => DateTime.UtcNow;
        public LevelSaveFile Level => null!;
        public LevelMetaSaveFile LevelMeta => null!;
        public LocalDataSaveFile LocalData => null!;
        public WorldOptionSaveFile WorldOption => null!;
        public List<PlayersSaveFile> Players => [];
        public IEnumerable<SaveFileLocation> RawFiles => [];
        public bool IsValid => true;
        public bool IsLocal => false;
        public event Action<ISaveGame>? Updated
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"palcalc-game-settings-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(path);

        public string Path => path;

        public string DataPath => System.IO.Path.Combine(path, "data");

        public void Dispose()
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
