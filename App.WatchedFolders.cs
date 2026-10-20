using System.IO;
using System.Threading;
using KillerPDF.Services;

namespace KillerPDF;

public partial class App
{
    private static WatchedFolderService? _folderAutomation;
    private static CancellationTokenSource? _folderAutomationCancellation;
    private static Task? _folderAutomationWorker;
    private static string FolderAutomationDirectory => Path.Combine(AppDataPaths.UserRoot, "automatic-folders");

    internal static WatchedFolderService GetFolderAutomation()
    {
        if (_folderAutomation is not null) return _folderAutomation;
        _folderAutomation = new WatchedFolderService(FolderAutomationDirectory,
            WatchedFolderHost.CreateDispatcher(FolderAutomationDirectory));
        _folderAutomationCancellation = new CancellationTokenSource();
        WatchedFolderService service = _folderAutomation;
        CancellationToken token = _folderAutomationCancellation.Token;
        _folderAutomationWorker = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try { await service.ReconcileAsync(token).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        try { service.Pause(); }
                        catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { service.Dispose(); }
        });
        return service;
    }

    private static void StartFolderAutomationIfConfigured()
    {
        if (!Directory.Exists(FolderAutomationDirectory)) return;
        try { _ = GetFolderAutomation(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { /* The setup dialog reports unavailable storage without interrupting document opening. */ }
    }

    private static bool StopFolderAutomation()
    {
        _folderAutomationCancellation?.Cancel();
        try
        {
            if (_folderAutomationWorker is not null && !_folderAutomationWorker.Wait(TimeSpan.FromSeconds(5)))
                return false;
        }
        catch (AggregateException) { }
        _folderAutomation?.Dispose();
        _folderAutomationCancellation?.Dispose();
        _folderAutomation = null;
        _folderAutomationCancellation = null;
        _folderAutomationWorker = null;
        return true;
    }
}
