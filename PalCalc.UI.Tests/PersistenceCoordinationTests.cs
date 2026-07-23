using PalCalc.UI.Model.Persistence;
using System.Windows.Threading;

namespace PalCalc.UI.Tests;

[TestClass]
public class PersistenceCoordinationTests
{
    [TestMethod]
    public async Task KeyedCoordinator_SerializesSamePath()
    {
        var coordinator = new KeyedWriteCoordinator();
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var executionOrder = new List<int>();

        var first = Task.Run(() => coordinator.RunAsync("same.json", () =>
        {
            executionOrder.Add(1);
            firstEntered.Set();
            releaseFirst.Wait();
        }));
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(2)));

        var second = Task.Run(() => coordinator.RunAsync("same.json", () => executionOrder.Add(2)));
        await Task.Delay(100);
        CollectionAssert.AreEqual(new[] { 1 }, executionOrder);

        releaseFirst.Set();
        await Task.WhenAll(first, second);
        CollectionAssert.AreEqual(new[] { 1, 2 }, executionOrder);
    }

    [TestMethod]
    public async Task KeyedCoordinator_AllowsDifferentPathsToProceed()
    {
        var coordinator = new KeyedWriteCoordinator();
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var first = Task.Run(() => coordinator.RunAsync("one.json", () =>
        {
            firstEntered.Set();
            release.Wait();
        }));
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(2)));

        var second = Task.Run(() => coordinator.RunAsync("two.json", () => secondEntered.Set()));
        Assert.IsTrue(secondEntered.Wait(TimeSpan.FromSeconds(2)));

        release.Set();
        await Task.WhenAll(first, second);
    }

    [TestMethod]
    public void DebouncedAction_CoalescesRequestsAndFlushesPendingWork()
    {
        var runs = 0;
        using var action = new DebouncedAction(Dispatcher.CurrentDispatcher, TimeSpan.FromSeconds(10), () => Interlocked.Increment(ref runs));

        action.Schedule();
        action.Schedule();
        action.Schedule();
        action.Flush();

        Assert.AreEqual(1, runs);
    }
}
