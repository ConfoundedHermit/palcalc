using AdonisUI.Controls;
using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PalCalc.UI.View
{
    public partial class SolverCancellationModal : AdonisWindow
    {
        public SolverCancellationModal()
        {
            InitializeComponent();
        }

        public void ShowDialogUntil(Task completion)
        {
            ArgumentNullException.ThrowIfNull(completion);
            _ = CloseWhenCompleteAsync(completion);
            ShowDialog();
        }

        private async Task CloseWhenCompleteAsync(Task completion)
        {
            await completion.ConfigureAwait(false);
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Background);
        }
    }
}
