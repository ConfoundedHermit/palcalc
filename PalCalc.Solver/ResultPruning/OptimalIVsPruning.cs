using PalCalc.Solver.PalReference;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PalCalc.Solver.ResultPruning
{
    /// <param name="maxIvDifference">
    /// Given a pal with the highest IVs, other pals will only be kept if their IVs differ by at most this much.
    /// </param>
    public class OptimalIVsPruning(CancellationToken token, int maxIvDifference) : IResultPruning(token)
    {
        static int TotalIVs(IV_Set ivs) => ivs.RelevantMaxTotal;

        static int TotalIVs(IPalReference r) => TotalIVs(r.IVs);

        public override IEnumerable<IPalReference> Apply(IEnumerable<IPalReference> results, CachedResultData cachedData)
        {
            // note: all pals within a group being pruned should:
            //
            // - all have the same `IsRelevant` for each type of IV
            //   e.g. all HP will be relevant or all HP will be irrelevant
            //
            //   (would be enforced by grouping with `WorkingSet.DefaultGroupFn`)
            //
            // Unrequested IVs are retained for display, but must not influence which results
            // survive pruning. Compare only the highest values for requested IV categories.

            if (token.IsCancellationRequested) return results;

            // could multiply maxIvDifference by the number of relevant IV types, but this
            // just further prunes results, and I'd prefer to give later pruning steps an
            // opportunity to apply their pruning
            var bestValue = results.Max(TotalIVs);
            var threshold = bestValue - maxIvDifference * 3;

            return results.Where(p => TotalIVs(p.IVs) >= threshold);
        }
    }
}
