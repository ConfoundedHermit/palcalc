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
    public void SolveFor_TargetMinimumIV_AllowsHigherIV()
    {
        var ownedTarget = CreateOwnedTarget();
        ownedTarget.IV_HP = 90;
        var specifier = new PalSpecifier { Pal = ownedTarget.Pal, IV_HP = 80 };
        var solver = new BreedingSolver(CreateSettings([ownedTarget], maxSolverIterations: 0));

        var results = solver.SolveFor(specifier, new SolverStateController());

        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(specifier.IsSatisfiedBy(results[0]));
        Assert.AreEqual(90, results[0].IVs.HP.Min);
    }

    [TestMethod]
    public void SolveFor_TargetMinimumIV_PrefersHigherQualifyingIVOverLocation()
    {
        var attack80 = CreateOwnedTarget();
        attack80.IV_Shot = 80;
        attack80.Location = new PalLocation { Type = LocationType.Palbox, Index = 0 };

        var attack82 = CreateOwnedTarget();
        attack82.InstanceId = "solver-orchestration-owned-target-82-attack";
        attack82.IV_Shot = 82;
        attack82.Location = new PalLocation { Type = LocationType.Base, Index = 0 };

        var solver = new BreedingSolver(CreateSettings([attack80, attack82], maxSolverIterations: 0));

        var results = solver.SolveFor(
            new PalSpecifier { Pal = attack80.Pal, IV_Attack = 80 },
            new SolverStateController());

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(82, results[0].IVs.Attack.Min);
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
