using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GongSolutions.Wpf.DragDrop;
using PalCalc.UI.Localization;
using PalCalc.UI.ViewModel.Mapped;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PalCalc.UI.ViewModel.Solver
{
    internal sealed class SolverJobFinishedEventArgs(SolverJobViewModel job, SolverJobTerminalResult result) : EventArgs
    {
        public SolverJobViewModel Job { get; } = job;
        public SolverJobTerminalResult Result { get; } = result;
    }

    /// <summary>
    /// Owns queue membership and all queue-driven job state transitions. It is the
    /// only subscriber to a job's terminal task, so every job is removed exactly once.
    /// </summary>
    internal partial class SolverQueueViewModel : ObservableObject, IDropTarget
    {
        private static SolverQueueViewModel designInstance;
        public static SolverQueueViewModel DesignInstance
        {
            get
            {
                if (designInstance == null)
                {
                    designInstance = new SolverQueueViewModel(Dispatcher.CurrentDispatcher);
                    designInstance.Run(PalSpecifierViewModel.DesignerInstance);
                }

                return designInstance;
            }
        }

        private readonly Dispatcher dispatcher;
        private readonly ObservableCollection<PalSpecifierViewModel> orderedPendingTargets = [];
        private readonly Dictionary<PalSpecifierViewModel, SolverJobViewModel> itemJobs = new();
        private TaskCompletionSource queueDrained;

        public ReadOnlyObservableCollection<PalSpecifierViewModel> QueuedItems { get; }
        public event EventHandler<SolverJobFinishedEventArgs> JobFinished;

        private ILocalizedText sectionTitleWithCount;
        public ILocalizedText SectionTitleWithCount
        {
            get => sectionTitleWithCount;
            private set => SetProperty(ref sectionTitleWithCount, value);
        }

        [ObservableProperty]
        private IRelayCommand<PalSpecifierViewModel> selectItemCommand;

        public SolverQueueViewModel(Dispatcher dispatcher = null)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            QueuedItems = new ReadOnlyObservableCollection<PalSpecifierViewModel>(orderedPendingTargets);
            orderedPendingTargets.CollectionChanged += (_, _) =>
                SectionTitleWithCount = LocalizationCodes.LC_JOB_QUEUE_HEADER.Bind(QueuedItems.Count);
            SectionTitleWithCount = LocalizationCodes.LC_JOB_QUEUE_HEADER.Bind(0);
        }

        public void Run(PalSpecifierViewModel item)
        {
            VerifyDispatcherAccess();
            if (item?.LatestJob == null)
                throw new InvalidOperationException("Queued targets must have a solver job.");
            if (itemJobs.ContainsKey(item))
                return;

            itemJobs.Add(item, item.LatestJob);
            orderedPendingTargets.Insert(0, item);
            _ = ObserveTerminalResult(item, item.LatestJob);
            StartFirstRunnableJob();
        }

        public void CancelAll()
        {
            VerifyDispatcherAccess();
            foreach (var job in itemJobs.Values.Distinct().ToList())
                job.Cancel();
        }

        /// <summary>
        /// Requests cooperative cancellation and completes only after the queue has
        /// observed every terminal result and removed every entry on the UI dispatcher.
        /// </summary>
        public Task CancelAndWaitAsync()
        {
            VerifyDispatcherAccess();
            CancelAll();
            if (orderedPendingTargets.Count == 0)
                return Task.CompletedTask;

            queueDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return queueDrained.Task;
        }

        private async Task ObserveTerminalResult(PalSpecifierViewModel item, SolverJobViewModel job)
        {
            var terminalResult = await job.TerminalResult.ConfigureAwait(false);
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;

            _ = dispatcher.BeginInvoke(() => FinishJob(item, job, terminalResult), DispatcherPriority.Background);
        }

        private void FinishJob(PalSpecifierViewModel item, SolverJobViewModel job, SolverJobTerminalResult terminalResult)
        {
            VerifyDispatcherAccess();
            if (!itemJobs.TryGetValue(item, out var currentJob) || currentJob != job)
                return;

            job.ApplyTerminalResult(terminalResult);
            itemJobs.Remove(item);
            orderedPendingTargets.Remove(item);
            JobFinished?.Invoke(this, new SolverJobFinishedEventArgs(job, terminalResult));
            if (orderedPendingTargets.Count == 0)
                queueDrained?.TrySetResult();
            StartFirstRunnableJob();
        }

        private void StartFirstRunnableJob()
        {
            VerifyDispatcherAccess();
            var firstItem = orderedPendingTargets.FirstOrDefault(item => !itemJobs[item].IsTerminal);
            if (firstItem == null)
                return;

            foreach (var (item, job) in itemJobs)
            {
                if (item != firstItem && job.LifecycleState == SolverJobLifecycleState.Running)
                    job.Pause();
            }

            if (itemJobs[firstItem].LifecycleState is SolverJobLifecycleState.Queued or SolverJobLifecycleState.Paused)
                _ = itemJobs[firstItem].RunAsync();
        }

        public void DragOver(IDropInfo dropInfo)
        {
            if (!dropInfo.IsSameDragDropContextAsSource)
                return;

            var sourceItem = dropInfo.Data as PalSpecifierViewModel;
            var targetItem = dropInfo.TargetItem as PalSpecifierViewModel;
            if (sourceItem != null && targetItem != null && sourceItem != targetItem && !targetItem.IsReadOnly && QueuedItems.Contains(sourceItem))
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
                dropInfo.Effects = DragDropEffects.Move;
            }
        }

        public void Drop(IDropInfo dropInfo)
        {
            VerifyDispatcherAccess();
            var sourceItem = dropInfo.Data as PalSpecifierViewModel;
            var targetItem = dropInfo.TargetItem as PalSpecifierViewModel;
            if (!QueuedItems.Contains(sourceItem) || !QueuedItems.Contains(targetItem))
                return;

            var sourceIndex = QueuedItems.IndexOf(sourceItem);
            var targetIndex = QueuedItems.IndexOf(targetItem);
            var newIndex = dropInfo.InsertIndex;
            if (sourceIndex < targetIndex)
                newIndex--;
            if (sourceIndex == newIndex)
                return;

            orderedPendingTargets.Move(sourceIndex, Math.Clamp(newIndex, 0, QueuedItems.Count - 1));
            StartFirstRunnableJob();
        }

        private void VerifyDispatcherAccess()
        {
            if (!dispatcher.CheckAccess())
                throw new InvalidOperationException("Solver queue state must be changed on its owning dispatcher.");
        }
    }
}
