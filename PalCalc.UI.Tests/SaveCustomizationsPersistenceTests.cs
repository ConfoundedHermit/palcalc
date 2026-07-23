using Newtonsoft.Json;
using PalCalc.Model;
using PalCalc.SaveReader;
using PalCalc.SaveReader.SaveFile;
using PalCalc.UI.Model;
using PalCalc.UI.Model.Persistence;

namespace PalCalc.UI.Tests;

[TestClass]
[DoNotParallelize]
public class SaveCustomizationsPersistenceTests
{
    [TestMethod]
    public void Load_LoadsExistingPrimaryOnlyDocument()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        using var save = new TestSave("user", "save");
        var db = PalDB.LoadEmbedded();
        var path = Storage.CustomContainerPath(save);
        File.WriteAllText(path, JsonConvert.SerializeObject(new SaveCustomizations(), new PalInstanceJsonConverter(db)));

        var actual = Storage.LoadSaveCustomizations(save, db);

        Assert.AreEqual(0, actual.CustomContainers.Count);
        Assert.IsFalse(File.Exists(path + RecoverableDocumentReader.BackupExtension));
    }

    [TestMethod]
    public void Load_CorruptPrimaryRecoversFromBackupAndPreservesDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        using var save = new TestSave("user", "save");
        var db = PalDB.LoadEmbedded();
        var path = Storage.CustomContainerPath(save);
        var backup = JsonConvert.SerializeObject(new SaveCustomizations(), new PalInstanceJsonConverter(db));
        File.WriteAllText(path, "{ malformed customizations");
        File.WriteAllText(path + RecoverableDocumentReader.BackupExtension, backup);

        var actual = Storage.LoadSaveCustomizations(save, db);

        Assert.AreEqual(0, actual.CustomContainers.Count);
        Assert.AreEqual(backup, File.ReadAllText(path));
        Assert.AreEqual("{ malformed customizations", File.ReadAllText(Directory.EnumerateFiles(directory.DataPath, "custom-containers.json.corrupt-*", SearchOption.AllDirectories).Single()));
    }

    [TestMethod]
    public void Save_ReplacesExistingPrimaryAndRetainsBackup()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        using var save = new TestSave("user", "save");
        var db = PalDB.LoadEmbedded();
        var path = Storage.CustomContainerPath(save);
        File.WriteAllText(path, "{}");

        Storage.SaveCustomizations(save, new SaveCustomizations(), db);

        Assert.AreEqual("{}", File.ReadAllText(path + RecoverableDocumentReader.BackupExtension));
        Assert.AreEqual(0, Storage.LoadSaveCustomizations(save, db).CustomContainers.Count);
    }

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
        public event Action<ISaveGame>? Updated { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"palcalc-customizations-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(path);
        public string Path => path;
        public string DataPath => System.IO.Path.Combine(path, "data");
        public void Dispose()
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
