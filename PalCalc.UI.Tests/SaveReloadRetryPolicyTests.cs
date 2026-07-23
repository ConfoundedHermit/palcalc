using PalCalc.UI.Model;

namespace PalCalc.UI.Tests;

[TestClass]
public class SaveReloadRetryPolicyTests
{
    [TestMethod]
    public void Execute_RetriesWithTheApprovedBackoffAndReturnsTheFirstSuccess()
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var policy = new SaveReloadRetryPolicy(delays.Add);

        var result = policy.Execute(() =>
        {
            attempts++;
            if (attempts < 3)
                throw new InvalidDataException("save is being written");
            return "loaded";
        });

        Assert.AreEqual("loaded", result);
        Assert.AreEqual(3, attempts);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1) },
            delays);
    }

    [TestMethod]
    public void Execute_ThrowsTheFinalFailureAfterAllApprovedRetries()
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var policy = new SaveReloadRetryPolicy(delays.Add);

        var exception = Assert.ThrowsException<InvalidDataException>(() => policy.Execute<object>(() =>
        {
            attempts++;
            throw new InvalidDataException("partial save");
        }));

        Assert.AreEqual("partial save", exception.Message);
        Assert.AreEqual(5, attempts);
        CollectionAssert.AreEqual(
            new[]
            {
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
            },
            delays);
    }
}
