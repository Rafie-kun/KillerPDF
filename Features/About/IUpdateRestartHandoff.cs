namespace KillerPDF.Features;

/// <summary>A captured saved session held stable while the verified updater starts.</summary>
internal interface IUpdateRestartHandoff : System.IDisposable
{
    void Started();
    void Commit();
}
