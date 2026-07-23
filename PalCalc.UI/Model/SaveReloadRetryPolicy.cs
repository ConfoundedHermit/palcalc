using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;

namespace PalCalc.UI.Model;

/// <summary>
/// Retries a save read while a game or sync client may still be replacing its files.
/// The caller owns rollback; this policy only delays and re-attempts the candidate read.
/// </summary>
internal sealed class SaveReloadRetryPolicy
{
    private static readonly ILogger logger = Log.ForContext<SaveReloadRetryPolicy>();

    public static readonly IReadOnlyList<TimeSpan> Delays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private readonly Action<TimeSpan> wait;

    public SaveReloadRetryPolicy(Action<TimeSpan> wait = null)
    {
        this.wait = wait ?? Thread.Sleep;
    }

    public T Execute<T>(Func<T> attempt)
    {
        for (var retryIndex = 0; ; retryIndex++)
        {
            try
            {
                return attempt();
            }
            catch (Exception ex) when (retryIndex < Delays.Count)
            {
                var delay = Delays[retryIndex];
                logger.Warning(
                    ex,
                    "Save reload attempt {attempt} failed; retrying after {delayMs}ms",
                    retryIndex + 1,
                    delay.TotalMilliseconds);
                wait(delay);
            }
        }
    }
}
