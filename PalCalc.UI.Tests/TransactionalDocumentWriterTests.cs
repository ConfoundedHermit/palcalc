using PalCalc.UI.Model.Persistence;

namespace PalCalc.UI.Tests;

[TestClass]
public class TransactionalDocumentWriterTests
{
    [TestMethod]
    public void Write_ReplacesExistingPrimaryAndRetainsLastKnownGoodBackup()
    {
        using var directory = new TemporaryDirectory();
        var primaryPath = directory.FilePath("settings.json");
        File.WriteAllText(primaryPath, "old complete document");

        TransactionalDocumentWriter.Write(primaryPath, "new complete document", value => value);

        Assert.AreEqual("new complete document", File.ReadAllText(primaryPath));
        Assert.AreEqual("old complete document", File.ReadAllText(primaryPath + RecoverableDocumentReader.BackupExtension));
        Assert.AreEqual(0, Directory.EnumerateFiles(directory.DirectoryPath, "*.tmp").Count());
    }

    [TestMethod]
    public void Write_WhenPromotionFails_PreservesPrimaryAndRetainsCompletedTemporaryDocument()
    {
        using var directory = new TemporaryDirectory();
        var primaryPath = directory.FilePath("settings.json");
        File.WriteAllText(primaryPath, "old complete document");
        var fileOperations = new ReplaceFailingFileOperations();

        var exception = Assert.ThrowsException<TransactionalDocumentWriteException>(
            () => TransactionalDocumentWriter.Write(primaryPath, "new complete document", value => value, fileOperations));

        Assert.AreEqual("old complete document", File.ReadAllText(primaryPath));
        Assert.IsTrue(File.Exists(exception.TemporaryPath));
        Assert.AreEqual("new complete document", File.ReadAllText(exception.TemporaryPath));
    }

    [TestMethod]
    public void Write_WhenReplaceIsUnsupported_UsesSameDirectoryMoveFallback()
    {
        using var directory = new TemporaryDirectory();
        var primaryPath = directory.FilePath("settings.json");
        File.WriteAllText(primaryPath, "old complete document");
        var fileOperations = new ReplaceUnsupportedFileOperations();

        TransactionalDocumentWriter.Write(primaryPath, "new complete document", value => value, fileOperations);

        Assert.IsTrue(fileOperations.MovedWithOverwrite);
        Assert.AreEqual("new complete document", File.ReadAllText(primaryPath));
    }

    [TestMethod]
    public void Write_WhenPrimaryDoesNotExist_MovesTemporaryDocumentIntoPlace()
    {
        using var directory = new TemporaryDirectory();
        var primaryPath = directory.FilePath("settings.json");

        TransactionalDocumentWriter.Write(primaryPath, "new complete document", value => value);

        Assert.AreEqual("new complete document", File.ReadAllText(primaryPath));
        Assert.IsFalse(File.Exists(primaryPath + RecoverableDocumentReader.BackupExtension));
    }

    [TestMethod]
    public void Write_WhenSerializationFails_DoesNotCreateTemporaryOrModifyPrimary()
    {
        using var directory = new TemporaryDirectory();
        var primaryPath = directory.FilePath("settings.json");
        File.WriteAllText(primaryPath, "old complete document");

        Assert.ThrowsException<InvalidOperationException>(
            () => TransactionalDocumentWriter.Write(primaryPath, "new document", _ => throw new InvalidOperationException("Injected serialization failure.")));

        Assert.AreEqual("old complete document", File.ReadAllText(primaryPath));
        Assert.AreEqual(0, Directory.EnumerateFiles(directory.DirectoryPath, "*.tmp").Count());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"palcalc-persistence-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(path);

        public string DirectoryPath => path;

        public string FilePath(string name) => System.IO.Path.Combine(path, name);

        public void Dispose()
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private class DelegatingFileOperations : IPersistenceFileOperations
    {
        public bool Exists(string path) => File.Exists(path);

        public string ReadAllText(string path) => File.ReadAllText(path);

        public Stream OpenWriteNew(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, options: FileOptions.WriteThrough);

        public void FlushToDisk(Stream stream) => ((FileStream)stream).Flush(flushToDisk: true);

        public virtual void ReplaceFile(string sourcePath, string destinationPath, string backupPath) =>
            File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);

        public virtual void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
            File.Move(sourcePath, destinationPath, overwrite);

        public void DeleteFile(string path) => File.Delete(path);
    }

    private sealed class ReplaceFailingFileOperations : DelegatingFileOperations
    {
        public override void ReplaceFile(string sourcePath, string destinationPath, string backupPath) =>
            throw new IOException("Injected replacement failure.");
    }

    private sealed class ReplaceUnsupportedFileOperations : DelegatingFileOperations
    {
        public bool MovedWithOverwrite { get; private set; }

        public override void ReplaceFile(string sourcePath, string destinationPath, string backupPath) =>
            throw new PlatformNotSupportedException("Injected unsupported replacement.");

        public override void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            MovedWithOverwrite = overwrite;
            base.MoveFile(sourcePath, destinationPath, overwrite);
        }
    }
}
