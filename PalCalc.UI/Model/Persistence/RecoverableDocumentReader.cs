using System;
using System.IO;

namespace PalCalc.UI.Model.Persistence
{
    internal enum PersistedDocumentSource
    {
        Primary,
        Backup,
    }

    /// <summary>
    /// Result of reading a primary persisted document and, if necessary, its backup.
    /// This reader never mutates either file; retention and diagnostics are decided by the caller.
    /// </summary>
    internal sealed class RecoverableDocumentReadResult<T>
        where T : class
    {
        public T Value { get; init; }

        public Nullable<PersistedDocumentSource> Source { get; init; }

        public Exception PrimaryFailure { get; init; }

        public Exception BackupFailure { get; init; }

        public bool IsSuccess => Value is not null;
    }

    internal static class RecoverableDocumentReader
    {
        public const string BackupExtension = ".bak";

        public static RecoverableDocumentReadResult<T> Read<T>(
            string primaryPath,
            Func<string, T> deserialize,
            IPersistenceFileReader fileReader = null)
            where T : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
            ArgumentNullException.ThrowIfNull(deserialize);

            fileReader ??= SystemPersistenceFileOperations.Instance;

            var (primaryValue, primaryFailure) = ReadCandidate(primaryPath, deserialize, fileReader);
            if (primaryValue is not null)
            {
                return new RecoverableDocumentReadResult<T>
                {
                    Value = primaryValue,
                    Source = PersistedDocumentSource.Primary,
                };
            }

            var (backupValue, backupFailure) = ReadCandidate(primaryPath + BackupExtension, deserialize, fileReader);
            return new RecoverableDocumentReadResult<T>
            {
                Value = backupValue,
                Source = backupValue is null ? null : PersistedDocumentSource.Backup,
                PrimaryFailure = primaryFailure,
                BackupFailure = backupFailure,
            };
        }

        /// <summary>
        /// Moves a failed primary aside without reading its contents. Callers should only invoke
        /// this after a successful backup read and before restoring a new primary.
        /// </summary>
        public static string PreserveFailedPrimary(string primaryPath, IPersistenceFileOperations fileOperations = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
            fileOperations ??= SystemPersistenceFileOperations.Instance;

            var fullPrimaryPath = Path.GetFullPath(primaryPath);
            if (!fileOperations.Exists(fullPrimaryPath)) return null;

            var directoryPath = Path.GetDirectoryName(fullPrimaryPath)
                ?? throw new InvalidOperationException($"Could not determine a directory for '{primaryPath}'.");
            var diagnosticPath = Path.Combine(
                directoryPath,
                $"{Path.GetFileName(fullPrimaryPath)}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");

            fileOperations.MoveFile(fullPrimaryPath, diagnosticPath, overwrite: false);
            return diagnosticPath;
        }

        private static (T Value, Exception Failure) ReadCandidate<T>(
            string path,
            Func<string, T> deserialize,
            IPersistenceFileReader fileReader)
            where T : class
        {
            if (!fileReader.Exists(path)) return (null, null);

            try
            {
                var value = deserialize(fileReader.ReadAllText(path));
                return value is null
                    ? (null, new InvalidDataException($"Persisted document '{path}' deserialized to null."))
                    : (value, null);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }
    }
}
