namespace KillerPDF.Features;

/// <summary>A captured saved session held stable while the verified updater starts.</summary>
internal interface IUpdateRestartHandoff : System.IDisposable
{
    void Started();
    void Commit();
}

/// <summary>APP-OWNED capability of KillerPDF's About host: the unsaved-changes gate and the
/// document reopen the kit About core reaches through its update hooks.</summary>
internal interface IAboutUpdateRestart
{
    /// <summary>Blocks a self-update while any document window has unsaved changes.</summary>
    bool IsDirty { get; }

    /// <summary>Captures all saved documents for the next normal startup, or refuses an unstable session.</summary>
    IUpdateRestartHandoff? PrepareUpdateRestart();
}
