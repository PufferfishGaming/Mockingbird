using System.Windows.Threading;

namespace TriAsr.Ui.SmokeTests;

/// <summary>
/// Runs a test on a thread with a WPF dispatcher, the way the program's window does: every continuation after an <c>await</c> comes back to the same thread, which the
/// collection views behind the review lists insist on. (Without it the test host resumes on whichever thread is free and a view refuses the change.)
/// </summary>
internal static class UiThread
{
    public static Task RunAsync(Func<Task> body)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await body(); finished.SetResult(); }
                catch (Exception error) { finished.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return finished.Task;
    }
}
