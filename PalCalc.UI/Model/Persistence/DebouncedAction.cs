using System;
using System.Windows.Threading;

namespace PalCalc.UI.Model.Persistence
{
    /// <summary>
    /// Coalesces repeated requests into one final action. Flush executes synchronously so callers
    /// can use it during shutdown without silently dropping the most recent request.
    /// </summary>
    internal sealed class DebouncedAction : IDisposable
    {
        private readonly Action action;
        private readonly DispatcherTimer timer;
        private bool pending;
        private bool disposed;

        public DebouncedAction(Dispatcher dispatcher, TimeSpan delay, Action action)
        {
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
            this.action = action ?? throw new ArgumentNullException(nameof(action));
            timer = new DispatcherTimer(delay, DispatcherPriority.Background, OnTimerTick, dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)));
        }

        public void Schedule()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending = true;
            timer.Stop();
            timer.Start();
        }

        public void Flush()
        {
            if (disposed || !pending) return;
            pending = false;
            timer.Stop();
            action();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            pending = false;
            timer.Stop();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (disposed || !pending) return;
            pending = false;
            timer.Stop();
            action();
        }
    }
}
