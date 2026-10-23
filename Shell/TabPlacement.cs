using KillerPDF.Controls;
using KillerPDF.Features;

namespace KillerPDF;

public partial class MainWindow
{
    private void InitTabPlacement()
        => SetTabPlacement(App.GetSetting("TabsAtBottom") == "1", persist: false);

    private void SetTabPlacement(bool bottom, bool persist = true)
    {
        Viewer.SetTabPlacement(bottom);
        ViewerB.SetTabPlacement(bottom);
        if (persist) App.SetSetting("TabsAtBottom", bottom ? "1" : "0");
    }

    void IViewerHost.SetTabPlacement(bool bottom) => SetTabPlacement(bottom);

    void IViewerHost.NewDocumentTab(PdfViewer viewer)
    {
        FocusPane(viewer);
        NewDocument();
    }
}
