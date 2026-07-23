using PalCalc.Model;
using PalCalc.Solver.ResultPruning;

namespace PalCalc.Solver.Tests;

[TestClass]
public class BreedingSolverOrchestrationTests
{
    private static readonly PalDB Db = PalDB.LoadEmbedded();

    [TestMethod]
    public void SolveFor_ReturnsOwnedTargetThatSatisfiesSpecifierAtZeroEffort()
    {
        var ownedTarget = CreateOwnedTarget();
        var specifier = new PalSpecifier { Pal = ownedTarget.Pal };
        var solver = new BreedingSolver(CreateSettings([ownedTarget], maxSolverIterations: 0));

        var results = solver.SolveFor(specifier, new SolverStateController());

        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(specifier.IsSatisfiedBy(results[0]));
        Assert.AreEqual(TimeSpan.Zero, results[0].BreedingEffort);
    }

    [TestMethod]
    public void SolveFor_WhenAlreadyCancelled_ReportsCancellationWithoutBreedingIterations()
    {
        var ownedTarget = CreateOwnedTarget();
        var solver = new BreedingSolver(CreateSettings([ownedTarget], maxSolverIterations: 100));
        var statuses = new List<SolverStatus>();
        solver.SolverStateUpdated += status => statuses.Add(new SolverStatus
        {
            CurrentPhase = status.CurrentPhase,
            Canceled = status.Canceled,
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        solver.SolveFor(
            new PalSpecifier { Pal = ownedTarget.Pal },
            new SolverStateController { CancellationToken = cancellation.Token });

        Assert.IsTrue(statuses.Count >= 2);
        Assert.AreEqual(SolverPhase.Initializing, statuses[0].CurrentPhase);
        Assert.IsTrue(statuses[0].Canceled);
        Assert.AreEqual(SolverPhase.Finished, statuses[^1].CurrentPhase);
        Assert.IsTrue(statuses[^1].Canceled);
        Assert.IsFalse(statuses.Any(status => status.CurrentPhase == SolverPhase.Breeding));
    }

    private static PalInstance CreateOwnedTarget() => new()
    {
        InstanceId = "solver-orchestration-owned-target",
        OwnerPlayerId = "solver-orchestration-owner",
        Pal = Db.Pals.First(),
        Gender = PalGender.MALE,
        Location = new PalLocation { Type = LocationType.Palbox, Index = 0 },
        PassiveSkills = [],
    };

    private static BreedingSolverSettings CreateSettings(List<PalInstance> ownedPals, int maxSolverIterations) => new(
        db: Db,
        gameSettings: GameSettings.Defaults,
        ownedPals: ownedPals,
        pruningBuilder: PruningRulesBuilder.Default,
        maxBreedingSteps: 0,
        maxSolverIterations: maxSolverIterations,
        maxWildPals: 0,
        allowedWildPals: [],
        bannedBredPals: [],
        maxInputIrrelevantPassives: 0,
        maxBredIrrelevantPassives: 0,
        maxEffort: TimeSpan.Zero,
        maxThreads: 1,
        maxSurgeryCost: 0,
        allowedSurgeryPassives: [],
        useGenderReversers: false);
}
