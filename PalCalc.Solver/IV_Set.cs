using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PalCalc.Solver
{
    public readonly record struct IV_Set(IV_Value HP, IV_Value Attack, IV_Value Defense)
    {
        public int RelevantMaxTotal =>
            (HP.IsRelevant ? HP.Max : 0) +
            (Attack.IsRelevant ? Attack.Max : 0) +
            (Defense.IsRelevant ? Defense.Max : 0);

        public int RelevantMinTotal =>
            (HP.IsRelevant ? HP.Min : 0) +
            (Attack.IsRelevant ? Attack.Min : 0) +
            (Defense.IsRelevant ? Defense.Min : 0);
    }
}
