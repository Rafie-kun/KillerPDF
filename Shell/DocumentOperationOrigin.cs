namespace KillerPDF;

public partial class MainWindow
{
    private Func<bool> CaptureDocumentOrigin()
    {
        var viewer = ActiveViewer;
        var session = viewer.ActiveSessionExt;
        var document = _doc;
        return () => ReferenceEquals(ActiveViewer, viewer)
            && ReferenceEquals(viewer.ActiveSessionExt, session)
            && ReferenceEquals(_doc, document);
    }
}
