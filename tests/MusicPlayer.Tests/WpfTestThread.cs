using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MusicPlayer.Tests;

internal static class WpfTestThread
{
    public static Task Run(Func<Task> scenario)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await scenario();
                    completed.SetResult();
                }
                catch (Exception error) { completed.SetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
