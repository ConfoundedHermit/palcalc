using CommunityToolkit.Mvvm.ComponentModel;
using PalCalc.Model;
using PalCalc.Solver;
using PalCalc.Solver.PalReference;
using PalCalc.Solver.ResultPruning;
using PalCalc.UI.Localization;
using PalCalc.UI.ViewModel.Mapped;
using QuickGraph;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PalCalc.UI.ViewModel.Solver
{
    internal delegate List<IPalReference> SolverJobRunner(PalSpecifier spec, SolverStateController controller);

    public class SolverJobViewModel : ObservableObject, IDisposable
    {
        private static readonly ILogger logger = Log.ForContext<SolverJobViewModel>();
        private const int ProgressUpdateIntervalMilliseconds = 100;

        private readonly Dispatcher dispatcher;
        private readonly SolverJobRunner runner;
        private readonly CancellationTokenSource tokenSource = new();
        private readonly SolverStateController solverController;
        private readonly TaskCompletionSource<SolverJobTerminalResult> terminalResultSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action unsubscribeProgress;
        private readonly Stopwatch stopwatch = new();

        private Task workerTask;
        private long lastProgressUpdateTimestamp;
        private int executionGeneration;
        private bool disposed;
        private int lastStepIndex = -1;

        public PalSpecifierViewModel Specifier { get; }

        private SolverState currentState;
        public SolverState CurrentState
        {
            get => currentState;
            private set
            {
                if (SetProperty(ref currentState, value))
                {
                    OnPropertyChanged(nameof(IsActive));
                    OnPropertyChanged(nameof(IsInactive));
                }
            }
        }

        private SolverJobLifecycleState lifecycleState;
        public SolverJobLifecycleState LifecycleState
        {
            get => lifecycleState;
            private set => SetProperty(ref lifecycleState, value);
        }

        private double solverProgress;
        public double SolverProgress
        {
            get => solverProgress;
            private set => SetProperty(ref solverProgress, value);
        }

        private double stepProgress;
        public double StepProgress
        {
            get => stepProgress;
            private set => SetProperty(ref stepProgress, value);
        }

        private ILocalizedText solverStatusMessage;
        public ILocalizedText SolverStatusMessage
        {
            get => solverStatusMessage;
            private set => SetProperty(ref solverStatusMessage, value);
        }

        private ILocalizedText stepStatusMessage;
        public ILocalizedText StepStatusMessage
        {
            get => stepStatusMessage;
            private set => SetProperty(ref stepStatusMessage, value);
        }

        public bool IsActive => LifecycleState is SolverJobLifecycleState.Queued
            or SolverJobLifecycleState.Running
            or SolverJobLifecycleState.Paused
            or SolverJobLifecycleState.Cancelling;

        public bool IsInactive => !IsActive;
        public bool IsTerminal => LifecycleState is SolverJobLifecycleState.Completed
            or SolverJobLifecycleState.Cancelled
            or SolverJobLifecycleState.Failed;

        public int SaveStateId { get; }
        public List<IPalReference> Results { get; private set; }
        public Task<SolverJobTerminalResult> TerminalResult => terminalResultSource.Task;

        public SolverJobViewModel(
            Dispatcher dispatcher,
            BreedingSolver solver,
            PalSpecifierViewModel spec,
            int saveStateId
        ) : this(dispatcher, spec, saveStateId, solver.SolveFor)
        {
            solver.SolverStateUpdated += OnSolverStateUpdated;
            unsubscribeProgress = () => solver.SolverStateUpdated -= OnSolverStateUpdated;
        }

        internal SolverJobViewModel(
            Dispatcher dispatcher,
            PalSpecifierViewModel spec,
            int saveStateId,
            SolverJobRunner runner
        )
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
            Specifier = spec ?? throw new ArgumentNullException(nameof(spec));
            SaveStateId = saveStateId;
            solverController = new SolverStateController { CancellationToken = tokenSource.Token };
            LifecycleState = SolverJobLifecycleState.Queued;
            CurrentState = SolverState.Paused;
        }

        /// <summary>
        /// Starts a queued job or resumes a paused one. The task always completes with
        /// one terminal result; it never exposes a worker exception to the UI.
        /// </summary>
        public Task<SolverJobTerminalResult> RunAsync()
        {
            VerifyDispatcherAccess();
            if (IsTerminal || LifecycleState == SolverJobLifecycleState.Cancelling)
                return TerminalResult;

            if (workerTask == null)
            {
                TransitionTo(SolverJobLifecycleState.Running);
                CurrentState = SolverState.Running;
                stopwatch.Start();
                var generation = ++executionGeneration;
                workerTask = Task.Run(() => RunWorker(generation));
            }
            else if (LifecycleState == SolverJobLifecycleState.Paused)
            {
                solverController.Resume();
                TransitionTo(SolverJobLifecycleState.Running);
                CurrentState = SolverState.Running;
            }

            return TerminalResult;
        }

        public void Run() => _ = RunAsync();

        public void Pause()
        {
            VerifyDispatcherAccess();
            if (workerTask == null || LifecycleState != SolverJobLifecycleState.Running)
                return;

            solverController.Pause();
            TransitionTo(SolverJobLifecycleState.Paused);
            CurrentState = SolverState.Paused;
        }

        public void Cancel()
        {
            VerifyDispatcherAccess();
            if (IsTerminal || LifecycleState == SolverJobLifecycleState.Cancelling)
                return;

            tokenSource.Cancel();
            solverController.Resume();

            if (workerTask == null)
            {
                TransitionTo(SolverJobLifecycleState.Cancelled);
                CurrentState = SolverState.Idle;
                terminalResultSource.TrySetResult(new(SolverJobOutcome.Cancelled, []));
                return;
            }

            TransitionTo(SolverJobLifecycleState.Cancelling);
        }

        /// <summary>
        /// Applies the terminal result on the owning UI dispatcher. Only
        /// SolverQueueViewModel calls this after confirming the job generation is current.
        /// </summary>
        internal void ApplyTerminalResult(SolverJobTerminalResult terminalResult)
        {
            VerifyDispatcherAccess();
            if (IsTerminal)
                return;

            switch (terminalResult.Outcome)
            {
                case SolverJobOutcome.Completed:
                    Results = terminalResult.Results;
                    TransitionTo(SolverJobLifecycleState.Completed);
                    break;
                case SolverJobOutcome.Cancelled:
                    TransitionTo(SolverJobLifecycleState.Cancelled);
                    break;
                case SolverJobOutcome.Failed:
                    TransitionTo(SolverJobLifecycleState.Failed);
                    break;
            }

            CurrentState = SolverState.Idle;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            unsubscribeProgress?.Invoke();
            Cancel();
            // Do not wait here: Dispose can run on the UI thread during shutdown.
        }

        private void RunWorker(int generation)
        {
            SolverJobTerminalResult terminalResult;
            try
            {
                tokenSource.Token.ThrowIfCancellationRequested();
                var results = runner(Specifier.ModelObject, solverController);
                tokenSource.Token.ThrowIfCancellationRequested();
                terminalResult = new(SolverJobOutcome.Completed, SimplifyResults(results));
            }
            catch (OperationCanceledException) when (tokenSource.IsCancellationRequested)
            {
                terminalResult = new(SolverJobOutcome.Cancelled, []);
            }
            catch (Exception error)
            {
                logger.Error(error, "Solver job failed for {TargetId}", Specifier.Id);
                terminalResult = new(SolverJobOutcome.Failed, [], error);
            }
            finally
            {
                stopwatch.Stop();
            }

            if (generation == Volatile.Read(ref executionGeneration))
                terminalResultSource.TrySetResult(terminalResult);
        }

        private List<IPalReference> SimplifyResults(List<IPalReference> results)
        {
            tokenSource.Token.ThrowIfCancellationRequested();
            var resultsTable = new PalPropertyGrouping(PalProperty.Combine(
                PalProperty.EffectivePassives,
                PalProperty.NumBreedingSteps,
                p => p.AllReferences().Select(r => r.Location.GetType()).Distinct().SetHash()
            ));
            resultsTable.AddRange(results);
            resultsTable.FilterAll(PruningRulesBuilder.Default, tokenSource.Token);

            tokenSource.Token.ThrowIfCancellationRequested();
            resultsTable = resultsTable.BuildNew(PalProperty.EffectivePassives);
            resultsTable.FilterAll(g =>
            {
                var nonZero = g.Where(r => r.BreedingEffort > TimeSpan.Zero).ToList();
                if (nonZero.Count == 0)
                    return g.Take(1);

                var fastest = nonZero.Min(r => r.BreedingEffort);
                return g.Where(r => r.BreedingEffort <= fastest * 2);
            }, tokenSource.Token);

            tokenSource.Token.ThrowIfCancellationRequested();
            return resultsTable.All.ToList();
        }

        private void OnSolverStateUpdated(SolverStatus status)
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished || IsTerminal)
                return;

            var now = Stopwatch.GetTimestamp();
            var minimumElapsed = Stopwatch.Frequency * ProgressUpdateIntervalMilliseconds / 1000;
            if (now - Interlocked.Read(ref lastProgressUpdateTimestamp) < minimumElapsed)
                return;

            Interlocked.Exchange(ref lastProgressUpdateTimestamp, now);
            var snapshot = new SolverStatus
            {
                CurrentPhase = status.CurrentPhase,
                CurrentStepIndex = status.CurrentStepIndex,
                TargetSteps = status.TargetSteps,
                Canceled = status.Canceled,
                Paused = status.Paused,
                CurrentWorkSize = status.CurrentWorkSize,
                WorkProcessedCount = status.WorkProcessedCount,
                TotalWorkProcessedCount = status.TotalWorkProcessedCount,
            };
            var generation = Volatile.Read(ref executionGeneration);

            dispatcher.BeginInvoke(() => ApplyProgress(snapshot, generation), DispatcherPriority.Background);
        }

        private void ApplyProgress(SolverStatus status, int generation)
        {
            if (generation != executionGeneration || IsTerminal || LifecycleState == SolverJobLifecycleState.Cancelling)
                return;

            string FormatNum(long num) => num.ToString("#,##");
            var numTotalSteps = (double)(1 + status.TargetSteps);
            var overallStep = 0;
            switch (status.CurrentPhase)
            {
                case SolverPhase.Initializing:
                    SolverStatusMessage = LocalizationCodes.LC_SOLVER_STATUS_INITIALIZING.Bind();
                    lastStepIndex = -1;
                    StepProgress = 0;
                    StepStatusMessage = null;
                    break;
                case SolverPhase.Breeding:
                    SolverStatusMessage = LocalizationCodes.LC_SOLVER_STATUS_BREEDING.Bind(new
                    {
                        StepNum = status.CurrentStepIndex + 1,
                        WorkSize = FormatNum(status.CurrentWorkSize),
                    });
                    overallStep = 1 + status.CurrentStepIndex;
                    StepProgress = status.CurrentWorkSize == 0 ? 0 : 100 * status.WorkProcessedCount / status.CurrentWorkSize;
                    StepStatusMessage = LocalizationCodes.LC_SOLVER_STEP_STATUS_BREEDING.Bind(new
                    {
                        NumProcessed = FormatNum(status.WorkProcessedCount),
                        WorkSize = FormatNum(status.CurrentWorkSize),
                    });
                    lastStepIndex = status.CurrentStepIndex;
                    break;
                case SolverPhase.Finished:
                    if (!status.Canceled)
                    {
                        SolverStatusMessage = LocalizationCodes.LC_SOLVER_STATUS_FINISHED.Bind(stopwatch.Elapsed.TimeSpanSecondsStr());
                        overallStep = (int)numTotalSteps;
                        StepProgress = 100;
                        StepStatusMessage = LocalizationCodes.LC_SOLVER_STEP_STATUS_DONE.Bind(FormatNum(status.TotalWorkProcessedCount));
                    }
                    break;
            }

            SolverProgress = 100 * overallStep / numTotalSteps;
        }

        private void TransitionTo(SolverJobLifecycleState next)
        {
            if (LifecycleState == next)
                return;

            var legal = LifecycleState switch
            {
                SolverJobLifecycleState.Queued => next is SolverJobLifecycleState.Running or SolverJobLifecycleState.Cancelled,
                SolverJobLifecycleState.Running => next is SolverJobLifecycleState.Paused or SolverJobLifecycleState.Cancelling or SolverJobLifecycleState.Completed or SolverJobLifecycleState.Cancelled or SolverJobLifecycleState.Failed,
                SolverJobLifecycleState.Paused => next is SolverJobLifecycleState.Running or SolverJobLifecycleState.Cancelling or SolverJobLifecycleState.Cancelled,
                SolverJobLifecycleState.Cancelling => next is SolverJobLifecycleState.Cancelled or SolverJobLifecycleState.Failed,
                _ => false,
            };

            if (!legal)
                throw new InvalidOperationException($"Illegal solver job transition: {LifecycleState} -> {next}");

            LifecycleState = next;
        }

        private void VerifyDispatcherAccess()
        {
            if (!dispatcher.CheckAccess())
                throw new InvalidOperationException("Solver job state must be changed on its owning dispatcher.");
        }
    }
}
