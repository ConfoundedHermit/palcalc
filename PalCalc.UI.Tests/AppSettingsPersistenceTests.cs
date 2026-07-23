using Newtonsoft.Json;
using PalCalc.UI.Model;
using PalCalc.UI.Model.Persistence;

namespace PalCalc.UI.Tests;

[TestClass]
[DoNotParallelize]
public class AppSettingsPersistenceTests
{
    [TestMethod]
    public void LoadAppSettings_LoadsExistingPrimaryOnlyDocument()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        var expected = new AppSettings { Theme = AppTheme.Light };
        File.WriteAllText(Storage.AppSettingsPath, JsonConvert.SerializeObject(expected));

        var actual = Storage.LoadAppSettings();

        Assert.AreEqual(AppTheme.Light, actual.Theme);
        Assert.IsFalse(File.Exists(Storage.AppSettingsPath + RecoverableDocumentReader.BackupExtension));
    }

    [TestMethod]
    public void LoadAppSettings_CorruptPrimaryRecoversFromBackupAndPreservesDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        var backup = new AppSettings { Theme = AppTheme.Light };
        File.WriteAllText(Storage.AppSettingsPath, "{ malformed settings");
        File.WriteAllText(
            Storage.AppSettingsPath + RecoverableDocumentReader.BackupExtension,
            JsonConvert.SerializeObject(backup));

        var actual = Storage.LoadAppSettings();

        Assert.AreEqual(AppTheme.Light, actual.Theme);
        Assert.AreEqual(AppTheme.Light, JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(Storage.AppSettingsPath))!.Theme);
        Assert.AreEqual("{ malformed settings", File.ReadAllText(Directory.EnumerateFiles(directory.DataPath, "settings.json.corrupt-*").Single()));
        Assert.AreEqual(AppTheme.Light, JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(Storage.AppSettingsPath + RecoverableDocumentReader.BackupExtension))!.Theme);
    }

    [TestMethod]
    public void SaveAppSettings_UsesTransactionalPrimaryAndBackup()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        File.WriteAllText(Storage.AppSettingsPath, JsonConvert.SerializeObject(new AppSettings { Theme = AppTheme.Dark }));

        Storage.SaveAppSettings(new AppSettings { Theme = AppTheme.Light });

        Assert.AreEqual(AppTheme.Light, JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(Storage.AppSettingsPath))!.Theme);
        Assert.AreEqual(AppTheme.Dark, JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(Storage.AppSettingsPath + RecoverableDocumentReader.BackupExtension))!.Theme);
    }

    [TestMethod]
    public void ResetAppSettingsAfterRecovery_PreservesBothUnreadableDocumentsAndWritesDefaults()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        File.WriteAllText(Storage.AppSettingsPath, "not valid primary settings");
        File.WriteAllText(Storage.AppSettingsPath + RecoverableDocumentReader.BackupExtension, "not valid backup settings");

        var loaded = Storage.LoadAppSettings();
        var shouldPrompt = Storage.ConsumeAppSettingsRecoveryPrompt();
        Storage.ResetAppSettingsAfterRecovery();

        Assert.AreEqual(AppTheme.Dark, loaded.Theme);
        Assert.IsTrue(shouldPrompt);
        Assert.AreEqual(AppTheme.Dark, JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(Storage.AppSettingsPath))!.Theme);
        Assert.AreEqual("not valid primary settings", File.ReadAllText(Directory.EnumerateFiles(directory.DataPath, "settings.json.corrupt-*").Single()));
        Assert.AreEqual("not valid backup settings", File.ReadAllText(Directory.EnumerateFiles(directory.DataPath, "settings.json.bak.corrupt-*").Single()));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"palcalc-app-settings-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(path);

        public string Path => path;

        public string DataPath => System.IO.Path.Combine(path, "data");

        public void Dispose()
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
