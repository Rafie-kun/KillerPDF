using KillerPDF.Controls;

namespace KillerPDF;

public partial class MainWindow
{
    private void OpenCertificateCatalog()
    {
        var dialog = new CertificateCatalogDialog(this);
        dialog.ShowDialog();
        RefreshSidebarPanel();
    }
}
