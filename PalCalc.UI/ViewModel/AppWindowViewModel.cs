using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PalCalc.Model;
using PalCalc.SaveReader;
using PalCalc.UI.Localization;
using PalCalc.UI.Model;
using PalCalc.UI.Model.Service;
using PalCalc.UI.View;
using PalCalc.UI.ViewModel.Mapped;
using PalCalc.UI.ViewModel.Mapped.Saves;
using PalCalc.UI.ViewModel.SaveSelection;
using PalCalc.UI.ViewModel.Solver;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

using AdonisMessageBox = AdonisUI.Controls.MessageBox;
using AdonisMessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using AdonisMessageBoxResult = AdonisUI.Controls.MessageBoxResult;

namespace PalCalc.UI.ViewModel
{
    internal partial class AppWindowViewModel : ObservableObject
    {
        private static ILogger logger = Log.ForContext<AppWindowViewModel>();

        private AppSettings settings;
        private ISavesService savesService;
        private PalDB db;
        private Dispatcher dispatcher;
        private readonly List<ISaveGame> failedToLoadSaves = [];

        [ObservableProperty]
        private bool showToolbar = false;

        private AppToolbarViewModel toolbarVM;
        public AppToolbarViewModel ToolbarVM => toolbarVM;

        private bool checkedUpdates;

        public AppWindowViewModel(Dispatcher dispatcher)
        {
            AppSettings.Current = settings = Storage.LoadAppSettings();
            savesService = new AppSettingsSaveService(settings);
            checkedUpdates = false;
            this.dispatcher = dispatcher;

            Translator.CurrentLocale = settings.Locale;

            Translator.LocaleUpdated += () =>
            {
                if (settings.Locale != Translator.CurrentLocale)
                {
                    settings.Locale = Translator.CurrentLocale;
                    Storage.SaveAppSettings(settings);
                }
            };

            toolbarVM = new AppToolbarViewModel(dispatcher);

            CachedSaveGame.SaveFileLoadError += CachedSaveGame_SaveFileLoadError;

            RemoveMissingManualSaveLocations();
            BeginNavigateSaveSelectionPage();
        }

        private void RemoveMissingManualSaveLocations()
        {
            var remainingLocations = settings.ExtraSaveLocations
                .Where(location =>
                {
                    if (Directory.Exists(location)) return true;

                    Storage.ClearForSave(new StandardSaveGame(location));
                    return false;
                })
                .ToList();

            if (remainingLocations.Count == settings.ExtraSaveLocations.Count) return;

            settings.ExtraSaveLocations = remainingLocations;
            Storage.SaveAppSettings(settings);
        }

        private bool CanBeginNavigateSaveSelectionPage() => Content is SolverPage;

        [RelayCommand(CanExecute = nameof(CanBeginNavigateSaveSelectionPage))]
        private void BeginNavigateSaveSelectionPage()
        {
            var loadingPage = new LoadingPage();
            Content = loadingPage;
            ShowToolbar = false;

            dispatcher.BeginInvoke(() =>
            {
                Task.Run(() =>
                {
                    try
                    {
                        // TODO - Move startup loading/detection orchestration into dedicated services.
                        var databaseTask = Task.Run(() =>
                        {
                            var loadedDb = PalDB.LoadEmbedded();
                            PalBreedingDB.BeginLoadEmbedded(loadedDb);
                            return loadedDb;
                        });
                        var savesTask = Task.Run(() => SavesCollectionViewModel.DetectAll(settings, savesService));

                        Task.WaitAll(databaseTask, savesTask);
                        db = databaseTask.Result;
                        var saves = savesTask.Result;
                        App.Current.Dispatcher.BeginInvoke(() =>
                        {
                            NavigateSaveSelectionPage(saves);
                            ShowToolbar = true;

                            if (!checkedUpdates)
                            {
                                RunStartupUpdatesCheck();
                                checkedUpdates = true;
                            }
                        }, DispatcherPriority.ContextIdle);
                    }
                    catch (Exception e)
                    {
                        // (exceptions in Tasks are handled differently - re-send exceptions on UI Dispatcher so it gets handled like a normal error)
                        dispatcher.BeginInvoke(() =>
                        {
                            throw new Exception("An error occurred while detecting available saves", e);
                        });
                    }
                });
            }, DispatcherPriority.ContextIdle);
        }

        private void RunStartupUpdatesCheck()
        {
            Task.Run(async () =>
            {
                var result = await AppUpdates.CheckForUpdates();
                if (result.Status != AppUpdateCheckStatus.UpdateAvailable)
                    return;

                var newVersion = result.Version;

                if (settings.SkippedAppVersion == newVersion.Version)
                    return;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                dispatcher.BeginInvoke(
                    () => AppUpdates.PromptUpdateDownload(newVersion),
                    DispatcherPriority.ContextIdle
                );
#pragma warning restore CS4014
            });
        }

        private void NavigateSaveSelectionPage(IEnumerable<SavesCollectionViewModel> collections)
        {
            var vm = new SaveSelectionPageViewModel(
                savesCollections: collections,
                loadSaveCommand: new RelayCommand<SaveGameViewModel>(NavigateSolverPage)
            );

            var page = new SaveSelectionPage();
            if (settings.SelectedGameIdentifier != null)
            {
                vm.TrySelectSaveByIdentifier(settings.SelectedGameIdentifier);
            }

            page.DataContext = vm;

            Content = page;
            PromptRecoverAppSettings();
        }

        private void NavigateSolverPage(SaveGameViewModel selectedSave)
        {
            settings.SelectedGameIdentifier = CachedSaveGame.IdentifierFor(selectedSave.Value);
            Storage.SaveAppSettings(settings);
            CrashSupport.ReferencedSave(selectedSave.Value);

            var parsedSave = Storage.LoadSave(selectedSave.Parent.SourceLocation, selectedSave.Value, db, GameSettingsViewModel.Load(selectedSave.Value).ModelObject);
            if (parsedSave == null)
                return;

            var targets = LoadPalTargets(selectedSave);
            PromptRecoverFailedSaves();

            var saveOperations = new CommonSaveOperationsViewModel(BeginNavigateSaveSelectionPageCommand, selectedSave.Parent, selectedSave);
            var vm = new SolverPageViewModel(Dispatcher.CurrentDispatcher, saveOperations, selectedSave, targets);
            Content = new SolverPage(vm);
        }

        private PalTargetListViewModel LoadPalTargets(SaveGameViewModel sg)
        {
            if (Storage.DEBUG_DisableStorage)
                return new PalTargetListViewModel(new PalSourceViewModel(sg, null));

            try
            {
                var gameSettings = GameSettingsViewModel.Load(sg.Value).ModelObject;
                var originalCachedSave = Storage.LoadSaveFromCache(sg.Value, db);
                var dataPath = Storage.SaveFileDataPath(sg.Value);
                var targetsFolder = Storage.SaveFileTargetsDataPath(sg.Value);
                var legacyTargetsPath = Path.Join(dataPath, "pal-targets.json");
                var targetIdsPath = Path.Join(dataPath, "pal-target-ids.json");

                if (File.Exists(legacyTargetsPath))
                {
                    Directory.CreateDirectory(targetsFolder);
                    var vmEntryConverter = new PalSpecifierViewModelConverter(db, gameSettings, originalCachedSave);
                    var legacy = Storage.LoadUserDocument(
                        legacyTargetsPath,
                        json => JsonConvert.DeserializeObject<JObject>(json) ?? throw new InvalidDataException("Legacy target list deserialized to null."),
                        value => value.ToString(Formatting.None),
                        "legacy target list");
                    if (!legacy.IsSuccess)
                        throw new InvalidDataException("Neither primary nor backup legacy target list could be loaded.");

                    var oldTargets = legacy.Value["Targets"]?.ToObject<List<PalSpecifierViewModel>>(
                        JsonSerializer.Create(new JsonSerializerSettings { Converters = [vmEntryConverter] })) ?? [];
                    foreach (var target in oldTargets)
                    {
                        Storage.SaveUserDocument(
                            Path.Join(targetsFolder, $"{target.Id}.json"),
                            target,
                            item => JsonConvert.SerializeObject(item, vmEntryConverter));
                    }

                    var result = new PalTargetListViewModel(new PalSourceViewModel(sg, null), oldTargets);
                    Storage.SaveUserDocument(
                        targetIdsPath,
                        result,
                        item => JsonConvert.SerializeObject(item,
                            new PalTargetListViewModelConverter(db, gameSettings, sg, originalCachedSave, oldTargets.ToDictionary(t => t.Id))));
                    Storage.ArchiveMigratedUserDocument(legacyTargetsPath);
                    return result;
                }

                if (!File.Exists(targetIdsPath))
                    return new PalTargetListViewModel(new PalSourceViewModel(sg, null));

                var targetFiles = Directory.Exists(targetsFolder) ? Directory.EnumerateFiles(targetsFolder, "*.json") : [];
                var entryConverter = new PalSpecifierViewModelConverter(db, gameSettings, originalCachedSave);
                var targetEntries = targetFiles.Select(path =>
                {
                    var target = Storage.LoadUserDocument(
                        path,
                        json => JsonConvert.DeserializeObject<PalSpecifierViewModel>(json, entryConverter),
                        value => JsonConvert.SerializeObject(value, entryConverter),
                        "target");
                    if (target.IsSuccess) return target.Value;

                    logger.Warning("Unable to load target for {saveId} at {path}; primary failure: {hasPrimaryFailure}; backup failure: {hasBackupFailure}",
                        CachedSaveGame.IdentifierFor(sg.Value), path, target.PrimaryFailure is not null, target.BackupFailure is not null);
                    RecordFailedSaveLoad(sg.Value);
                    return null;
                }).SkipNull().ToList();

                var converter = new PalTargetListViewModelConverter(db, gameSettings, sg, originalCachedSave, targetEntries.ToDictionary(e => e.Id));
                var targetList = Storage.LoadUserDocument(
                    targetIdsPath,
                    json => JsonConvert.DeserializeObject<PalTargetListViewModel>(json, [converter]),
                    value => JsonConvert.SerializeObject(value, converter),
                    "target list");
                if (!targetList.IsSuccess)
                    throw new InvalidDataException("Neither primary nor backup target list could be loaded.");

                return targetList.Value;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "failed to load target data for {saveId}", CachedSaveGame.IdentifierFor(sg.Value));
                RecordFailedSaveLoad(sg.Value);
                return new PalTargetListViewModel(new PalSourceViewModel(sg, null));
            }
        }

        private void RecordFailedSaveLoad(ISaveGame save)
        {
            lock (failedToLoadSaves)
                failedToLoadSaves.Add(save);
        }

        private void PromptRecoverAppSettings()
        {
            if (!Storage.ConsumeAppSettingsRecoveryPrompt()) return;

            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var owner = App.Current.MainWindow;
                    var title = LocalizationCodes.LC_APP_SETTINGS_RECOVERY_TITLE.Bind().Value;
                    var message = LocalizationCodes.LC_APP_SETTINGS_RECOVERY_MESSAGE.Bind().Value;
                    var result = owner is not null
                        ? AdonisMessageBox.Show(owner, message, title, AdonisMessageBoxButton.YesNo)
                        : AdonisMessageBox.Show(message, title, AdonisMessageBoxButton.YesNo);

                    if (result == AdonisMessageBoxResult.Yes)
                    {
                        Storage.ResetAppSettingsAfterRecovery();
                        logger.Information("reset app settings after explicit user confirmation");
                    }

                    var openFolderMessage = LocalizationCodes.LC_APP_SETTINGS_RECOVERY_OPEN_FOLDER.Bind().Value;
                    var openFolder = owner is not null
                        ? AdonisMessageBox.Show(owner, openFolderMessage, title, AdonisMessageBoxButton.YesNo)
                        : AdonisMessageBox.Show(openFolderMessage, title, AdonisMessageBoxButton.YesNo);
                    if (openFolder == AdonisMessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Path.GetDirectoryName(Storage.AppSettingsPath),
                            UseShellExecute = true,
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "failed to complete app settings recovery prompt");
                }
            });
        }

        private void PromptRecoverFailedSaves()
        {
            List<ISaveGame> failed;
            lock (failedToLoadSaves)
            {
                if (failedToLoadSaves.Count == 0) return;
                failed = failedToLoadSaves.Distinct().ToList();
            }

            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var names = string.Join("\n", failed.Select(save => " - " + CachedSaveGame.IdentifierFor(save)));
                    var message =
                        "Pal Calc could not load stored data for the following save(s):\n\n" + names + "\n\n" +
                        "Would you like to reset Pal Calc's cached and target data for these save(s)? " +
                        "Your Palworld saves, Pal Calc settings, and custom Pals will NOT be modified. " +
                        "You will need to re-enter any breeding targets for the affected saves.";
                    var owner = App.Current.MainWindow;
                    var result = owner is not null
                        ? AdonisMessageBox.Show(owner, message, "Some saves could not be loaded", AdonisMessageBoxButton.YesNo)
                        : AdonisMessageBox.Show(message, "Some saves could not be loaded", AdonisMessageBoxButton.YesNo);
                    if (result != AdonisMessageBoxResult.Yes) return;

                    foreach (var save in failed)
                    {
                        try
                        {
                            Storage.ClearCacheAndTargetsForSave(save);
                            logger.Information("cleared cached/target data for {saveId} at user request", CachedSaveGame.IdentifierFor(save));
                        }
                        catch (Exception ex)
                        {
                            logger.Warning(ex, "failed to clear data for {saveId} during recovery", CachedSaveGame.IdentifierFor(save));
                        }
                    }

                    lock (failedToLoadSaves)
                        failedToLoadSaves.Clear();

                    AdonisMessageBox.Show(owner ?? App.Current.MainWindow, "The affected save data has been reset. Please restart Pal Calc.", "Reset complete");
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "failed to prompt for failed-save recovery");
                }
            });
        }

        private void CachedSaveGame_SaveFileLoadError(ISaveGame obj, Exception ex)
        {
            logger.Error(ex, "error when parsing save file for {saveId}", CachedSaveGame.IdentifierFor(obj));

            var saveId = CachedSaveGame.IdentifierFor(obj);
            if (settings.SelectedGameIdentifier == saveId)
            {
                settings.SelectedGameIdentifier = null;
                Storage.SaveAppSettings(settings);
                logger.Information("cleared failed auto-selected save {saveId}", saveId);
            }

            var crashsupport = CrashSupport.PrepareSupportFile(specificSave: obj);
            AdonisMessageBox.Show(LocalizationCodes.LC_ERROR_SAVE_LOAD_FAILED.Bind(crashsupport).Value, caption: "");
        }

        protected override void OnPropertyChanging(PropertyChangingEventArgs e)
        {
            base.OnPropertyChanging(e);

            if (e.PropertyName == nameof(Content))
            {
                // TODO - hacky workaround
                if (Content is SolverPage sp)
                {
                    var vm = sp.DataContext as SolverPageViewModel;
                    vm.Dispose();
                }
            }
        }

        [NotifyCanExecuteChangedFor(nameof(BeginNavigateSaveSelectionPageCommand))]
        [ObservableProperty]
        private FrameworkElement content;
    }
}
