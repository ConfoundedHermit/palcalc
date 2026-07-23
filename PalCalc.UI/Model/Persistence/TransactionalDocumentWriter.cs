using System;
using System.IO;
using System.Text;

namespace PalCalc.UI.Model.Persistence
{
    /// <summary>
    /// The primary document was not changed, but a completed temporary document was retained
    /// after promotion failed so it can be diagnosed or retried by a future caller.
    /// </summary>
    internal sealed class TransactionalDocumentWriteException : IOException
    {
        public TransactionalDocumentWriteException(string primaryPath, string temporaryPath, Exception innerException)
            : base($"Unable to promote temporary document '{temporaryPath}' to '{primaryPath}'.", innerException)
        {
            PrimaryPath = primaryPath;
            TemporaryPath = temporaryPath;
        }

        public string PrimaryPath { get; }

        public string TemporaryPath { get; }
    }

    /// <summary>
    /// Writes a complete replacement document in the destination directory before promoting it.
    /// Existing documents use File.Replace to retain a last-known-good backup; unsupported
    /// replacement falls back to an atomic same-volume move with replacement.
    /// </summary>
    internal static class TransactionalDocumentWriter
    {
        private static readonly KeyedWriteCoordinator writeCoordinator = new();

        public static void Write<T>(
            string primaryPath,
            T document,
            Func<T, string> serialize,
            IPersistenceFileOperations fileOperations = null)
        {
            writeCoordinator.RunAsync(
                primaryPath,
                () => WriteCore(primaryPath, document, serialize, fileOperations)
            ).GetAwaiter().GetResult();
        }

        private static void WriteCore<T>(
            string primaryPath,
            T document,
            Func<T, string> serialize,
            IPersistenceFileOperations fileOperations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
            ArgumentNullException.ThrowIfNull(serialize);

            // Serialization must complete before a primary document or temporary path is touched.
            var serialized = serialize(document);
            ArgumentNullException.ThrowIfNull(serialized);

            fileOperations ??= SystemPersistenceFileOperations.Instance;

            var fullPrimaryPath = Path.GetFullPath(primaryPath);
            var directoryPath = Path.GetDirectoryName(fullPrimaryPath)
                ?? throw new InvalidOperationException($"Could not determine a directory for '{primaryPath}'.");
            var temporaryPath = Path.Combine(directoryPath, $".{Path.GetFileName(fullPrimaryPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var stream = fileOperations.OpenWriteNew(temporaryPath))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true))
                {
                    writer.Write(serialized);
                    writer.Flush();
                    fileOperations.FlushToDisk(stream);
                }
            }
            catch
            {
                TryDeleteTemporaryFile(temporaryPath, fileOperations);
                throw;
            }

            try
            {
                PromoteTemporaryFile(temporaryPath, fullPrimaryPath, fileOperations);
            }
            catch (Exception ex)
            {
                // Do not clean this up: a completed temporary document is useful for recovery.
                throw new TransactionalDocumentWriteException(fullPrimaryPath, temporaryPath, ex);
            }
        }

        private static void PromoteTemporaryFile(string temporaryPath, string primaryPath, IPersistenceFileOperations fileOperations)
        {
            if (!fileOperations.Exists(primaryPath))
            {
                fileOperations.MoveFile(temporaryPath, primaryPath, overwrite: false);
                return;
            }

            try
            {
                fileOperations.ReplaceFile(temporaryPath, primaryPath, primaryPath + RecoverableDocumentReader.BackupExtension);
            }
            catch (NotSupportedException)
            {
                // Same-directory move stays on the same volume. A pre-existing .bak remains the
                // last known-good backup when this filesystem cannot perform File.Replace.
                fileOperations.MoveFile(temporaryPath, primaryPath, overwrite: true);
            }
            catch (FileNotFoundException)
            {
                // The primary disappeared after Exists; safely promote the complete temp file.
                fileOperations.MoveFile(temporaryPath, primaryPath, overwrite: false);
            }
        }

        private static void TryDeleteTemporaryFile(string temporaryPath, IPersistenceFileOperations fileOperations)
        {
            try
            {
                if (fileOperations.Exists(temporaryPath)) fileOperations.DeleteFile(temporaryPath);
            }
            catch
            {
                // The original write failure is more actionable. A failed cleanup leaves only a
                // partial temp file; it never affects the readable primary document.
            }
        }
    }
}
