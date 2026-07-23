using Newtonsoft.Json;
using PalCalc.UI.Model.Persistence;

namespace PalCalc.UI.Tests;

[TestClass]
public class RecoverableDocumentReaderTests
{
    [TestMethod]
    public void Read_UsesValidPrimaryBeforeBackup()
    {
        using var directory = new TemporaryDirectory();
        var primaryPath = directory.FilePath("settings.json");
        File.WriteAllText(primaryPath, JsonConvert.SerializeObject(new TestDocument("primary")));
        File.WriteAllText(primaryPath + RecoverableDocumentReader.BackupExtension, JsonConvert.SerializeObject(new TestDocument("backup")));

        var result = ReadDocument(primaryPath);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(PersistedDocumentSource.Primary, result.Source);
        Assert.AreEqual("primary", result.Value!.Name);
        Assert.IsNull(result.PrimaryFailure);
    }

    [TestMethod]
    public void Read_FallsBackToBackupWhenPrimaryIsCorrupt()
    {
        using var directory = new TemporaryDirectory();
        var primaryPath = directory.FilePath("settings.json");
        File.WriteAllText(primaryPath, "{ not valid json");
        File.WriteAllText(primaryPath + RecoverableDocumentReader.BackupExtension, JsonConvert.SerializeObject(new TestDocument("backup")));

        var result = ReadDocument(primaryPath);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(PersistedDocumentSource.Backup, result.Source);
        Assert.AreEqual("backup", result.Value!.Name);
        Assert.IsNotNull(result.PrimaryFailure);
        Assert.IsTrue(File.Exists(primaryPath), "Recovery must not delete the failed primary before retention policy is decided.");
    }

    [TestMethod]
    public void Read_FallsBackToBackupWhenPrimaryReadFails()
    {
        using var directory = new TemporaryDirectory();
        var primaryPath = directory.FilePath("settings.json");
        File.WriteAllText(primaryPath, JsonConvert.SerializeObject(new TestDocument("primary")));
        File.WriteAllText(primaryPath + RecoverableDocumentReader.BackupExtension, JsonConvert.SerializeObject(new TestDocument("backup")));

        var result = RecoverableDocumentReader.Read(
            primaryPath,
            DeserializeDocument,
            new PrimaryReadFailureFileReader(primaryPath));

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(PersistedDocumentSource.Backup, result.Source);
        Assert.AreEqual("backup", result.Value!.Name);
        Assert.IsInstanceOfType<IOException>(result.PrimaryFailure);
    }

    [TestMethod]
    public void Read_ReportsFailureWhenNeitherDocumentIsValid()
    {
        using var directory = new TemporaryDirectory();
        var primaryPath = directory.FilePath("settings.json");
        File.WriteAllText(primaryPath, "not json");
        File.WriteAllText(primaryPath + RecoverableDocumentReader.BackupExtension, "also not json");

        var result = ReadDocument(primaryPath);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Source);
        Assert.IsNotNull(result.PrimaryFailure);
        Assert.IsNotNull(result.BackupFailure);
    }

    private static RecoverableDocumentReadResult<TestDocument> ReadDocument(string primaryPath) =>
        RecoverableDocumentReader.Read(primaryPath, DeserializeDocument);

    private static TestDocument DeserializeDocument(string json) => JsonConvert.DeserializeObject<TestDocument>(json)!;

    private sealed record TestDocument(string Name);

    private sealed class PrimaryReadFailureFileReader(string failingPath) : IPersistenceFileReader
    {
        public bool Exists(string path) => File.Exists(path);

        public string ReadAllText(string path)
        {
            if (StringComparer.Ordinal.Equals(path, failingPath))
                throw new IOException("Injected primary read failure.");

            return File.ReadAllText(path);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"palcalc-persistence-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(path);

        public string FilePath(string name) => Path.Combine(path, name);

        public void Dispose()
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
