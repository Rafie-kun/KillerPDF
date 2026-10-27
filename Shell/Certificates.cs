using KillerPDF.Controls;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private void OpenCertificateCatalog()
    {
        var dialog = new CertificateCatalogDialog(this);
        dialog.ShowDialog();
        RefreshSidebarPanel();
    }
}
