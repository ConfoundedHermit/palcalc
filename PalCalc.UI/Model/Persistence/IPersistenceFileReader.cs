using System.IO;

namespace PalCalc.UI.Model.Persistence
{
    /// <summary>
    /// Minimal filesystem boundary for loading persisted documents.
    /// </summary>
    internal interface IPersistenceFileReader
    {
        bool Exists(string path);

        string ReadAllText(string path);
    }

    /// <summary>
    /// Filesystem boundary for transactional persistence. It is injectable so tests can
    /// fail individual filesystem operations without touching user data.
    /// </summary>
    internal interface IPersistenceFileOperations : IPersistenceFileReader
    {
        Stream OpenWriteNew(string path);

        void FlushToDisk(Stream stream);

        void ReplaceFile(string sourcePath, string destinationPath, string backupPath);

        void MoveFile(string sourcePath, string destinationPath, bool overwrite);

        void DeleteFile(string path);
    }

    internal sealed class SystemPersistenceFileOperations : IPersistenceFileOperations
    {
        public static readonly SystemPersistenceFileOperations Instance = new();

        private SystemPersistenceFileOperations()
        {
        }

        public bool Exists(string path) => File.Exists(path);

        public string ReadAllText(string path) => File.ReadAllText(path);

        public Stream OpenWriteNew(string path) => new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);

        public void FlushToDisk(Stream stream)
        {
            if (stream is FileStream fileStream)
                fileStream.Flush(flushToDisk: true);
            else
                stream.Flush();
        }

        public void ReplaceFile(string sourcePath, string destinationPath, string backupPath) =>
            File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
            File.Move(sourcePath, destinationPath, overwrite);

        public void DeleteFile(string path) => File.Delete(path);
    }
}
