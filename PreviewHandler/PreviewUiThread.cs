using System.Windows.Threading;

namespace KillerPDF.PreviewHandler;

internal static class PreviewUiThread
{
    private static readonly Lazy<Dispatcher> Owner = new(Start);

    internal static void Invoke(Action action)
    {
        Dispatcher dispatcher = Owner.Value;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    internal static T Invoke<T>(Func<T> action)
    {
        Dispatcher dispatcher = Owner.Value;
        return dispatcher.CheckAccess() ? action() : dispatcher.Invoke(action);
    }

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(DispatcherPriority.Send, () => ready.TrySetResult(dispatcher));
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "KillerPDF preview UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
