using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PalCalc.UI.Model.Persistence
{
    /// <summary>
    /// Serializes work for one normalized path without blocking work for unrelated paths.
    /// </summary>
    internal sealed class KeyedWriteCoordinator
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.OrdinalIgnoreCase);

        public async Task RunAsync(string path, Action action)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(action);

            var key = System.IO.Path.GetFullPath(path);
            var gate = gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                action();
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
