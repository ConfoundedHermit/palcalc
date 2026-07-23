using Newtonsoft.Json;
using PalCalc.UI.Model;
using PalCalc.UI.Model.Persistence;

namespace PalCalc.UI.Tests;

[TestClass]
[DoNotParallelize]
public class UserDocumentStorageTests
{
    [TestMethod]
    public void LoadUserDocument_CorruptPrimaryRecoversFromBackupAndRestoresPrimary()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        Directory.CreateDirectory(Storage.DataPath);
        var path = System.IO.Path.Combine(Storage.DataPath, "target.json");
        File.WriteAllText(path, "{ malformed target");
        File.WriteAllText(path + RecoverableDocumentReader.BackupExtension, JsonConvert.SerializeObject(new TestDocument("backup")));

        var result = Storage.LoadUserDocument(
            path,
            json => JsonConvert.DeserializeObject<TestDocument>(json)!,
            document => JsonConvert.SerializeObject(document),
            "test target");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(PersistedDocumentSource.Backup, result.Source);
        Assert.AreEqual("backup", result.Value!.Name);
        Assert.AreEqual("backup", JsonConvert.DeserializeObject<TestDocument>(File.ReadAllText(path))!.Name);
        Assert.AreEqual("{ malformed target", File.ReadAllText(Directory.EnumerateFiles(Storage.DataPath, "target.json.corrupt-*").Single()));
    }

    [TestMethod]
    public void ArchiveMigratedUserDocument_RetainsOriginalUnderMigrationDiagnosticName()
    {
        using var directory = new TemporaryDirectory();
        using var storage = Storage.UseStorageRootForTests(directory.Path);
        Directory.CreateDirectory(Storage.DataPath);
        var path = System.IO.Path.Combine(Storage.DataPath, "pal-targets.json");
        File.WriteAllText(path, "legacy target document");

        Storage.ArchiveMigratedUserDocument(path);

        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual("legacy target document", File.ReadAllText(Directory.EnumerateFiles(Storage.DataPath, "pal-targets.json.migrated-*").Single()));
    }

    private sealed record TestDocument(string Name);

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"palcalc-user-document-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(path);

        public string Path => path;

        public void Dispose()
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
