using PalCalc.Model;
using PalCalc.Solver.PalReference;
using PalCalc.Solver.Processing;
using PalCalc.Solver.ResultPruning;

namespace PalCalc.Solver.Tests;

[TestClass]
public class BreedingSolverOrchestrationTests
{
    private static readonly PalDB Db = PalDB.LoadEmbedded();

    [TestMethod]
    public void Solve_ReturnsOwnedTargetThatSatisfiesSpecifierAtZeroEffort()
    {
        var ownedTarget = CreateOwnedTarget();
        var specifier = new PalSpecifier { Pal = ownedTarget.Pal };

        var results = Solve(CreateSettings([ownedTarget], maxSolverIterations: 0), specifier);

        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(specifier.IsSatisfiedBy(results[0]));
        Assert.AreEqual(TimeSpan.Zero, results[0].BreedingEffort);
    }

    [TestMethod]
    public void Solve_TargetMinimumIV_AllowsHigherIV()
    {
        var ownedTarget = CreateOwnedTarget();
        ownedTarget.IV_HP = 90;
        var specifier = new PalSpecifier { Pal = ownedTarget.Pal, IV_HP = 80 };

        var results = Solve(CreateSettings([ownedTarget], maxSolverIterations: 0), specifier);

        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(specifier.IsSatisfiedBy(results[0]));
        Assert.AreEqual(90, results[0].IVs.HP.Min);
    }

    [TestMethod]
    public void Solve_TargetMinimumIV_PrefersHigherQualifyingIVOverLocation()
    {
        var attack80 = CreateOwnedTarget();
        attack80.IV_Shot = 80;
        attack80.Location = new PalLocation { Type = LocationType.Palbox, Index = 0 };

        var attack82 = CreateOwnedTarget();
        attack82.InstanceId = "solver-orchestration-owned-target-82-attack";
        attack82.IV_Shot = 82;
        attack82.Location = new PalLocation { Type = LocationType.Base, Index = 0 };

        var results = Solve(
            CreateSettings([attack80, attack82], maxSolverIterations: 0),
            new PalSpecifier { Pal = attack80.Pal, IV_Attack = 80 }
        );

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(82, results[0].IVs.Attack.Min);
    }

    [TestMethod]
    public void Solve_WhenAlreadyCancelled_ReportsCancellationWithoutBreedingIterations()
    {
        var ownedTarget = CreateOwnedTarget();
        var solver = new BreedingSolver();
        var statuses = new List<SolverStatus>();
        solver.StatusUpdated += status => statuses.Add(status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = solver.Solve(
            new BreedingSolverRequest(
                new PalSpecifier { Pal = ownedTarget.Pal },
                CreateSettings([ownedTarget], maxSolverIterations: 100)
            ),
            new SolverStateController(cancellation.Token)
        );

        Assert.IsTrue(result.IsCanceled);
        Assert.IsTrue(statuses.Count >= 2);
        Assert.AreEqual(SolverPhase.Initializing, statuses[0].CurrentPhase);
        Assert.IsTrue(statuses[0].IsCanceled);
        Assert.AreEqual(SolverPhase.Finished, statuses[^1].CurrentPhase);
        Assert.IsTrue(statuses[^1].IsCanceled);
        Assert.IsFalse(statuses.Any(status => status.CurrentPhase == SolverPhase.Breeding));
    }

    private static List<IPalReference> Solve(
        BreedingSolverSettings settings,
        PalSpecifier specifier
    ) =>
        new BreedingSolver()
            .Solve(
                new BreedingSolverRequest(specifier, settings),
                new SolverStateController(CancellationToken.None)
            )
            .Results
            .ToList();

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
        breedingDB: PalBreedingDB.LoadEmbedded(Db),
        gameSettings: GameSettings.Defaults,
        ownedPals: ownedPals,
        resultPruning: ResultPruningPolicy.Default,
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
