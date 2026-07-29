using PalCalc.Solver;
using PalCalc.Solver.PalReference;
using PalCalc.UI.ViewModel.Mapped;
using PalCalc.UI.ViewModel.Solver;
using System.Diagnostics;
using System.Windows.Threading;

namespace PalCalc.UI.Tests;

[TestClass]
[DoNotParallelize]
public class SolverJobLifecycleTests
{
    [TestMethod]
    public async Task QueuedJob_CancelIsIdempotentAndEmitsOneCancelledResult()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var job = NewJob(dispatcher, (_, _) => throw new AssertFailedException("A cancelled queued job must not start."));

        job.Cancel();
        job.Cancel();
        var result = await job.TerminalResult;

        Assert.AreEqual(SolverJobOutcome.Cancelled, result.Outcome);
        Assert.AreEqual(SolverJobLifecycleState.Cancelled, job.LifecycleState);
        Assert.AreEqual(SolverState.Idle, job.CurrentState);
    }

    [TestMethod]
    public void RunningPausedJob_CancelResumesWorkerAndCompletesCancelled()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var entered = new ManualResetEventSlim();
        using var paused = new ManualResetEventSlim();
        var job = NewJob(dispatcher, (_, controller) =>
        {
            entered.Set();
            if (!SpinWait.SpinUntil(() => controller.IsPaused, TimeSpan.FromSeconds(2)))
                throw new TimeoutException("The fake solver was not paused.");

            paused.Set();
            if (!SpinWait.SpinUntil(() => !controller.IsPaused, TimeSpan.FromSeconds(2)))
                throw new TimeoutException("The fake solver was not resumed for cancellation.");

            controller.CancellationToken.ThrowIfCancellationRequested();
            return [];
        });

        _ = job.RunAsync();
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
        job.Pause();
        Assert.IsTrue(paused.Wait(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(SolverJobLifecycleState.Paused, job.LifecycleState);

        job.Cancel();
        Assert.IsTrue(job.TerminalResult.Wait(TimeSpan.FromSeconds(2)));
        var result = job.TerminalResult.Result;
        job.ApplyTerminalResult(result);

        Assert.AreEqual(SolverJobOutcome.Cancelled, result.Outcome);
        Assert.AreEqual(SolverJobLifecycleState.Cancelled, job.LifecycleState);
    }

    [TestMethod]
    public void FailedRunner_ProducesOneObservedFailedResult()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var job = NewJob(dispatcher, (_, _) => throw new InvalidOperationException("test failure"));

        _ = job.RunAsync();
        Assert.IsTrue(job.TerminalResult.Wait(TimeSpan.FromSeconds(2)));
        var result = job.TerminalResult.Result;
        job.ApplyTerminalResult(result);

        Assert.AreEqual(SolverJobOutcome.Failed, result.Outcome);
        Assert.IsInstanceOfType<InvalidOperationException>(result.Error);
        Assert.AreEqual(SolverJobLifecycleState.Failed, job.LifecycleState);
    }

    [TestMethod]
    public void Queue_AdvancesThroughOneTerminalPathPerJob()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var firstStarted = new ManualResetEventSlim();
        var first = NewJob(dispatcher, (_, controller) =>
        {
            firstStarted.Set();
            controller.CancellationToken.WaitHandle.WaitOne();
            controller.CancellationToken.ThrowIfCancellationRequested();
            return [];
        });
        var second = NewJob(dispatcher, (_, _) => []);
        var firstItem = first.Specifier;
        var secondItem = second.Specifier;
        firstItem.LatestJob = first;
        secondItem.LatestJob = second;

        var queue = new SolverQueueViewModel(dispatcher);
        var outcomes = new List<SolverJobOutcome>();
        queue.JobFinished += (_, result) => outcomes.Add(result.Result.Outcome);

        queue.Run(firstItem);
        Assert.IsTrue(firstStarted.Wait(TimeSpan.FromSeconds(2)));
        queue.Run(secondItem);
        PumpUntil(dispatcher, () => outcomes.Count == 1);

        var cancellationComplete = queue.CancelAndWaitAsync();
        PumpUntil(dispatcher, () => cancellationComplete.IsCompleted && outcomes.Count == 2 && queue.QueuedItems.Count == 0);

        CollectionAssert.AreEqual(new[] { SolverJobOutcome.Completed, SolverJobOutcome.Cancelled }, outcomes);
        Assert.IsTrue(cancellationComplete.IsCompletedSuccessfully);
    }

    private static SolverJobViewModel NewJob(Dispatcher dispatcher, SolverJobRunner runner) =>
        new(dispatcher, new PalSpecifierViewModel(Guid.NewGuid().ToString(), null), 0, runner);

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            var frame = new DispatcherFrame();
            _ = dispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.Background);
            Dispatcher.PushFrame(frame);
        }

        Assert.IsTrue(condition(), "The dispatcher did not reach the expected solver queue state.");
    }
}
