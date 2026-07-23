using Newtonsoft.Json;
using PalCalc.Model;
using PalCalc.SaveReader;
using PalCalc.UI.Model.Persistence;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PalCalc.UI.Model
{
    internal static class Storage
    {
        private static ILogger logger = Log.ForContext(typeof(Storage));

        public static event Action<ISaveGame> SaveReloaded;

        // (debug-only setting)
        public static readonly bool DEBUG_DisableStorage = false;

        private static string storageRootPath = null;
        private static bool appSettingsRecoveryPromptPending = false;

        public static string CachePath => PathUnderStorageRoot("cache");
        public static string SaveCachePath => $"{CachePath}/saves";
        public static string DataPath => PathUnderStorageRoot("data");

        public static string AppSettingsPath
        {
            get
            {
                Init();
                return $"{DataPath}/settings.json";
            }
        }

        // path for cached copy of save file data
        public static string SaveCachePathFor(ISaveGame forSaveFile)
        {
            Init();
            return $"{SaveCachePath}/{CachedSaveGame.IdentifierFor(forSaveFile)}.json";
        }

        // path for storing data associated with a specific save file
        public static string SaveFileDataPath(ISaveGame forSaveFile)
        {
            Init();
            var path = $"{DataPath}/{CachedSaveGame.IdentifierFor(forSaveFile)}";
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        public static string SaveFileTargetsDataPath(ISaveGame forSaveFile) => Path.Join(SaveFileDataPath(forSaveFile), "targets");

        // path for storing game-specific game settings (breeding time, etc.)
        public static string GameSettingsPath(ISaveGame forSaveFile)
        {
            Init();
            return SaveFileDataPath(forSaveFile) + "/game-settings.json";
        }

        public static string CustomContainerPath(ISaveGame forSaveFile)
        {
            Init();
            return SaveFileDataPath(forSaveFile) + "/custom-containers.json";
        }

        private static bool didInit = false;

        internal static IDisposable UseStorageRootForTests(string rootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            var previousRootPath = storageRootPath;
            var previousDidInit = didInit;
            var previousRecoveryPromptPending = appSettingsRecoveryPromptPending;
            storageRootPath = rootPath;
            didInit = false;
            appSettingsRecoveryPromptPending = false;
            return new StorageRootTestScope(previousRootPath, previousDidInit, previousRecoveryPromptPending);
        }

        public static void Init()
        {
            if (didInit) return;

            if (!Directory.Exists(CachePath)) Directory.CreateDirectory(CachePath);
            if (!Directory.Exists(SaveCachePath)) Directory.CreateDirectory(SaveCachePath);
            if (!Directory.Exists(DataPath)) Directory.CreateDirectory(DataPath);

            // migrate file locations from before beta-v0.5
            if (Directory.Exists($"{DataPath}/results"))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries($"{DataPath}/results"))
                {
                    var newPath = $"{DataPath}/{Path.GetFileName(entry)}";
                    if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory))
                    {
                        Directory.Move(entry, newPath);
                    }
                    else
                    {
                        File.Move(entry, newPath);
                    }
                }

                Directory.Delete($"{DataPath}/results");
            }

            didInit = true;
        }

        public static AppSettings LoadAppSettings()
        {
            if (DEBUG_DisableStorage) return new();

            var settingsPath = AppSettingsPath;
            var loaded = RecoverableDocumentReader.Read(settingsPath, DeserializeAppSettings);
            if (!loaded.IsSuccess)
            {
                if (loaded.PrimaryFailure is not null || loaded.BackupFailure is not null)
                {
                    appSettingsRecoveryPromptPending = true;
                    logger.Error(
                        "Unable to read app settings from the primary or backup document; using in-memory defaults. Primary failure: {hasPrimaryFailure}; backup failure: {hasBackupFailure}",
                        loaded.PrimaryFailure is not null,
                        loaded.BackupFailure is not null);
                }

                return new();
            }

            var res = loaded.Value;
            if (loaded.Source == PersistedDocumentSource.Backup)
            {
                RestoreAppSettingsPrimaryFromBackup(settingsPath, res, loaded.PrimaryFailure);
            }

            // remove duplicates caused by missing `ObjectCreationHandling` in older versions
            res.SolverSettings.BannedBredPalInternalNames = res.SolverSettings.BannedBredPalInternalNames.Distinct().ToList();
            res.SolverSettings.BannedWildPalInternalNames = res.SolverSettings.BannedWildPalInternalNames.Distinct().ToList();

            return res;
        }

        public static void SaveAppSettings(AppSettings settings) =>
            TransactionalDocumentWriter.Write(AppSettingsPath, settings, SerializeAppSettings);

        /// <summary>
        /// Reads a user-configured document and repairs its primary from a valid backup without
        /// discarding an unreadable primary. Callers decide how to handle a failure of both copies.
        /// </summary>
        internal static RecoverableDocumentReadResult<T> LoadUserDocument<T>(
            string path,
            Func<string, T> deserialize,
            Func<T, string> serialize,
            string documentDescription)
            where T : class
        {
            var loaded = RecoverableDocumentReader.Read(path, deserialize);
            if (!loaded.IsSuccess || loaded.Source != PersistedDocumentSource.Backup) return loaded;

            if (loaded.PrimaryFailure is not null)
            {
                try
                {
                    var diagnosticPath = RecoverableDocumentReader.PreserveFailedPrimary(path);
                    if (diagnosticPath is not null)
                        logger.Warning("Recovered {documentDescription} from backup; preserved failed primary at {diagnosticPath}", documentDescription, diagnosticPath);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Recovered {documentDescription} from backup but could not preserve the failed primary; leaving it untouched", documentDescription);
                    return loaded;
                }
            }

            try
            {
                TransactionalDocumentWriter.Write(path, loaded.Value, serialize);
                logger.Warning("Recovered {documentDescription} from backup and restored the primary document", documentDescription);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Recovered {documentDescription} from backup but could not restore the primary document", documentDescription);
            }

            return loaded;
        }

        internal static void SaveUserDocument<T>(string path, T document, Func<T, string> serialize) =>
            TransactionalDocumentWriter.Write(path, document, serialize);

        /// <summary>
        /// Retains a successfully migrated legacy document under a distinct name. It is never
        /// deleted as part of migration, so a user can recover it if the new document set fails.
        /// </summary>
        internal static void ArchiveMigratedUserDocument(string path)
        {
            if (!File.Exists(path)) return;

            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException($"Could not determine a directory for '{path}'.");
            var archivePath = Path.Combine(
                directory,
                $"{Path.GetFileName(fullPath)}.migrated-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
            SystemPersistenceFileOperations.Instance.MoveFile(fullPath, archivePath, overwrite: false);
        }

        internal static bool ConsumeAppSettingsRecoveryPrompt()
        {
            var result = appSettingsRecoveryPromptPending;
            appSettingsRecoveryPromptPending = false;
            return result;
        }

        /// <summary>
        /// Preserves both unreadable app-settings documents under diagnostic names, then writes
        /// a new default settings document. It is only called after explicit user confirmation.
        /// </summary>
        internal static void ResetAppSettingsAfterRecovery()
        {
            var settingsPath = AppSettingsPath;
            PreserveAppSettingsDocument(settingsPath);
            PreserveAppSettingsDocument(settingsPath + RecoverableDocumentReader.BackupExtension);
            SaveAppSettings(new AppSettings());
        }

        private static AppSettings DeserializeAppSettings(string json) =>
            JsonConvert.DeserializeObject<AppSettings>(
                json,
                // `SolverSettings.BannedWildPalInternalNames` has a non-empty-list default value; base Newtonsoft JSON
                // behavior is to merge the deserialized list with it, leading to duplicate entries.
                new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace }
            ) ?? new();

        private static string SerializeAppSettings(AppSettings settings) => JsonConvert.SerializeObject(settings);

        private static void RestoreAppSettingsPrimaryFromBackup(string settingsPath, AppSettings settings, Exception primaryFailure)
        {
            if (primaryFailure is not null)
            {
                try
                {
                    var diagnosticPath = RecoverableDocumentReader.PreserveFailedPrimary(settingsPath);
                    if (diagnosticPath is not null)
                        logger.Warning("Recovered app settings from backup; preserved failed primary at {diagnosticPath}", diagnosticPath);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Recovered app settings from backup but could not preserve the failed primary; leaving it untouched");
                    return;
                }
            }

            try
            {
                TransactionalDocumentWriter.Write(settingsPath, settings, SerializeAppSettings);
                logger.Warning("Recovered app settings from backup and restored the primary document");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Recovered app settings from backup but could not restore the primary document");
            }
        }

        private static void PreserveAppSettingsDocument(string path)
        {
            if (!File.Exists(path)) return;

            var diagnosticPath = RecoverableDocumentReader.PreserveFailedPrimary(path);
            logger.Warning("Preserved unreadable app settings document at {diagnosticPath}", diagnosticPath);
        }

        private static string PathUnderStorageRoot(string relativePath) =>
            storageRootPath is null ? relativePath : Path.Combine(storageRootPath, relativePath);

        private sealed class StorageRootTestScope(string previousRootPath, bool previousDidInit, bool previousRecoveryPromptPending) : IDisposable
        {
            public void Dispose()
            {
                storageRootPath = previousRootPath;
                didInit = previousDidInit;
                appSettingsRecoveryPromptPending = previousRecoveryPromptPending;
            }
        }

        public static void ClearForSave(ISaveGame save)
        {
            try
            {
                DiscardCacheDocuments(SaveCachePathFor(save));
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Unable to delete cache-file for {saveId}", save.GameId);
            }

            try
            {
                var dataPath = SaveFileDataPath(save);
                if (Directory.Exists(dataPath))
                    Directory.Delete(dataPath, true);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Unable to delete data-folder for {saveId}", save.GameId);
            }
        }

        /// <summary>
        /// Removes only regenerable cache data and stored breeding targets for a save. Unlike
        /// <see cref="ClearForSave"/>, this deliberately preserves user settings and custom Pals.
        /// </summary>
        public static void ClearCacheAndTargetsForSave(ISaveGame save)
        {
            try
            {
                DiscardCacheDocuments(SaveCachePathFor(save));
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Unable to delete cache-file for {saveId}", save.GameId);
            }

            try
            {
                var dataPath = SaveFileDataPath(save);
                var targetListPath = Path.Join(dataPath, "pal-target-ids.json");
                var legacyTargetListPath = Path.Join(dataPath, "pal-targets.json");
                var targetsPath = SaveFileTargetsDataPath(save);

                if (File.Exists(targetListPath)) File.Delete(targetListPath);
                if (File.Exists(legacyTargetListPath)) File.Delete(legacyTargetListPath);
                if (Directory.Exists(targetsPath)) Directory.Delete(targetsPath, true);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Unable to delete target data for {saveId}", save.GameId);
            }
        }

        public static SaveCustomizations LoadSaveCustomizations(ISaveGame forSaveGame, PalDB db)
        {
            if (DEBUG_DisableStorage) return new SaveCustomizations();

            var filePath = CustomContainerPath(forSaveGame);
            var converter = new PalInstanceJsonConverter(db);
            var loaded = LoadUserDocument(
                filePath,
                json => JsonConvert.DeserializeObject<SaveCustomizations>(json, converter),
                value => JsonConvert.SerializeObject(value, converter),
                "save customizations");
            if (!loaded.IsSuccess)
                logger.Warning(loaded.PrimaryFailure, "Unable to load save customizations for {label}; preserving unreadable documents", CachedSaveGame.IdentifierFor(forSaveGame));

            var res = loaded.Value ?? new SaveCustomizations();
            res.CustomContainers ??= [];
            return res;
        }

        public static void SaveCustomizations(ISaveGame forSaveGame, SaveCustomizations custom, PalDB db)
        {
            if (DEBUG_DisableStorage) return;

            var converter = new PalInstanceJsonConverter(db);
            SaveUserDocument(
                CustomContainerPath(forSaveGame),
                custom,
                value => JsonConvert.SerializeObject(value, converter)
            );
        }

        #region Cached Game Save Files

        private static Dictionary<string, CachedSaveGame> InMemorySaves = new Dictionary<string, CachedSaveGame>();

        // only loads the save if it has been cached, otherwise returns null
        public static CachedSaveGame LoadSaveFromCache(ISaveGame save, PalDB db)
        {
            Init();

            CrashSupport.ReferencedSave(save);

            if (DEBUG_DisableStorage) return null;

            var path = SaveCachePathFor(save);
            if (File.Exists(path) || File.Exists(path + RecoverableDocumentReader.BackupExtension))
            {
                var loaded = RecoverableDocumentReader.Read(path, json => CachedSaveGame.FromJson(json, db));
                if (!loaded.IsSuccess)
                {
                    logger.Error(loaded.PrimaryFailure, "Failed to load cached save-game data; discarding the regenerable cache");
                    DiscardCacheDocuments(path);
                    return null;
                }

                var res = loaded.Value;
                if (loaded.Source == PersistedDocumentSource.Backup)
                {
                    logger.Warning("Recovered cached save-game data from its backup; discarding the unreadable cache primary");
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                        TransactionalDocumentWriter.Write(path, res, cached => cached.ToJson(db));
                    }
                    catch (Exception ex)
                    {
                        logger.Warning(ex, "Recovered cached save-game data from backup but could not restore the primary cache document");
                    }
                }

                res.UnderlyingSave = save;

                CrashSupport.ReferencedCachedSave(res);
                return res;
            }
            else
            {
                return null;
            }
        }

        // loads the cached save data and updates it if it's outdated or not yet cached
        public static CachedSaveGame LoadSave(ISavesLocation containerLocation, ISaveGame save, PalDB db, GameSettings settings)
        {
            Init();

            CrashSupport.ReferencedSave(save);

            var path = SaveCachePathFor(save);
            if (!save.IsValid)
            {
                if (!DEBUG_DisableStorage && (File.Exists(path) || File.Exists(path + RecoverableDocumentReader.BackupExtension)))
                {
                    logger.Warning("cached save available but the save-game itself is invalid, deleting cached save for {savePath}", save.BasePath);
                    DiscardCacheDocuments(path);
                }
                return null;
            }

            var identifier = CachedSaveGame.IdentifierFor(save);

            lock (InMemorySaves)
            {
                if (InMemorySaves.ContainsKey(identifier)) return InMemorySaves[identifier];

                if (!DEBUG_DisableStorage && (File.Exists(path) || File.Exists(path + RecoverableDocumentReader.BackupExtension)))
                {
                    var res = LoadSaveFromCache(save, db);

                    if (res is null || !res.IsValid)
                    {
                        // TODO - no longer necessary? should have been covered by check at top of this method
                        // TODO - log
                        DiscardCacheDocuments(path);
                        return null;
                    }

                    if (res.IsOutdated(db))
                    {
                        DiscardCacheDocuments(path);
                        return LoadSave(containerLocation, save, db, settings);
                    }

                    InMemorySaves.Add(identifier, res);
                    return res;
                }
                else
                {
                    var res = CachedSaveGame.FromSaveGame(containerLocation, save, db, settings);
                    if (res != null)
                    {
                        CrashSupport.ReferencedCachedSave(res);

                        if (!DEBUG_DisableStorage)
                            TransactionalDocumentWriter.Write(path, res, cached => cached.ToJson(db));
                    }

                    // TODO - adding `null` entries will prevent re-adding a save at the same path until the app is restarted
                    if (InMemorySaves.ContainsKey(identifier))
                        InMemorySaves.Remove(identifier);

                    InMemorySaves.Add(identifier, res);
                    return res;
                }
            }
        }

        // Removes all data related to the save (in memory + on disk), but does _not_ remove
        // any related entries within AppSettings
        public static void RemoveSave(ISaveGame save)
        {
            lock (InMemorySaves)
                InMemorySaves.Remove(CachedSaveGame.IdentifierFor(save));

            CrashSupport.RemoveReferences(save);
            ClearForSave(save);
        }

        public static void ReloadSave(ISavesLocation containerLocation, ISaveGame save, PalDB db, GameSettings settings)
        {
            Init();

            if (save == null) return;

            CrashSupport.ReferencedSave(save);

            lock (InMemorySaves)
            {
                var identifier = CachedSaveGame.IdentifierFor(save);
                var originalCachedSave = InMemorySaves.GetValueOrDefault(identifier);

                if (originalCachedSave != null)
                {
                    CrashSupport.ReferencedCachedSave(originalCachedSave);
                    InMemorySaves.Remove(identifier);
                }

                var path = SaveCachePathFor(save);
                var wasStored = !DEBUG_DisableStorage && File.Exists(path);
                var backupPath = wasStored ? path + ".bak" : null;

                if (wasStored)
                {
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    File.Move(path, backupPath);
                }

                var newCachedSave = LoadSave(containerLocation, save, db, settings);

                if (newCachedSave == null)
                {
                    if (!DEBUG_DisableStorage && wasStored)
                    {
                        DiscardCacheDocuments(path);

                        File.Move(backupPath, path);
                    }

                    InMemorySaves[identifier] = originalCachedSave;
                }
                else
                {
                    if (!DEBUG_DisableStorage)
                    {
                        if (wasStored) File.Delete(backupPath);

                        if (originalCachedSave != null)
                            originalCachedSave.CopyFrom(newCachedSave);
                    }

                    InMemorySaves[identifier] = originalCachedSave ?? newCachedSave;

                    SaveReloaded?.Invoke(save);
                }
            }
        }

        #endregion

        private static void DiscardCacheDocuments(string cachePath)
        {
            foreach (var path in new[] { cachePath, cachePath + RecoverableDocumentReader.BackupExtension })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
