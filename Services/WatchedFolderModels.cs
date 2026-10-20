namespace KillerPDF.Services;

public sealed record WatchedFolderOptions(string InputFolder, string OutputFolder,
    string FilePattern, string MacroJson, bool PauseOnError = false);

public enum WatchedFolderJobStatus { Waiting, Processing, Completed, Failed }

public sealed record WatchedFolderJob(Guid Id, string InputPath, string OutputPath,
    WatchedFolderJobStatus Status, int Attempts, string? ErrorCode);

public sealed record WatchedFolderSnapshot(WatchedFolderOptions? Options, bool IsPaused,
    IReadOnlyList<WatchedFolderJob> Jobs, string? ErrorCode);
