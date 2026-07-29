using PalCalc.Model;
using PalCalc.Solver.PalReference;
using PalCalc.Solver.PalReference.Properties;
using PalCalc.Solver.ResultPruning;

namespace PalCalc.Solver.Tests;

[TestClass]
public class OptimalIVsPruningTests
{
    private static readonly PalDB Db = PalDB.LoadEmbedded();

    [TestMethod]
    public void Apply_DoesNotDiscardHigherRequestedIVForBetterUnrequestedIVs()
    {
        var attack80WithBetterUnrequestedIvs = CreateReference(
            "attack-80",
            new IV_Set(
                HP: new IV_Value(false, 100, 100),
                Attack: new IV_Value(true, 80, 80),
                Defense: new IV_Value(false, 100, 100)));
        var attack82 = CreateReference(
            "attack-82",
            new IV_Set(
                HP: new IV_Value(false, 0, 0),
                Attack: new IV_Value(true, 82, 82),
                Defense: new IV_Value(false, 0, 0)));
        var references = new IPalReference[] { attack80WithBetterUnrequestedIvs, attack82 };

        var result = new OptimalIVsPruning(CancellationToken.None, maxIvDifference: 10)
            .Apply(references, new CachedResultData(references))
            .ToList();

        CollectionAssert.Contains(result, attack82);
    }

    private static OwnedPalReference CreateReference(string instanceId, IV_Set ivs) => new(
        new PalInstance
        {
            InstanceId = instanceId,
            OwnerPlayerId = instanceId,
            Pal = Db.Pals.First(),
            Gender = PalGender.MALE,
            Location = new PalLocation { Type = LocationType.Palbox, Index = 0 },
            PassiveSkills = [],
        },
        effectivePassives: [],
        effectiveIVs: ivs);
}
