using PalCalc.Solver.PalReference;
using System;
using System.Collections.Generic;

namespace PalCalc.UI.ViewModel.Solver
{
    /// <summary>
    /// The one terminal outcome produced by a queued solver job. Queue ownership is
    /// responsible for applying this result to UI state.
    /// </summary>
    public enum SolverJobOutcome
    {
        Completed,
        Cancelled,
        Failed,
    }

    public enum SolverJobLifecycleState
    {
        Queued,
        Running,
        Paused,
        Cancelling,
        Completed,
        Cancelled,
        Failed,
    }

    public sealed record SolverJobTerminalResult(
        SolverJobOutcome Outcome,
        List<IPalReference> Results,
        Exception Error = null
    );
}
